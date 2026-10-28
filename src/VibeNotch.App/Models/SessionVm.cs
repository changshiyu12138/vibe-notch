using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace VibeNotch;

public enum SessionPhase
{
    Idle,
    Processing,
    RunningTool,
    WaitingApproval,
    WaitingInput,
    Done,
    Error,
    Compacting,
    Subagent,
    Ended
}

public sealed class SessionVm : INotifyPropertyChanged
{
    private static readonly Brush Blue = Frozen(0x4D, 0xA3, 0xFF);
    private static readonly Brush Amber = Frozen(0xFF, 0xB0, 0x20);
    private static readonly Brush Green = Frozen(0x3D, 0xDC, 0x84);
    private static readonly Brush Red = Frozen(0xFF, 0x5A, 0x5A);
    private static readonly Brush Violet = Frozen(0xB0, 0x7C, 0xFF);
    private static readonly Brush Gray = Frozen(0x8C, 0x8C, 0x96);

    public string Id { get; init; } = "";
    public string Cwd { get; set; } = "";
    public string ProjectName { get; set; } = "Qoder";
    public string TranscriptPath { get; set; } = "";
    public string ToolName { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime LastActivity { get; set; } = DateTime.Now;
    public string ParentId { get; set; } = "";

    public bool IsChild => ParentId.Length > 0;

    public string Model { get; private set; } = "";
    public double ContextRatio { get; private set; } = -1;
    public string GitBranch { get; private set; } = "";
    public bool GitDirty { get; private set; }
    public DateTime TurnStartedAt { get; set; }
    public int LastTurnSeconds { get; set; } = -1;
    public int TodoTotal { get; set; }
    public int TodoDone { get; set; }
    public int Subagents { get; set; }

    internal HashSet<string> CompletedTodos { get; } = new();

    private SessionPhase _phase = SessionPhase.Idle;

    public SessionPhase Phase
    {
        get => _phase;
        set
        {
            if (_phase == value) return;
            _phase = value;
            Notify(nameof(Phase));
            Notify(nameof(PhaseText));
            Notify(nameof(AccentBrush));
            Notify(nameof(IsActive));
        }
    }

    public bool IsActive => _phase is SessionPhase.Processing or SessionPhase.RunningTool
        or SessionPhase.Subagent or SessionPhase.Compacting or SessionPhase.WaitingApproval;

    public string PhaseText => _phase switch
    {
        SessionPhase.Processing => "思考中",
        SessionPhase.RunningTool => string.IsNullOrEmpty(ToolName) ? "正在执行工具" : $"正在 {ToolName}",
        SessionPhase.WaitingApproval => string.IsNullOrEmpty(ToolName) ? "等待授权" : $"等待授权 · {ToolName}",
        SessionPhase.WaitingInput => "等待输入",
        SessionPhase.Done => "已完成",
        SessionPhase.Error => "执行出错",
        SessionPhase.Compacting => "压缩上下文",
        SessionPhase.Subagent => "子智能体运行中",
        SessionPhase.Ended => "已结束",
        _ => "空闲"
    };

    public Brush AccentBrush => _phase switch
    {
        SessionPhase.Processing or SessionPhase.RunningTool => Blue,
        SessionPhase.WaitingApproval => Amber,
        SessionPhase.WaitingInput or SessionPhase.Done => Green,
        SessionPhase.Error => Red,
        SessionPhase.Compacting or SessionPhase.Subagent => Violet,
        _ => Gray
    };

    public string TimeAgoText
    {
        get
        {
            var delta = DateTime.Now - LastActivity;
            if (delta.TotalSeconds < 5) return "刚刚";
            if (delta.TotalSeconds < 60) return $"{(int)delta.TotalSeconds}秒前";
            if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}分钟前";
            return $"{(int)delta.TotalHours}小时前";
        }
    }

    public string RowSubtitle
    {
        get
        {
            var line = string.IsNullOrWhiteSpace(Detail) ? PhaseText : $"{PhaseText} · {Detail}";
            var info = InfoLine;
            return info.Length == 0 ? line : $"{line} · {info}";
        }
    }

    public string ContextText => ContextRatio < 0 ? "" : $"{ContextRatio * 100:0}%";

    public void ApplyFacts(SessionFacts facts)
    {
        var changed = facts.GitBranch != GitBranch || facts.GitDirty != GitDirty
            || facts.Model != Model || facts.HasContext != (ContextRatio >= 0)
            || (facts.HasContext && Math.Abs(facts.ContextRatio - ContextRatio) > 0.005);

        GitBranch = facts.GitBranch;
        GitDirty = facts.GitDirty;
        Model = facts.Model;
        ContextRatio = facts.HasContext ? facts.ContextRatio : -1;

        if (!changed) return;
        Notify(nameof(InfoLine));
        Notify(nameof(ContextText));
        Notify(nameof(RowSubtitle));
        Notify(nameof(RowTooltip));
    }

    // Elapsed time for the running turn, or the final duration of the last one.
    public string TurnText
    {
        get
        {
            var seconds = LastTurnSeconds;
            if (IsActive && TurnStartedAt != default)
            {
                seconds = (int)(DateTime.Now - TurnStartedAt).TotalSeconds;
            }

            if (seconds < 0) return "";
            if (seconds < 60) return $"{seconds}秒";
            if (seconds < 3600) return $"{seconds / 60}分{seconds % 60}秒";
            return $"{seconds / 3600}时{(seconds % 3600) / 60}分";
        }
    }

    // Model codes are opaque ("qmodel_38max"), so they live in the row tooltip
    // instead of crowding out the branch and context numbers.
    public string RowTooltip => Model.Length > 0 ? $"{Cwd}\n模型 {Model}" : Cwd;

    public string InfoLine
    {
        get
        {
            var parts = new List<string>(5);

            if (GitBranch.Length > 0) parts.Add(GitDirty ? GitBranch + "*" : GitBranch);
            if (ContextRatio >= 0) parts.Add($"ctx {ContextText}");
            if (TodoTotal > 0) parts.Add($"任务 {Math.Min(TodoDone, TodoTotal)}/{TodoTotal}");
            if (Subagents > 0) parts.Add($"子智能体 {Subagents}");
            var turn = TurnText;
            if (turn.Length > 0) parts.Add(turn);

            return string.Join(" · ", parts);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Touch()
    {
        Notify(nameof(PhaseText));
        Notify(nameof(TimeAgoText));
        Notify(nameof(RowSubtitle));
        Notify(nameof(InfoLine));
        Notify(nameof(TurnText));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    internal static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class PendingPermission
{
    public string RequestId { get; init; } = "";
    public string SessionId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public string Display { get; init; } = "";
    public DateTime ReceivedAt { get; init; } = DateTime.Now;
    public PendingKind Kind { get; init; } = PendingKind.Approval;
    public IReadOnlyList<QuestionVm> Questions { get; init; } = Array.Empty<QuestionVm>();
    public JsonNode? ToolInput { get; init; }
    public string? EditableKey { get; init; }
    public string EditableValue { get; init; } = "";
}

public enum PendingKind
{
    Approval,
    Question,
    Plan
}

public sealed class QuestionVm
{
    public string Text { get; init; } = "";
    public bool MultiSelect { get; init; }
    public IReadOnlyList<QuestionOption> Options { get; init; } = Array.Empty<QuestionOption>();
}

public sealed class QuestionOption
{
    public string Label { get; init; } = "";
    public string Description { get; init; } = "";
}

public static class QuestionParser
{
    public static IReadOnlyList<QuestionVm> Parse(JsonNode? toolInput)
    {
        var result = new List<QuestionVm>();
        if (toolInput is not JsonObject input || input["questions"] is not JsonArray questions) return result;

        foreach (var node in questions)
        {
            if (node is not JsonObject q) continue;

            var options = new List<QuestionOption>();
            if (q["options"] is JsonArray optionNodes)
            {
                foreach (var optionNode in optionNodes)
                {
                    if (optionNode is not JsonObject o) continue;
                    var label = o["label"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(label)) continue;
                    options.Add(new QuestionOption
                    {
                        Label = label,
                        Description = o["description"]?.GetValue<string>() ?? ""
                    });
                }
            }

            var text = q["question"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(text) || options.Count == 0) continue;

            result.Add(new QuestionVm
            {
                Text = text,
                MultiSelect = q["multiSelect"]?.GetValue<bool>() ?? false,
                Options = options
            });
        }

        return result;
    }
}

public static class DiffBuilder
{
    public static IReadOnlyList<DiffRow> Build(string? tool, JsonNode? input)
    {
        var rows = new List<DiffRow>();
        if (input is not JsonObject o) return rows;

        string? Get(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        switch (tool)
        {
            case "Edit" or "MultiEdit":
                AddFile(rows, Get("file_path"));
                AddBlock(rows, Get("old_string"), '-');
                AddBlock(rows, Get("new_string"), '+');
                break;

            case "Write":
                AddFile(rows, Get("file_path"));
                AddBlock(rows, Get("content"), '+');
                break;

            case "NotebookEdit":
                AddFile(rows, Get("notebook_path"));
                AddBlock(rows, Get("new_source"), '+');
                break;

            case "Bash":
                AddBlock(rows, Get("command"), '$');
                break;

            case "ExitPlanMode":
                AddBlock(rows, Get("plan"), ' ');
                break;

            default:
                AddBlock(rows, ToolText.Truncate(ToolText.ToReadableJson(o), 900), ' ');
                break;
        }

        return rows;
    }

    private static void AddFile(List<DiffRow> rows, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path)) rows.Add(new DiffRow(path, DiffKind.File));
    }

    private static void AddBlock(List<DiffRow> rows, string? text, char prefix)
    {
        if (string.IsNullOrEmpty(text)) return;

        var kind = prefix switch
        {
            '-' => DiffKind.Remove,
            '+' => DiffKind.Add,
            '$' => DiffKind.Command,
            _ => DiffKind.Context
        };

        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            rows.Add(new DiffRow(prefix == ' ' ? line : $"{prefix} {line}", kind));
        }
    }
}

public enum DiffKind
{
    Context,
    Add,
    Remove,
    File,
    Command
}

public sealed class DiffRow
{
    private static readonly Brush AddBrush = SessionVm.Frozen(0x3D, 0xDC, 0x84);
    private static readonly Brush RemoveBrush = SessionVm.Frozen(0xFF, 0x6B, 0x6B);
    private static readonly Brush FileBrush = SessionVm.Frozen(0x4D, 0xA3, 0xFF);
    private static readonly Brush CommandBrush = SessionVm.Frozen(0xD9, 0xFF, 0xFF);
    private static readonly Brush ContextBrush = SessionVm.Frozen(0xA6, 0xFF, 0xFF);

    public DiffRow(string text, DiffKind kind)
    {
        Text = text;
        Kind = kind;
    }

    public string Text { get; }
    public DiffKind Kind { get; }

    public Brush Brush => Kind switch
    {
        DiffKind.Add => AddBrush,
        DiffKind.Remove => RemoveBrush,
        DiffKind.File => FileBrush,
        DiffKind.Command => CommandBrush,
        _ => ContextBrush
    };
}

public static class ToolText
{
    public static string? EditableKeyFor(string? tool) => tool switch
    {
        "Bash" => "command",
        "Edit" or "MultiEdit" => "new_string",
        "Write" => "content",
        "NotebookEdit" => "new_source",
        _ => null
    };

    public static string ProjectNameFromCwd(string cwd)
    {
        var trimmed = cwd.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public static string ShortSummary(string? tool, JsonNode? input)
    {
        if (input is not JsonObject o) return "";

        string? Get(string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s) ? s : null;

        var text = tool switch
        {
            "Bash" => Get("command"),
            "Edit" or "Write" or "MultiEdit" or "NotebookEdit" or "Read" => Get("file_path"),
            "Glob" => Get("pattern"),
            "Grep" => Get("pattern"),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(text))
        {
            foreach (var pair in o)
            {
                if (pair.Value is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                {
                    text = s;
                    break;
                }
            }
        }

        return Truncate(Collapse(text ?? ""), 70);
    }

    public static string BuildApprovalDisplay(string? tool, JsonNode? input)
    {
        if (input is not JsonObject o) return "";

        string? Get(string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        var sb = new StringBuilder();

        switch (tool)
        {
            case "Bash":
                if (Get("description") is { Length: > 0 } description) sb.AppendLine(description).AppendLine();
                sb.Append(Get("command"));
                break;

            case "Edit":
                sb.AppendLine(Get("file_path"));
                if (Get("old_string") is { Length: > 0 } oldText)
                    sb.AppendLine("--- 原内容").AppendLine(TruncateBlock(oldText, 240));
                if (Get("new_string") is { Length: > 0 } newText)
                    sb.AppendLine("+++ 新内容").AppendLine(TruncateBlock(newText, 240));
                break;

            case "Write":
                sb.AppendLine(Get("file_path"));
                if (Get("content") is { Length: > 0 } content) sb.AppendLine(TruncateBlock(content, 320));
                break;

            case "ExitPlanMode":
                sb.Append(Get("plan"));
                break;

            default:
                sb.Append(TruncateBlock(ToReadableJson(o), 900));
                break;
        }

        var text = sb.ToString().Trim();
        return text.Length > 900 ? text[..900] + "…" : text;
    }

    public static string Truncate(string value, int max)
    {
        value = value.Replace("\r", "").Replace("\n", " ").Trim();
        return value.Length <= max ? value : value[..max] + "…";
    }

    private static string TruncateBlock(string value, int max)
    {
        value = value.Replace("\r", "").Trim();
        return value.Length <= max ? value : value[..max] + "\n…";
    }

    // Keep CJK readable in the fallback JSON view; the default encoder escapes
    // every non-ASCII character into \uXXXX noise.
    internal static string ToReadableJson(JsonObject o) =>
        o.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });

    private static string Collapse(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
