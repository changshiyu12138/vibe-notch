using System.Text.Json.Nodes;

namespace VibeNotch;

public sealed class SessionStore
{
    private readonly Dictionary<string, SessionVm> _sessions = new();
    private readonly List<PendingPermission> _pending = new();

    public IReadOnlyList<SessionVm> Sessions => _sessions.Values
        .Where(s => s.Phase != SessionPhase.Ended || (DateTime.Now - s.LastActivity).TotalSeconds < 20)
        .OrderByDescending(s => _pending.Any(p => p.SessionId == s.Id))
        .ThenByDescending(s => Priority(s.Phase))
        .ThenByDescending(s => s.LastActivity)
        .Take(8)
        .ToList();

    public IReadOnlyList<PendingPermission> Pending => _pending;

    public void Handle(HookEvent e)
    {
        if (string.IsNullOrEmpty(e.SessionId)) return;

        if (!_sessions.TryGetValue(e.SessionId, out var session))
        {
            // Qoder may report a subagent under its own session id; the only
            // parent hint available is "newest active session in the same cwd".
            var parent = e.Event == "SubagentStart"
                ? _sessions.Values
                    .Where(s => s.IsActive && !s.IsChild && string.Equals(s.Cwd, e.Cwd, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(s => s.LastActivity)
                    .FirstOrDefault()
                : null;

            session = new SessionVm { Id = e.SessionId, ParentId = parent?.Id ?? "" };
            if (parent != null) parent.Subagents++;
            _sessions[e.SessionId] = session;
        }

        if (!string.IsNullOrWhiteSpace(e.Cwd))
        {
            session.Cwd = e.Cwd;
            session.ProjectName = ToolText.ProjectNameFromCwd(e.Cwd);
        }

        if (!string.IsNullOrWhiteSpace(e.TranscriptPath)) session.TranscriptPath = e.TranscriptPath;
        session.LastActivity = DateTime.Now;

        switch (e.Event)
        {
            case "SessionStart":
                session.Phase = SessionPhase.Idle;
                session.Detail = "";
                break;

            case "UserPromptSubmit":
                session.Phase = SessionPhase.Processing;
                session.ToolName = "";
                session.Detail = "";
                session.TurnStartedAt = DateTime.Now;
                session.LastTurnSeconds = -1;
                break;

            case "PreToolUse":
                if (!string.IsNullOrWhiteSpace(e.RequestId))
                {
                    // Synchronous AskUserQuestion decision request from the hook.
                    session.Phase = SessionPhase.WaitingApproval;
                    session.ToolName = string.IsNullOrWhiteSpace(e.ToolName) ? "工具" : e.ToolName;
                    session.Detail = "";
                    AddPending(session, e, PendingKind.Question);
                    break;
                }

                session.Phase = SessionPhase.RunningTool;
                session.ToolName = e.ToolName ?? "";
                session.Detail = ToolText.ShortSummary(e.ToolName, e.ToolInput);
                TrackTodo(session, e.ToolName, e.ToolInput);
                break;

            case "PostToolUse":
                session.Phase = SessionPhase.Processing;
                session.Detail = "";
                RemovePendingForSession(session.Id);
                break;

            case "PostToolUseFailure":
                session.Phase = SessionPhase.Processing;
                session.Detail = ToolText.Truncate(e.Error ?? "工具执行失败", 90);
                RemovePendingForSession(session.Id);
                break;

            case "PermissionRequest":
                session.Phase = SessionPhase.WaitingApproval;
                session.ToolName = string.IsNullOrWhiteSpace(e.ToolName) ? "工具" : e.ToolName;
                session.Detail = "";
                AddPending(session, e, session.ToolName switch
                {
                    "AskUserQuestion" => PendingKind.Question,
                    "ExitPlanMode" => PendingKind.Plan,
                    _ => PendingKind.Approval
                });
                break;

            case "PermissionDenied":
                session.Phase = SessionPhase.Processing;
                session.Detail = "已拒绝";
                RemovePendingForSession(session.Id);
                break;

            case "Notification":
                if (e.NotificationType == "permission_prompt")
                {
                    // PermissionRequest already renders this with richer detail.
                }
                else if (e.NotificationType == "idle_prompt")
                {
                    session.Phase = SessionPhase.WaitingInput;
                    session.Detail = "";
                }
                else if (!string.IsNullOrWhiteSpace(e.Message))
                {
                    session.Detail = ToolText.Truncate(e.Message, 90);
                }
                break;

            case "Stop":
                session.Phase = SessionPhase.Done;
                session.ToolName = "";
                session.Detail = ToolText.Truncate(e.LastAssistantMessage ?? "", 100);
                FinishTurn(session);
                RemovePendingForSession(session.Id);
                break;

            case "StopFailure":
                session.Phase = SessionPhase.Error;
                session.Detail = ToolText.Truncate(e.Error ?? "本轮执行失败", 100);
                FinishTurn(session);
                RemovePendingForSession(session.Id);
                break;

            case "SubagentStart":
                session.Phase = SessionPhase.Subagent;
                session.Detail = "";
                session.Subagents++;
                break;

            case "SubagentStop":
                session.Phase = SessionPhase.Processing;
                session.Detail = "";
                session.Subagents = Math.Max(0, session.Subagents - 1);
                break;

            case "PreCompact":
                session.Phase = SessionPhase.Compacting;
                session.Detail = "";
                break;

            case "PostCompact":
                session.Phase = SessionPhase.Processing;
                session.Detail = "";
                break;

            case "SessionEnd":
                session.Phase = SessionPhase.Ended;
                RemovePendingForSession(session.Id);
                break;
        }
    }

    public void ResolvePermission(string requestId, string decision)
    {
        var pending = _pending.FirstOrDefault(p => p.RequestId == requestId);
        if (pending == null) return;
        _pending.Remove(pending);

        if (_sessions.TryGetValue(pending.SessionId, out var session))
        {
            session.Phase = SessionPhase.Processing;
            session.Detail = decision == "deny" ? "已拒绝" : "";
            session.LastActivity = DateTime.Now;
        }
    }

    public void ExpirePermission(string requestId)
    {
        var pending = _pending.FirstOrDefault(p => p.RequestId == requestId);
        if (pending == null) return;
        _pending.Remove(pending);

        if (_sessions.TryGetValue(pending.SessionId, out var session))
        {
            // The hook answered "ask", so Qoder owns the prompt from here on.
            session.Phase = SessionPhase.WaitingInput;
            session.ToolName = "";
            session.Detail = "已交给 Qoder 原生弹窗";
            session.Touch();
        }
    }

    public void Prune()
    {
        foreach (var session in _sessions.Values.ToList())
        {
            var age = DateTime.Now - session.LastActivity;

            if (age > StaleAfter(session.Phase))
            {
                Log.Write($"stale: {session.ProjectName} {session.Phase} -> Idle after {(int)age.TotalSeconds}s quiet");
                session.Phase = SessionPhase.Idle;
                session.ToolName = "";
                session.Detail = "";
            }

            if (session.Phase == SessionPhase.Ended && age.TotalSeconds > 30) _sessions.Remove(session.Id);
            else if (session.Phase == SessionPhase.Idle && age.TotalMinutes > 5) _sessions.Remove(session.Id);
            else if (!session.IsActive && age.TotalHours > 3) _sessions.Remove(session.Id);
        }
    }

    // Qoder skips its terminal event whenever a turn is interrupted, the window
    // closes, or a hook process dies, so an active phase has to expire on its
    // own or the island keeps claiming "思考中" long after the work is gone.
    private static TimeSpan StaleAfter(SessionPhase phase) => phase switch
    {
        SessionPhase.Processing => TimeSpan.FromSeconds(120),
        SessionPhase.Compacting => TimeSpan.FromMinutes(3),
        SessionPhase.Subagent => TimeSpan.FromMinutes(5),
        SessionPhase.RunningTool => TimeSpan.FromMinutes(10),
        SessionPhase.WaitingApproval => TimeSpan.FromMinutes(10),
        _ => TimeSpan.MaxValue
    };

    private void AddPending(SessionVm session, HookEvent e, PendingKind kind)
    {
        if (string.IsNullOrWhiteSpace(e.RequestId)) return;
        if (_pending.Any(p => p.RequestId == e.RequestId)) return;

        var editableKey = ToolText.EditableKeyFor(e.ToolName);
        string editableValue = "";
        if (editableKey != null && e.ToolInput is JsonObject inputObject &&
            inputObject[editableKey] is JsonValue inputValue && inputValue.TryGetValue<string>(out var text))
        {
            editableValue = text;
        }

        _pending.Add(new PendingPermission
        {
            RequestId = e.RequestId,
            SessionId = session.Id,
            ToolName = session.ToolName,
            Display = ToolText.BuildApprovalDisplay(e.ToolName, e.ToolInput),
            ReceivedAt = DateTime.Now,
            Kind = kind,
            Questions = kind == PendingKind.Question ? QuestionParser.Parse(e.ToolInput) : Array.Empty<QuestionVm>(),
            ToolInput = e.ToolInput,
            EditableKey = editableKey,
            EditableValue = editableValue
        });
    }

    private void RemovePendingForSession(string sessionId)
    {
        _pending.RemoveAll(p => p.SessionId == sessionId);
    }

    private static void FinishTurn(SessionVm session)
    {
        if (session.TurnStartedAt != default)
        {
            session.LastTurnSeconds = (int)(DateTime.Now - session.TurnStartedAt).TotalSeconds;
        }

        session.TurnStartedAt = default;
        session.Subagents = 0;
        session.Touch();
    }

    // Qoder only exposes the todo list through its own tools: TaskCreate /
    // TaskUpdate / TaskStop. Counting those is the only per-turn progress
    // signal available, so "任务 2/5" can be a rough estimate.
    private static void TrackTodo(SessionVm session, string? tool, JsonNode? input)
    {
        if (tool is not ("TaskCreate" or "TaskUpdate" or "TaskStop")) return;
        if (input is not JsonObject o) return;

        string? Str(string key) =>
            o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

        switch (tool)
        {
            case "TaskCreate":
                session.TodoTotal++;
                break;

            case "TaskUpdate":
            {
                var status = Str("status");
                var taskId = Str("taskId") ?? Str("task_id") ?? "";
                if (status == "completed" && taskId.Length > 0 && session.CompletedTodos.Add(taskId))
                {
                    session.TodoDone++;
                }
                else if (status == "deleted")
                {
                    session.TodoTotal = Math.Max(0, session.TodoTotal - 1);
                }
                break;
            }

            case "TaskStop":
                session.TodoTotal = Math.Max(session.TodoDone, session.TodoTotal - 1);
                break;
        }

        session.TodoDone = Math.Min(session.TodoDone, session.TodoTotal);
        session.Touch();
    }

    private static int Priority(SessionPhase phase) => phase switch
    {
        SessionPhase.WaitingApproval => 9,
        SessionPhase.RunningTool => 8,
        SessionPhase.Processing => 7,
        SessionPhase.Subagent => 6,
        SessionPhase.Compacting => 5,
        SessionPhase.WaitingInput => 4,
        SessionPhase.Error => 3,
        SessionPhase.Done => 2,
        SessionPhase.Idle => 1,
        _ => 0
    };
}
