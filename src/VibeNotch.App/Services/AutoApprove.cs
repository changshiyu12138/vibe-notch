using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace VibeNotch;

// Rule grammar: "Tool" allows the whole tool, "Tool:pattern" matches the tool's
// subject (command for Bash, file path for file tools) with * and ? wildcards.
public static class AutoApprove
{
    public static bool Matches(IReadOnlyList<string> rules, string? tool, JsonNode? input)
    {
        if (rules.Count == 0 || string.IsNullOrWhiteSpace(tool)) return false;

        var subject = Subject(tool, input);

        foreach (var raw in rules)
        {
            var rule = raw.Trim();
            if (rule.Length == 0) continue;

            var separator = rule.IndexOf(':');
            var ruleTool = separator > 0 ? rule[..separator] : rule;
            if (!ruleTool.Equals(tool, StringComparison.OrdinalIgnoreCase)) continue;

            if (separator < 0) return true;

            var pattern = rule[(separator + 1)..].Trim();
            if (pattern.Length == 0 || pattern == "*") return true;
            if (subject != null && Glob(pattern, subject)) return true;
        }

        return false;
    }

    public static string SuggestedRule(string? tool, JsonNode? input)
    {
        if (string.IsNullOrWhiteSpace(tool)) return "";

        var subject = Subject(tool, input);
        if (tool == "Bash" && !string.IsNullOrWhiteSpace(subject))
        {
            var firstToken = subject.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (firstToken.Length > 0) return $"{tool}:{firstToken}*";
        }

        return tool;
    }

    private static string? Subject(string tool, JsonNode? input)
    {
        if (input is not JsonObject o) return null;

        string? Get(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        return tool switch
        {
            "Bash" => Get("command"),
            "Edit" or "MultiEdit" or "Write" or "Read" => Get("file_path"),
            "NotebookEdit" => Get("notebook_path"),
            "Glob" or "Grep" => Get("pattern"),
            _ => null
        };
    }

    private static bool Glob(string pattern, string text)
    {
        var escaped = Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".");
        return Regex.IsMatch(text, $"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }
}
