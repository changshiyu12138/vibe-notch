using System.Text.Json;
using System.Text.Json.Nodes;

namespace VibeNotch;

public sealed class HookEvent
{
    public string Event { get; init; } = "";
    public string? SessionId { get; init; }
    public string? Cwd { get; init; }
    public string? TranscriptPath { get; init; }
    public string? PermissionMode { get; init; }
    public string? ToolName { get; init; }
    public JsonNode? ToolInput { get; init; }
    public string? ToolUseId { get; init; }
    public string? NotificationType { get; init; }
    public string? Message { get; init; }
    public string? LastAssistantMessage { get; init; }
    public string? Error { get; init; }
    public string? RequestId { get; init; }

    public static HookEvent? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;

            JsonNode? input = null;
            if (root.TryGetProperty("tool_input", out var ti) && ti.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
            {
                input = JsonNode.Parse(ti.GetRawText());
            }

            return new HookEvent
            {
                Event = Str(root, "event") ?? "",
                SessionId = Str(root, "session_id"),
                Cwd = Str(root, "cwd"),
                TranscriptPath = Str(root, "transcript_path"),
                PermissionMode = Str(root, "permission_mode"),
                ToolName = Str(root, "tool_name"),
                ToolInput = input,
                ToolUseId = Str(root, "tool_use_id"),
                NotificationType = Str(root, "notification_type"),
                Message = Str(root, "message"),
                LastAssistantMessage = Str(root, "last_assistant_message"),
                Error = Str(root, "error"),
                RequestId = Str(root, "request_id")
            };
        }
        catch
        {
            return null;
        }
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
}
