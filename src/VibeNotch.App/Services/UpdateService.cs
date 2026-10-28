using Velopack;
using Velopack.Sources;

namespace VibeNotch;

public enum UpdateState
{
    Unsupported,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    Ready,
    Failed
}

public sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/changshiyu12138/vibe-notch";
    private static readonly TimeSpan AutoCheckInterval = TimeSpan.FromHours(6);

    private readonly App _app;
    private readonly UpdateManager? _manager;
    private UpdateInfo? _pending;
    private int _busy;

    public UpdateService(App app)
    {
        _app = app;
        try
        {
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            State = _manager.IsInstalled && !_manager.IsPortable ? UpdateState.Idle : UpdateState.Unsupported;
            Log.Write(IsInstalled
                ? "updates: installed v" + CurrentVersion
                : "updates: not a Velopack install, updates disabled");
        }
        catch (Exception ex)
        {
            State = UpdateState.Unsupported;
            Log.Write("updates init failed: " + ex.Message);
        }
    }

    public UpdateState State { get; private set; } = UpdateState.Unsupported;
    public int Progress { get; private set; }
    public bool IsInstalled => _manager?.IsInstalled == true && _manager?.IsPortable != true;

    public string CurrentVersion
    {
        get
        {
            var version = _manager?.CurrentVersion?.ToString()
                ?? typeof(App).Assembly.GetName().Version?.ToString(3)
                ?? "0.0.0";
            return version;
        }
    }

    public string? AvailableVersion => _pending?.TargetFullRelease.Version.ToString();

    public string StatusText => State switch
    {
        UpdateState.Unsupported => "仅安装版支持",
        UpdateState.Idle => "点击检查",
        UpdateState.Checking => "正在检查…",
        UpdateState.UpToDate => "已是最新 · v" + CurrentVersion,
        UpdateState.Available => "发现 v" + AvailableVersion + "，点击更新",
        UpdateState.Downloading => "下载中 " + Progress + "%",
        UpdateState.Ready => "正在重启完成更新…",
        _ => "检查失败，点击重试"
    };

    public string TrayText => State switch
    {
        UpdateState.Available => "检查更新 · 发现 v" + AvailableVersion,
        UpdateState.Downloading => "正在下载更新 " + Progress + "%",
        UpdateState.UpToDate => "检查更新 · 已是最新",
        UpdateState.Unsupported => "检查更新（仅安装版）",
        _ => "检查更新"
    };

    public void CheckOnStartup()
    {
        if (!IsInstalled) return;

        var last = _app.Settings.Value.UpdateCheckUtc;
        if (DateTimeOffset.TryParse(last, out var at) &&
            DateTimeOffset.UtcNow - at < AutoCheckInterval) return;

        _ = Task.Run(CheckAsync);
    }

    public void BeginUserFlow()
    {
        if (!IsInstalled)
        {
            Refresh();
            return;
        }

        if (State == UpdateState.Available && _pending != null)
        {
            DownloadAndApply();
            return;
        }

        _ = Task.Run(CheckAsync);
    }

    private async Task CheckAsync()
    {
        if (_manager == null || !IsInstalled) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        try
        {
            SetState(UpdateState.Checking);
            var info = await _manager.CheckForUpdatesAsync();
            _app.Settings.Value.UpdateCheckUtc = DateTimeOffset.UtcNow.ToString("o");
            _app.Settings.Save();

            if (info == null)
            {
                Log.Write("updates: up to date v" + CurrentVersion);
                SetState(UpdateState.UpToDate);
                return;
            }

            _pending = info;
            Log.Write("updates: available v" + AvailableVersion);
            SetState(UpdateState.Available);
        }
        catch (Exception ex)
        {
            Log.Write("updates: check failed: " + ex.Message);
            SetState(UpdateState.Failed);
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private void DownloadAndApply()
    {
        if (_manager == null || _pending == null) return;
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;

        _ = Task.Run(async () =>
        {
            try
            {
                SetState(UpdateState.Downloading, 0);
                await _manager.DownloadUpdatesAsync(_pending, p => SetState(UpdateState.Downloading, p));
                Log.Write("updates: downloaded v" + AvailableVersion);
                SetState(UpdateState.Ready);
                await Task.Delay(400);
                _app.Dispatcher.Invoke(() => _manager.ApplyUpdatesAndRestart(_pending));
            }
            catch (Exception ex)
            {
                Log.Write("updates: download failed: " + ex.Message);
                SetState(UpdateState.Failed);
                Interlocked.Exchange(ref _busy, 0);
            }
        });
    }

    private void SetState(UpdateState state, int progress = 0)
    {
        State = state;
        Progress = progress;
        Refresh();
    }

    private void Refresh()
    {
        _app.Dispatcher.BeginInvoke(new Action(() =>
        {
            _app.Island?.OnUpdateStateChanged();
            _app.Tray?.SyncState();
        }));
    }
}
