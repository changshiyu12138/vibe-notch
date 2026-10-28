using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeNotch.Hook;

internal static class Program
{
    private const string PipeName = "vibe-notch";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(150);
    private static readonly TimeSpan PermissionTimeout = TimeSpan.FromSeconds(280);

    private static int Main()
    {
        try
        {
            var raw = ReadStdin();
            if (string.IsNullOrWhiteSpace(raw)) return 0;

            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var eventName = Str(root, "hook_event_name");
            var toolName = Str(root, "tool_name");

            // AskUserQuestion never raises PermissionRequest; the only chance to
            // answer it from the island is a synchronous PreToolUse decision.
            var interactive = eventName == "PermissionRequest"
                || (eventName == "PreToolUse" && toolName == "AskUserQuestion");

            string? requestId = interactive ? Guid.NewGuid().ToString("N") : null;

            var payload = new JsonObject
            {
                ["event"] = eventName,
                ["session_id"] = Str(root, "session_id"),
                ["cwd"] = Str(root, "cwd"),
                ["transcript_path"] = Str(root, "transcript_path"),
                ["permission_mode"] = Str(root, "permission_mode"),
                ["tool_name"] = toolName,
                ["tool_input"] = Node(root, "tool_input"),
                ["tool_use_id"] = Str(root, "tool_use_id"),
                ["notification_type"] = Str(root, "notification_type"),
                ["message"] = Str(root, "message"),
                ["last_assistant_message"] = Str(root, "last_assistant_message"),
                ["error"] = Str(root, "error"),
                ["request_id"] = requestId,
                ["hook_pid"] = Environment.ProcessId,
                ["at"] = DateTimeOffset.Now.ToUnixTimeMilliseconds()
            };

            Log($"{eventName} tool={toolName ?? "-"} session={(payload["session_id"]?.GetValue<string>() is { } sid ? sid[..Math.Min(8, sid.Length)] : "-")}");

            var response = Send(payload.ToJsonString(), waitForResponse: requestId != null);
            if (requestId == null || response == null) return 0;

            var decision = response["decision"]?.GetValue<string>();
            if (decision is not ("allow" or "deny")) return 0;

            var outputFormat = response["output"]?.GetValue<string>() ?? "qoder";
            string? reason = decision == "deny"
                ? response["reason"]?.GetValue<string>() ?? "Denied from Vibe Notch"
                : null;
            var updatedInput = response["updatedInput"] as JsonObject;

            var outObj = outputFormat is "claude" or "cli"
                ? BuildCliOutput(eventName!, decision, reason, updatedInput)
                : BuildIdeOutput(eventName!, decision, reason, updatedInput);

            Console.Out.Write(outObj.ToJsonString());
            Console.Out.Flush();
            Log($"permission -> {decision} ({outputFormat})");
        }
        catch (Exception ex)
        {
            Log("error " + ex.GetType().Name + ": " + ex.Message);
        }

        return 0;
    }

    // Qoder IDE / JetBrains plugin: hookSpecificOutput.permissionDecision.
    private static JsonObject BuildIdeOutput(string eventName, string decision, string? reason, JsonObject? updatedInput)
    {
        var specific = new JsonObject
        {
            ["hookEventName"] = eventName,
            ["permissionDecision"] = decision
        };
        if (reason != null) specific["permissionDecisionReason"] = reason;
        if (updatedInput != null) specific["updatedInput"] = updatedInput.DeepClone();
        return new JsonObject { ["hookSpecificOutput"] = specific };
    }

    // Qoder CLI / Claude-compatible CLI: nested decision.behavior.
    private static JsonObject BuildCliOutput(string eventName, string decision, string? reason, JsonObject? updatedInput)
    {
        var d = new JsonObject { ["behavior"] = decision };
        if (reason != null) d["message"] = reason;
        if (updatedInput != null) d["updatedInput"] = updatedInput.DeepClone();
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = eventName,
                ["decision"] = d
            }
        };
    }

    private static JsonNode? Send(string json, bool waitForResponse)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            pipe.Connect((int)ConnectTimeout.TotalMilliseconds);

            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
            using var reader = new StreamReader(pipe, new UTF8Encoding(false), false, 4096, leaveOpen: true);

            writer.WriteLine(json);
            if (!waitForResponse) return null;

            using var cts = new CancellationTokenSource(PermissionTimeout);
            var read = reader.ReadLineAsync(cts.Token).AsTask();
            read.Wait(cts.Token);
            var line = read.Result;
            return string.IsNullOrWhiteSpace(line) ? null : JsonNode.Parse(line);
        }
        catch
        {
            return null;
        }
    }

    private static string ReadStdin()
    {
        using var stdin = Console.OpenStandardInput();
        using var reader = new StreamReader(stdin, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string? Str(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.String => el.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => el.GetRawText()
        };
    }

    private static JsonNode? Node(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var el)) return null;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
        try { return JsonNode.Parse(el.GetRawText()); } catch { return null; }
    }

    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VibeNotch", "logs");
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, "hook.log");
            var info = new FileInfo(file);
            if (info.Exists && info.Length > 256 * 1024) info.Delete();
            File.AppendAllText(file, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break the hook
        }
    }
}
