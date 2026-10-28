using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeNotch;

public static class HookInstaller
{
    private const string HookExeName = "VibeNotch.Hook.exe";
    private const string LegacyHookExeName = "QoderVibeNotch.Hook.exe";

    private static readonly (string Event, string? Matcher, bool Async)[] Events =
    {
        ("SessionStart", null, true),
        ("UserPromptSubmit", null, true),
        ("PreToolUse", "*", true),
        // AskUserQuestion needs a synchronous decision so the island can answer it.
        ("PreToolUse", "AskUserQuestion", false),
        ("PostToolUse", "*", true),
        ("PostToolUseFailure", "*", true),
        ("PermissionRequest", "*", false),
        ("PermissionDenied", "*", true),
        ("Notification", null, true),
        ("Stop", null, true),
        ("StopFailure", null, true),
        ("SubagentStart", null, true),
        ("SubagentStop", null, true),
        ("PreCompact", null, true),
        ("PostCompact", null, true),
        ("SessionEnd", null, true)
    };

    public static string QoderSettingsPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".qoder",
        "settings.json");

    private static string HookExePath => Path.Combine(AppContext.BaseDirectory, HookExeName);

    public static bool IsInstalled()
    {
        try
        {
            if (!File.Exists(QoderSettingsPath)) return false;
            var root = ParseSettings(File.ReadAllText(QoderSettingsPath));
            return root?["hooks"] is JsonObject hooks && ContainsOurHook(hooks);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Ok, string Message) Install()
    {
        try
        {
            if (!File.Exists(HookExePath))
                return (false, $"找不到 hook 客户端：{HookExePath}");

            var settingsPath = QoderSettingsPath;
            Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

            JsonObject root;
            string? originalText = null;

            if (File.Exists(settingsPath))
            {
                originalText = File.ReadAllText(settingsPath);
                root = ParseSettings(originalText) ?? new JsonObject();
            }
            else
            {
                root = new JsonObject();
            }

            if (root["hooks"] is not JsonObject hooks)
            {
                hooks = new JsonObject();
                root["hooks"] = hooks;
            }

            RemoveOurHooks(hooks);

            foreach (var (eventName, matcher, isAsync) in Events)
            {
                if (hooks[eventName] is not JsonArray entries)
                {
                    entries = new JsonArray();
                    hooks[eventName] = entries;
                }

                var hook = new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = $"\"{HookExePath}\"",
                    ["timeout"] = isAsync ? 10 : 300
                };
                if (isAsync) hook["async"] = true;

                var entry = new JsonObject();
                if (matcher != null) entry["matcher"] = matcher;
                entry["hooks"] = new JsonArray(hook);
                entries.Add(entry);
            }

            var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            if (originalText != null && string.Equals(originalText, updated, StringComparison.Ordinal))
                return (true, "hooks 已是最新");

            if (!string.IsNullOrWhiteSpace(originalText)) Backup(settingsPath, originalText);
            File.WriteAllText(settingsPath, updated);
            return (true, "已写入 ~/.qoder/settings.json");
        }
        catch (Exception ex)
        {
            Log.Write("hook install failed: " + ex.Message);
            return (false, ex.Message);
        }
    }

    public static (bool Ok, string Message) Uninstall()
    {
        try
        {
            if (!File.Exists(QoderSettingsPath)) return (true, "没有可移除的 hooks");

            var text = File.ReadAllText(QoderSettingsPath);
            var root = ParseSettings(text);
            if (root?["hooks"] is not JsonObject hooks) return (true, "没有可移除的 hooks");

            Backup(QoderSettingsPath, text);
            RemoveOurHooks(hooks);

            if (hooks.Count == 0) root.Remove("hooks");

            File.WriteAllText(QoderSettingsPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            return (true, "已从 ~/.qoder/settings.json 移除");
        }
        catch (Exception ex)
        {
            Log.Write("hook uninstall failed: " + ex.Message);
            return (false, ex.Message);
        }
    }

    private static JsonObject? ParseSettings(string text)
    {
        text = text.TrimStart('\uFEFF');
        if (string.IsNullOrWhiteSpace(text)) return new JsonObject();
        try
        {
            return JsonNode.Parse(text) as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static void RemoveOurHooks(JsonObject hooks)
    {
        foreach (var key in hooks.Select(pair => pair.Key).ToList())
        {
            if (hooks[key] is not JsonArray entries) continue;

            for (var i = entries.Count - 1; i >= 0; i--)
            {
                if (entries[i] is not JsonObject entry) continue;
                if (entry["hooks"] is not JsonArray inner) continue;

                var ours = inner.Any(node =>
                    node is JsonObject hook &&
                    hook["command"]?.GetValue<string>() is { } command &&
                    IsOurCommand(command));

                if (ours) entries.RemoveAt(i);
            }

            if (entries.Count == 0) hooks.Remove(key);
        }
    }

    private static bool IsOurCommand(string command) =>
        command.Contains(HookExeName, StringComparison.OrdinalIgnoreCase) ||
        command.Contains(LegacyHookExeName, StringComparison.OrdinalIgnoreCase);

    private static bool ContainsOurHook(JsonObject hooks)
    {
        foreach (var pair in hooks)
        {
            if (pair.Value is not JsonArray entries) continue;
            foreach (var entry in entries)
            {
                if (entry is not JsonObject obj || obj["hooks"] is not JsonArray inner) continue;
                foreach (var node in inner)
                {
                    if (node is JsonObject hook &&
                        hook["command"]?.GetValue<string>() is { } command &&
                        IsOurCommand(command))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static void Backup(string path, string text)
    {
        try
        {
            var backup = $"{path}.vibenech-{DateTime.Now:yyyyMMdd-HHmmss}.bak";
            File.WriteAllText(backup, text);
        }
        catch (Exception ex)
        {
            Log.Write("settings backup failed: " + ex.Message);
        }
    }
}
