using System.Diagnostics;
using System.IO;

namespace VibeNotch;

public sealed record SessionFacts(string GitBranch, bool GitDirty, string Model, double ContextRatio, bool HasContext);

/// <summary>
/// Collects the "how is this session doing" facts Qoder only exposes indirectly:
/// git branch/dirtiness comes from a throttled `git status`, context usage and
/// model come from the tail of the session transcript. Everything is cached and
/// runs off the UI thread — a hook burst must never spawn git once per event.
/// </summary>
public static class Enrich
{
    private sealed record GitFacts(string Branch, bool Dirty, DateTime At);

    private static readonly Dictionary<string, GitFacts> GitCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, TranscriptFacts> TranscriptCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    private sealed record TranscriptFacts(string Model, double Ratio, bool HasContext, long Length, DateTime WriteTime);

    private static readonly TimeSpan GitTtl = TimeSpan.FromSeconds(20);
    private const int TranscriptTailBytes = 192 * 1024;

    public static List<(SessionVm Session, SessionFacts Facts)> Collect(IReadOnlyList<SessionVm> sessions)
    {
        var results = new List<(SessionVm, SessionFacts)>(sessions.Count);

        foreach (var session in sessions)
        {
            var git = session.Cwd.Length > 0 ? ReadGit(session.Cwd) : null;
            var transcript = session.TranscriptPath.Length > 0 ? ReadTranscript(session.TranscriptPath) : null;

            results.Add((session, new SessionFacts(
                git?.Branch ?? "",
                git?.Dirty ?? false,
                transcript?.Model ?? "",
                transcript?.Ratio ?? 0,
                transcript?.HasContext ?? false)));
        }

        return results;
    }

    private static GitFacts? ReadGit(string cwd)
    {
        var now = DateTime.Now;
        lock (Gate)
        {
            if (GitCache.TryGetValue(cwd, out var cached) && now - cached.At < GitTtl) return cached;
        }

        var facts = QueryGit(cwd) ?? new GitFacts("", false, now);

        lock (Gate)
        {
            GitCache[cwd] = facts;
        }

        return facts;
    }

    private static GitFacts? QueryGit(string cwd)
    {
        try
        {
            if (!Directory.Exists(cwd)) return null;

            var start = new ProcessStartInfo("git")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = cwd
            };
            start.ArgumentList.Add("status");
            start.ArgumentList.Add("--porcelain=v1");
            start.ArgumentList.Add("--branch");

            using var process = Process.Start(start);
            if (process == null) return null;

            // ReadToEnd would block forever on a hung git; drain asynchronously
            // and give the process a hard deadline instead.
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(1500))
            {
                try { process.Kill(true); } catch { /* best effort */ }
                return null;
            }

            _ = errorTask.Result;
            var output = outputTask.Result;

            if (process.ExitCode != 0) return new GitFacts("", false, DateTime.Now);

            string branch = "";
            var dirty = false;

            foreach (var line in output.Split('\n'))
            {
                if (line.StartsWith("## ", StringComparison.Ordinal))
                {
                    var name = line[3..];
                    var upstream = name.IndexOf("...", StringComparison.Ordinal);
                    if (upstream > 0) name = name[..upstream];
                    else name = name.Split(' ')[0];
                    branch = name.StartsWith("HEAD", StringComparison.OrdinalIgnoreCase) ? "detached" : name;
                }
                else if (line.Trim().Length > 0)
                {
                    dirty = true;
                }
            }

            return new GitFacts(branch, dirty, DateTime.Now);
        }
        catch
        {
            // git missing or refused to run: treat as no repository
            return new GitFacts("", false, DateTime.Now);
        }
    }

    private static TranscriptFacts? ReadTranscript(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            lock (Gate)
            {
                if (TranscriptCache.TryGetValue(path, out var cached) &&
                    cached.Length == info.Length && cached.WriteTime == info.LastWriteTime)
                {
                    return cached;
                }
            }

            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var start = Math.Max(0, length - TranscriptTailBytes);
            stream.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[length - start];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

            var model = "";
            double ratio = 0;
            var hasContext = false;

            const string modelKey = "\"model\":\"";
            const string ratioKey = "\"context_usage_ratio\":";

            var modelAt = text.LastIndexOf(modelKey, StringComparison.Ordinal);
            if (modelAt >= 0)
            {
                var begin = modelAt + modelKey.Length;
                var end = text.IndexOf('"', begin);
                if (end > begin) model = text[begin..end];
            }

            var ratioAt = text.LastIndexOf(ratioKey, StringComparison.Ordinal);
            if (ratioAt >= 0)
            {
                var begin = ratioAt + ratioKey.Length;
                var end = begin;
                while (end < text.Length && (char.IsDigit(text[end]) || text[end] == '.')) end++;
                if (end > begin && double.TryParse(text[begin..end], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out var value))
                {
                    ratio = value;
                    hasContext = true;
                }
            }

            var facts = new TranscriptFacts(model, ratio, hasContext, info.Length, info.LastWriteTime);

            lock (Gate)
            {
                TranscriptCache[path] = facts;
            }

            return facts;
        }
        catch
        {
            return null;
        }
    }
}
