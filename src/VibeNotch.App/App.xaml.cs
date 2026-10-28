using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Threading;

namespace VibeNotch;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private TrayService? _tray;
    private UpdateService? _updates;
    private bool _balloonShown;

    public SettingsStore Settings { get; private set; } = null!;
    public HookServer Hooks { get; private set; } = null!;
    public SessionStore Store { get; } = new();
    public StatsStore Stats { get; } = StatsStore.Load();
    public MainWindow? Island { get; private set; }
    public TrayService? Tray => _tray;
    public UpdateService Updates => _updates!;
    public bool IslandVisible => Island?.IsVisible == true;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        if (e.Args.Contains("--install-hooks"))
        {
            var result = HookInstaller.Install();
            Log.Write("cli install hooks: " + result.Message);
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        if (e.Args.Contains("--uninstall-hooks"))
        {
            var result = HookInstaller.Uninstall();
            Log.Write("cli uninstall hooks: " + result.Message);
            Shutdown(result.Ok ? 0 : 1);
            return;
        }

        var activated = e.Args.Any(a =>
            a.StartsWith(ToastService.Protocol + ":", StringComparison.OrdinalIgnoreCase));

        _mutex = new Mutex(true, "VibeNotch.SingleInstance", out var created);
        if (!created)
        {
            if (activated) ToastService.ForwardActivation();
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Write("unhandled: " + args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Write("fatal: " + args.ExceptionObject);

        Settings = SettingsStore.Load();
        Log.Write("startup");
        ToastService.Initialize();

        Hooks = new HookServer(Settings.Value.PermissionTimeoutSeconds);
        Hooks.EventReceived += OnHookEvent;
        Hooks.PermissionTimedOut += OnPermissionTimedOut;
        Hooks.Start();

        Island = new MainWindow(this);
        MainWindow = Island;
        Island.Show();

        _updates = new UpdateService(this);
        _tray = new TrayService(this);
        _tray.SyncState();

        AutoStart.Apply(Settings.Value.AutoStart);

        var (ok, message) = HookInstaller.Install();
        Settings.Value.HooksInstalled = ok;
        Settings.Save();
        Log.Write("hooks: " + message);

        if (!_balloonShown)
        {
            _balloonShown = true;
            if (ok) _tray.ShowBalloon("Vibe Notch 已启动", "Hooks 已写入 Qoder，新开的会话会显示在顶部浮岛。");
            else _tray.ShowBalloon("Hooks 安装失败", message);
        }

        if (activated) Dispatcher.BeginInvoke(new Action(OnToastActivated));

        Updates.CheckOnStartup();
    }

    public void BeginUpdateFlow() => Updates.BeginUserFlow();

    public void OnToastActivated()
    {
        var focused = QoderFocuser.Focus();
        Log.Write("toast activated: " + (focused ? "focused Qoder" : "no Qoder window"));
    }

    private void OnHookEvent(HookEvent evt)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                Store.Handle(evt);
                MaybeAutoApprove(evt);
                Island?.OnStoreChanged();

                if (evt.Event == "PermissionRequest")
                {
                    if (Settings.Value.SoundOnPermission) PlaySound();
                    if (Settings.Value.NotifyOnPermission) NotifyPermission(evt);
                }
                else if (evt.Event == "Stop")
                {
                    if (Settings.Value.SoundOnComplete) PlaySound();
                    Stats.Bump(s => s.Tasks++);
                    Stats.Save();
                    if (Settings.Value.NotifyOnComplete) NotifyComplete(evt);
                }
            }
            catch (Exception ex)
            {
                Log.Write("event failed: " + ex.Message);
            }
        }));
    }

    private SessionVm? SessionFor(HookEvent evt) =>
        Store.Sessions.FirstOrDefault(s => s.Id == evt.SessionId);

    private void NotifyComplete(HookEvent evt)
    {
        var session = SessionFor(evt);
        var text = session == null
            ? "任务已完成"
            : $"{session.ProjectName} · {ToolText.Truncate(evt.LastAssistantMessage ?? "任务已完成", 120)}";
        Notify("Qoder 任务完成", text);
    }

    private void NotifyPermission(HookEvent evt)
    {
        // Auto-approve already answered it, so don't nag about a prompt that
        // never reached the user.
        if (!Store.Pending.Any(p => p.RequestId == evt.RequestId)) return;

        var session = SessionFor(evt);
        Notify("Qoder 等待授权", $"{session?.ProjectName ?? "Qoder"} · {evt.ToolName}");
    }

    private void Notify(string title, string text)
    {
        if (ToastService.Show(title, text))
        {
            Log.Write("toast: " + title);
            return;
        }

        _tray?.ShowBalloon(title, text);
    }

    public void TestNotification() => Notify("Qoder 任务完成", "通知测试 · 浮岛仍会在顶部显示会话状态");

    private void OnPermissionTimedOut(string requestId)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            Store.ExpirePermission(requestId);
            Island?.OnStoreChanged();
        }));
    }

    public void ResolvePermission(string requestId, string decision, JsonNode? updatedInput = null,
        string? reason = null, bool auto = false)
    {
        Log.Write($"resolve {requestId} -> {decision}");

        Hooks.Resolve(requestId, decision,
            reason ?? (decision == "deny" ? "Denied from Vibe Notch" : ""),
            Settings.Value.PermissionOutput,
            updatedInput);

        Store.ResolvePermission(requestId, decision);

        if (auto) Stats.Bump(s => s.AutoAllowed++);
        else if (decision == "deny") Stats.Bump(s => s.Denied++);
        else Stats.Bump(s => s.Allowed++);
        Stats.Save();
    }

    private void MaybeAutoApprove(HookEvent evt)
    {
        var interactive = evt.Event == "PermissionRequest"
            || (evt.Event == "PreToolUse" && !string.IsNullOrWhiteSpace(evt.RequestId));
        if (!interactive || string.IsNullOrWhiteSpace(evt.RequestId)) return;

        var pending = Store.Pending.FirstOrDefault(p => p.RequestId == evt.RequestId);
        if (pending == null || pending.Kind == PendingKind.Question) return;

        if (!AutoApprove.Matches(Settings.Value.AutoApprove, evt.ToolName, evt.ToolInput)) return;

        Log.Write($"auto-allow {evt.ToolName} rule-match {evt.RequestId}");
        ResolvePermission(evt.RequestId, "allow", auto: true);
    }

    public void ToggleIsland() => Island?.ToggleIsland();

    public void SelectMonitor(string deviceName)
    {
        Settings.Value.MonitorDeviceName = deviceName;
        Settings.Value.MonitorMode = "fixed";
        Settings.Save();
        Island?.ApplyPosition();
        _tray?.SyncState();
    }

    public void SetMonitorMode(string mode)
    {
        Settings.Value.MonitorMode = mode;
        Settings.Save();
        Island?.ApplyPosition();
        _tray?.SyncState();
    }

    public void CycleMonitorMode()
    {
        var next = Settings.Value.MonitorMode switch
        {
            "fixed" => "mouse",
            "mouse" => "qoder",
            _ => "fixed"
        };
        SetMonitorMode(next);
    }

    public bool SoundsEnabled =>
        Settings.Value.SoundOnComplete || Settings.Value.SoundOnPermission;

    public void SetSounds(bool enabled)
    {
        Settings.Value.SoundOnComplete = enabled;
        Settings.Value.SoundOnPermission = enabled;
        Settings.Save();
        _tray?.SyncState();
    }

    public bool NotificationsEnabled =>
        Settings.Value.NotifyOnComplete || Settings.Value.NotifyOnPermission;

    public void SetNotifications(bool enabled)
    {
        Settings.Value.NotifyOnComplete = enabled;
        Settings.Value.NotifyOnPermission = enabled;
        Settings.Save();
        _tray?.SyncState();
    }

    public string SoundName => SoundService.Normalize(Settings.Value.SoundName);

    public void SelectSound(string key)
    {
        Settings.Value.SoundName = SoundService.Normalize(key);
        Settings.Save();
        SoundService.Play(Settings.Value.SoundName);
        _tray?.SyncState();
    }

    public void SetAutoStart(bool enabled)
    {
        Settings.Value.AutoStart = enabled;
        Settings.Save();
        AutoStart.Apply(enabled);
        _tray?.SyncState();
    }

    public void InstallHooks(bool notify)
    {
        var (ok, message) = HookInstaller.Install();
        Settings.Value.HooksInstalled = ok;
        Settings.Save();
        Log.Write("hooks install: " + message);
        _tray?.SyncState();
        if (notify) _tray?.ShowBalloon(ok ? "Hooks 已安装" : "Hooks 安装失败", message);
    }

    public void RemoveHooks()
    {
        var (ok, message) = HookInstaller.Uninstall();
        Settings.Value.HooksInstalled = false;
        Settings.Save();
        _tray?.SyncState();
        _tray?.ShowBalloon(ok ? "Hooks 已移除" : "移除失败", message);
    }

    public void ExitApplication()
    {
        try
        {
            Settings.Save();
            Stats.Save();
        }
        catch
        {
            // ignore
        }

        _tray?.Dispose();
        _tray = null;
        Hooks?.Dispose();
        try
        {
            Island?.Close();
        }
        catch
        {
            // ignore
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Hooks?.Dispose();
        _tray?.Dispose();
        base.OnExit(e);
    }

    private void PlaySound() => SoundService.Play(Settings.Value.SoundName);
}
