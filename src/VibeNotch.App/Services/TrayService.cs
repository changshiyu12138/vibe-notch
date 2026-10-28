using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace VibeNotch;

public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly WinForms.NotifyIcon _notify;
    private readonly WinForms.ToolStripMenuItem _showItem;
    private readonly WinForms.ToolStripMenuItem _autoStartItem;
    private readonly WinForms.ToolStripMenuItem _soundCompleteItem;
    private readonly WinForms.ToolStripMenuItem _soundPermissionItem;
    private readonly WinForms.ToolStripMenuItem _notifyCompleteItem;
    private readonly WinForms.ToolStripMenuItem _notifyPermissionItem;
    private readonly WinForms.ToolStripMenuItem _positionItem;
    private readonly WinForms.ToolStripMenuItem _soundPickerItem;
    private readonly List<WinForms.ToolStripMenuItem> _soundItems = new();
    private readonly Drawing.Icon _icon;

    public TrayService(App app)
    {
        _app = app;
        _icon = CreateIcon();

        var menu = new WinForms.ContextMenuStrip();

        _showItem = new WinForms.ToolStripMenuItem("隐藏浮岛");
        _showItem.Click += (_, _) => _app.ToggleIsland();

        var open = new WinForms.ToolStripMenuItem("打开 Qoder");
        open.Click += (_, _) => QoderFocuser.OpenOrFocus(null);

        var reinstall = new WinForms.ToolStripMenuItem("重装 Hooks");
        reinstall.Click += (_, _) => _app.InstallHooks(notify: true);

        var remove = new WinForms.ToolStripMenuItem("移除 Hooks");
        remove.Click += (_, _) => _app.RemoveHooks();

        _positionItem = new WinForms.ToolStripMenuItem("浮岛位置");
        _positionItem.DropDownOpening += (_, _) => BuildPositionMenu();

        _autoStartItem = new WinForms.ToolStripMenuItem("开机自启") { Checked = app.Settings.Value.AutoStart };
        _autoStartItem.Click += (_, _) => _app.SetAutoStart(!_app.Settings.Value.AutoStart);

        _soundCompleteItem = new WinForms.ToolStripMenuItem("完成提示音") { Checked = app.Settings.Value.SoundOnComplete };
        _soundCompleteItem.Click += (_, _) =>
        {
            _app.Settings.Value.SoundOnComplete = !_app.Settings.Value.SoundOnComplete;
            _app.Settings.Save();
            SyncState();
        };

        _soundPermissionItem = new WinForms.ToolStripMenuItem("授权提示音") { Checked = app.Settings.Value.SoundOnPermission };
        _soundPermissionItem.Click += (_, _) =>
        {
            _app.Settings.Value.SoundOnPermission = !_app.Settings.Value.SoundOnPermission;
            _app.Settings.Save();
            SyncState();
        };

        _soundPickerItem = new WinForms.ToolStripMenuItem("提示音音效");
        foreach (var option in SoundService.Options)
        {
            var item = new WinForms.ToolStripMenuItem(option.Label);
            item.Click += (_, _) => _app.SelectSound(option.Key);
            _soundPickerItem.DropDownItems.Add(item);
            _soundItems.Add(item);
        }

        _notifyCompleteItem = new WinForms.ToolStripMenuItem("完成通知") { Checked = app.Settings.Value.NotifyOnComplete };        _notifyCompleteItem.Click += (_, _) =>
        {
            _app.Settings.Value.NotifyOnComplete = !_app.Settings.Value.NotifyOnComplete;
            _app.Settings.Save();
            SyncState();
        };

        _notifyPermissionItem = new WinForms.ToolStripMenuItem("授权通知") { Checked = app.Settings.Value.NotifyOnPermission };
        _notifyPermissionItem.Click += (_, _) =>
        {
            _app.Settings.Value.NotifyOnPermission = !_app.Settings.Value.NotifyOnPermission;
            _app.Settings.Save();
            SyncState();
        };

        var testNotify = new WinForms.ToolStripMenuItem("测试通知");
        testNotify.Click += (_, _) => _app.TestNotification();

        var quit = new WinForms.ToolStripMenuItem("退出");
        quit.Click += (_, _) => _app.ExitApplication();

        menu.Items.AddRange(new WinForms.ToolStripItem[]
        {
            _showItem,
            open,
            new WinForms.ToolStripSeparator(),
            _positionItem,
            _autoStartItem,
            _soundCompleteItem,
            _soundPermissionItem,
            _soundPickerItem,
            _notifyCompleteItem,
            _notifyPermissionItem,
            testNotify,
            new WinForms.ToolStripSeparator(),
            reinstall,
            remove,
            new WinForms.ToolStripSeparator(),
            quit
        });

        _notify = new WinForms.NotifyIcon
        {
            Icon = _icon,
            Text = "Vibe Notch",
            Visible = true,
            ContextMenuStrip = menu
        };
        _notify.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _app.ToggleIsland();
        };
        _notify.DoubleClick += (_, _) => QoderFocuser.OpenOrFocus(null);
    }

    public void SyncState()
    {
        _showItem.Text = _app.IslandVisible ? "隐藏浮岛" : "显示浮岛";
        _autoStartItem.Checked = _app.Settings.Value.AutoStart;
        _soundCompleteItem.Checked = _app.Settings.Value.SoundOnComplete;
        _soundPermissionItem.Checked = _app.Settings.Value.SoundOnPermission;
        _notifyCompleteItem.Checked = _app.Settings.Value.NotifyOnComplete;
        _notifyPermissionItem.Checked = _app.Settings.Value.NotifyOnPermission;
        _positionItem.Text = PositionLabel();

        var currentSound = _app.SoundName;
        _soundPickerItem.Text = "提示音音效 · " + SoundService.LabelOf(currentSound);
        for (var i = 0; i < _soundItems.Count; i++)
        {
            _soundItems[i].Checked = SoundService.Options[i].Key == currentSound;
        }
    }

    private string PositionLabel() => _app.Settings.Value.MonitorMode switch
    {
        "mouse" => "浮岛位置 · 跟随鼠标",
        "qoder" => "浮岛位置 · 跟随 Qoder",
        _ => "浮岛位置 · 固定屏幕"
    };

    public void ShowBalloon(string title, string text)
    {
        if (ToastService.Show(title, text))
        {
            Log.Write("toast: " + title);
            return;
        }

        try
        {
            _notify.BalloonTipTitle = title;
            _notify.BalloonTipText = text;
            _notify.BalloonTipIcon = WinForms.ToolTipIcon.Info;
            _notify.ShowBalloonTip(4000);
        }
        catch
        {
            // balloon tips are best-effort
        }
    }

    private void BuildPositionMenu()
    {
        _positionItem.DropDownItems.Clear();

        var mode = _app.Settings.Value.MonitorMode ?? "fixed";

        var mouse = new WinForms.ToolStripMenuItem("跟随鼠标") { Checked = mode == "mouse" };
        mouse.Click += (_, _) => _app.SetMonitorMode("mouse");
        _positionItem.DropDownItems.Add(mouse);

        var qoder = new WinForms.ToolStripMenuItem("跟随 Qoder 窗口") { Checked = mode == "qoder" };
        qoder.Click += (_, _) => _app.SetMonitorMode("qoder");
        _positionItem.DropDownItems.Add(qoder);

        _positionItem.DropDownItems.Add(new WinForms.ToolStripSeparator());
        _positionItem.DropDownItems.Add(new WinForms.ToolStripMenuItem("固定屏幕") { Enabled = false });

        var current = _app.Settings.Value.MonitorDeviceName ?? PrimaryDeviceName();
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var label = screen.Primary ? $"{screen.DeviceName}（主屏）" : $"{screen.DeviceName}  {screen.Bounds.Width}×{screen.Bounds.Height}";
            var item = new WinForms.ToolStripMenuItem(label)
            {
                Checked = mode == "fixed" && string.Equals(current, screen.DeviceName, StringComparison.OrdinalIgnoreCase)
            };
            item.Click += (_, _) => _app.SelectMonitor(screen.DeviceName);
            _positionItem.DropDownItems.Add(item);
        }
    }

    private static string PrimaryDeviceName() => WinForms.Screen.PrimaryScreen?.DeviceName ?? "";

    private static Drawing.Icon CreateIcon()
    {
        using var bitmap = new Drawing.Bitmap(32, 32);
        using (var graphics = Drawing.Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Drawing.Color.Transparent);

            using var body = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 16, 17, 21));
            using var dot = new Drawing.SolidBrush(Drawing.Color.FromArgb(255, 61, 220, 132));
            using var outline = new Drawing.Pen(Drawing.Color.FromArgb(90, 255, 255, 255), 1);

            var rect = new Drawing.Rectangle(1, 9, 30, 14);
            graphics.FillRectangle(body, rect);
            graphics.DrawRectangle(outline, rect);
            graphics.FillEllipse(dot, 13, 14, 6, 6);
        }

        var handle = bitmap.GetHicon();
        using var temp = Drawing.Icon.FromHandle(handle);
        var icon = (Drawing.Icon)temp.Clone();
        NativeMethods.DestroyIcon(handle);
        return icon;
    }

    public void Dispose()
    {
        try
        {
            _notify.Visible = false;
            _notify.Dispose();
            _icon.Dispose();
        }
        catch
        {
            // ignore
        }
    }
}
