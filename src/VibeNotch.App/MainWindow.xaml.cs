using System.Collections.ObjectModel;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using SolidColorBrush = System.Windows.Media.SolidColorBrush;
using WpfButton = System.Windows.Controls.Button;
using WpfHorizontalAlignment = System.Windows.HorizontalAlignment;
using WinForms = System.Windows.Forms;

namespace VibeNotch;

public partial class MainWindow : Window
{
    private enum IslandState
    {
        Collapsed,
        Compact,
        Expanded,
        Approval,
        Settings
    }

    private const double CollapsedWidth = 196;
    private const double CollapsedHeight = 8;
    private const double CompactWidth = 400;
    private const double CompactHeight = 44;
    private const double ExpandedWidth = 446;
    private const double ApprovalWidth = 486;
    private const double SettingsHeight = 400;

    private static readonly Brush IdleBrush = FrozenBrush(0x6E, 0x6E, 0x7A);
    private static readonly Brush GreenBrush = FrozenBrush(0x3D, 0xDC, 0x84);
    private static readonly Brush AmberBrush = FrozenBrush(0xFF, 0xB0, 0x20);
    private static readonly Brush RedBrush = FrozenBrush(0xFF, 0x5A, 0x5A);
    private static readonly Brush BadgeTextBrush = FrozenBrush(0xB4, 0xB4, 0xC0);
    private static readonly Brush QuestionBrush = FrozenBrush(0x4D, 0xA3, 0xFF);
    private static readonly Brush PlanBrush = FrozenBrush(0xB0, 0x7C, 0xFF);
    private static readonly Brush HintTextBrush = FrozenBrush(0x66, 0xFF, 0xFF);
    private static readonly Brush ReasonTextBrush = FrozenBrush(0xE6, 0xFF, 0xFF);

    private const string ReasonPlaceholder = "拒绝理由（可选，会回传给 Qoder）";

    // Same id in every process as long as the message name matches, so the copy
    // launched by the toast protocol handler can reach this window.
    private static readonly int ToastActivateMessage =
        unchecked((int)NativeMethods.RegisterWindowMessage(ToastService.ActivateMessage));

    private readonly App _app;
    private readonly ObservableCollection<SessionVm> _items = new();
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _hoverTimer;
    private readonly DispatcherTimer _enrichTimer;

    private IslandState _state = IslandState.Collapsed;
    private bool _hover;
    private bool _pinned;
    private bool _pulse;
    private bool _editMode;
    private bool _textMode;
    private bool _enriching;
    private bool _settingsOpen;
    private bool _screenPickerOpen;
    private string _screenSignature = "";
    private string _screenDevice = "";
    private DateTime _lastEventAt = DateTime.MinValue;
    private string? _pendingId;
    private PendingPermission? _current;
    private readonly HashSet<string> _selected = new();
    private List<OptionRow> _optionRows = new();
    private double _inputHeight = 30;
    private double _approvalHeight = 200;
    private double _targetWidth = CollapsedWidth;
    private double _targetHeight = CollapsedHeight;

    public MainWindow(App app)
    {
        _app = app;
        InitializeComponent();

        SessionList.ItemsSource = _items;

        Island.MouseEnter += (_, _) => { _hover = true; Refresh(); };
        Island.MouseLeave += (_, _) => { _hover = false; Refresh(); };
        CompactRow.MouseLeftButtonUp += (_, _) =>
        {
            _pinned = !_pinned;
            Refresh();
        };

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        // The transparent area around the island is click-through, so the window
        // can miss a fast pointer exit; poll the cursor to collapse promptly.
        _hoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        _hoverTimer.Tick += (_, _) => PollHover();
        _hoverTimer.Start();

        // git + transcript reads are the only expensive facts; refresh them on a
        // slow cadence instead of once per hook event.
        _enrichTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _enrichTimer.Tick += (_, _) => EnrichNow();
        _enrichTimer.Start();

        Loaded += (_, _) => ApplyPosition();
    }

    public bool IslandVisible => IsVisible;

    public void ToggleIsland() => SetIslandVisible(!IsVisible);

    public void SetIslandVisible(bool visible)
    {
        if (visible)
        {
            Show();
            ApplyPosition();
            Refresh();
        }
        else
        {
            Hide();
        }

        _app.Tray?.SyncState();
    }

    public void OnStoreChanged()
    {
        _lastEventAt = DateTime.Now;
        Refresh();
    }

    public void ApplyPosition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var screen = TargetScreen();
        if (screen == null) return;
        _screenDevice = screen.DeviceName;
        if (!NativeMethods.GetWindowRect(hwnd, out var rect)) return;

        var width = rect.Right - rect.Left;
        if (width <= 0) return;

        var x = screen.Bounds.Left + (screen.Bounds.Width - width) / 2;
        var y = screen.Bounds.Top;

        NativeMethods.SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);
    }

    private WinForms.Screen? TargetScreen()
    {
        switch (_app.Settings.Value.MonitorMode)
        {
            case "mouse":
                return WinForms.Screen.FromPoint(WinForms.Cursor.Position);

            case "qoder":
                var bounds = QoderRemote.FocusBounds();
                if (bounds.HasValue) return WinForms.Screen.FromRectangle(bounds.Value);
                break;
        }

        var preferred = _app.Settings.Value.MonitorDeviceName;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = WinForms.Screen.AllScreens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return WinForms.Screen.PrimaryScreen;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            style | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
        ApplyPosition();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (ToastActivateMessage != 0 && msg == ToastActivateMessage)
        {
            handled = true;
            _app.OnToastActivated();
            return IntPtr.Zero;
        }

        if (msg == NativeMethods.WM_NCHITTEST)
        {
            handled = true;
            var screenX = unchecked((short)(long)lParam);
            var screenY = unchecked((short)((long)lParam >> 16));
            return new IntPtr(IsPointInsideIsland(screenX, screenY)
                ? NativeMethods.HTCLIENT
                : NativeMethods.HTTRANSPARENT);
        }

        if (msg == NativeMethods.WM_MOUSEACTIVATE)
        {
            // While a TextBox needs the keyboard the island must accept
            // activation; otherwise stay click-through and never steal focus.
            if (!_textMode)
            {
                handled = true;
                return new IntPtr(NativeMethods.MA_NOACTIVATE);
            }
        }

        if (msg == NativeMethods.WM_DPICHANGED)
        {
            Dispatcher.BeginInvoke(new Action(ApplyPosition));
        }

        return IntPtr.Zero;
    }

    /// <summary>
    /// The window is a fixed-size transparent canvas; only the island itself
    /// should catch input so clicks pass through the empty space around it.
    /// </summary>
    private bool IsCursorInsideIsland()
    {
        if (!NativeMethods.GetCursorPos(out var point)) return false;
        return IsPointInsideIsland(point.X, point.Y);
    }

    private bool IsPointInsideIsland(int screenX, int screenY)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var point = new NativeMethods.POINT { X = screenX, Y = screenY };
        if (!NativeMethods.ScreenToClient(hwnd, ref point)) return false;

        var dpi = VisualTreeHelper.GetDpi(this);
        var x = point.X / dpi.DpiScaleX;
        var y = point.Y / dpi.DpiScaleY;

        var width = Island.ActualWidth;
        var height = Island.ActualHeight;
        if (width <= 0 || height <= 0) return false;

        var left = (Width - width) / 2;
        return x >= left && x <= left + width && y >= 0 && y <= height;
    }

    private void Tick()
    {
        _app.Store.Prune();

        if (IsVisible) _hover = IsCursorInsideIsland();

        FollowScreen();
        Refresh();
    }

    private void FollowScreen()
    {
        if (!IsVisible) return;

        var mode = _app.Settings.Value.MonitorMode;
        if (mode is not ("mouse" or "qoder")) return;

        var screen = TargetScreen();
        if (screen == null || screen.DeviceName == _screenDevice) return;

        Log.Write($"follow screen: {_screenDevice} -> {screen.DeviceName}");
        ApplyPosition();
    }

    private async void EnrichNow()
    {
        if (_enriching) return;

        var sessions = _app.Store.Sessions;
        if (sessions.Count == 0) return;

        _enriching = true;
        try
        {
            var facts = await Task.Run(() => Enrich.Collect(sessions));
            foreach (var (session, item) in facts) session.ApplyFacts(item);
        }
        catch (Exception ex)
        {
            Log.Write("enrich: " + ex.Message);
        }
        finally
        {
            _enriching = false;
        }
    }

    private void PollHover()
    {
        if (!IsVisible) return;

        var inside = IsCursorInsideIsland();
        if (inside == _hover) return;

        _hover = inside;
        Refresh();
    }

    private void Refresh()
    {
        var sessions = _app.Store.Sessions;
        var pending = _app.Store.Pending;

        SyncItems(sessions);

        var active = ChooseActive(sessions, pending);
        UpdateCompact(active, sessions.Count);
        UpdateStats();
        UpdateApproval(pending);
        UpdateSettings();

        ApplyState(ChooseState(sessions, pending));
    }

    private void UpdateSettings()
    {
        var settings = _app.Settings.Value;

        MenuButton.Visibility = _settingsOpen || _hover || _pinned
            ? Visibility.Visible
            : Visibility.Collapsed;
        MenuButton.Content = _settingsOpen ? "✕" : "☰";

        PositionValue.Text = settings.MonitorMode switch
        {
            "mouse" => "跟随鼠标",
            "qoder" => "跟随 Qoder",
            _ => "固定屏幕"
        };

        SetToggle(SoundDot, SoundValue, _app.SoundsEnabled);
        ToneValue.Text = SoundService.LabelOf(settings.SoundName);
        SetToggle(NotifyDot, NotifyValue, _app.NotificationsEnabled);
        SetToggle(AutoStartDot, AutoStartValue, settings.AutoStart);

        var selectedScreen = EffectiveFixedScreen();
        var screenIndex = Array.FindIndex(WinForms.Screen.AllScreens, s => s.DeviceName == selectedScreen?.DeviceName);
        ScreenValue.Text = (selectedScreen == null ? "主屏" : ScreenLabel(selectedScreen, screenIndex))
            + (settings.MonitorMode == "fixed" ? "" : " · 跟随中");
        UpdateScreenList();

        var hooks = settings.HooksInstalled;
        HooksDot.Fill = hooks ? GreenBrush : IdleBrush;
        HooksValue.Text = hooks ? "已安装" : "未安装";

        var version = typeof(App).Assembly.GetName().Version;
        AboutText.Text = version == null
            ? "Vibe Notch"
            : $"Vibe Notch v{version.ToString(3)} · Windows";
    }

    private static void SetToggle(System.Windows.Shapes.Ellipse dot, TextBlock label, bool on)
    {
        dot.Fill = on ? GreenBrush : IdleBrush;
        label.Text = on ? "开" : "关";
    }

    private WinForms.Screen? EffectiveFixedScreen()
    {
        var screens = WinForms.Screen.AllScreens;
        var preferred = _app.Settings.Value.MonitorDeviceName;
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var match = screens.FirstOrDefault(s =>
                string.Equals(s.DeviceName, preferred, StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }

        return WinForms.Screen.PrimaryScreen;
    }

    private static string ScreenLabel(WinForms.Screen screen, int index)
    {
        var width = screen.Bounds.Width;
        var height = screen.Bounds.Height;
        return $"显示器 {index + 1} · {width}×{height}" + (screen.Primary ? " · 主屏" : "");
    }

    private void UpdateScreenList()
    {
        var screens = WinForms.Screen.AllScreens;
        var selected = EffectiveFixedScreen();
        var signature = string.Join("|", screens.Select(s => s.DeviceName + "@" + s.Bounds))
            + "#" + selected?.DeviceName + "#" + _screenPickerOpen;
        if (signature == _screenSignature) return;
        _screenSignature = signature;

        ScreenList.Visibility = _screenPickerOpen ? Visibility.Visible : Visibility.Collapsed;
        ScreenList.Children.Clear();
        if (!_screenPickerOpen) return;

        for (var i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            var check = screen.DeviceName == selected?.DeviceName;
            var label = new TextBlock
            {
                Text = ScreenLabel(screen, i),
                FontSize = 11,
                Foreground = check ? GreenBrush : FrozenBrush(0x8C, 0x8C, 0x99),
                VerticalAlignment = VerticalAlignment.Center
            };

            var grid = new Grid();
            grid.Children.Add(label);
            if (check)
            {
                grid.Children.Add(new TextBlock
                {
                    Text = "✓",
                    FontSize = 11,
                    Foreground = GreenBrush,
                    HorizontalAlignment = WpfHorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            var row = new WpfButton
            {
                Style = (Style)FindResource("MenuRow"),
                Tag = "screen|" + screen.DeviceName,
                Height = 28,
                Margin = new Thickness(12, 0, 0, 0),
                Content = grid
            };
            row.Click += SettingsRow_Click;
            ScreenList.Children.Add(row);
        }
    }

    private double SettingsExtraHeight =>
        _screenPickerOpen ? WinForms.Screen.AllScreens.Length * 28 + 6 : 0;

    private void SyncItems(IReadOnlyList<SessionVm> sessions)
    {
        var top = sessions.Take(5).ToList();

        var same = top.Count == _items.Count;
        if (same)
        {
            for (var i = 0; i < top.Count; i++)
            {
                if (!ReferenceEquals(top[i], _items[i]))
                {
                    same = false;
                    break;
                }
            }
        }

        if (!same)
        {
            _items.Clear();
            foreach (var session in top) _items.Add(session);
        }

        foreach (var item in _items) item.Touch();
    }

    private static SessionVm? ChooseActive(IReadOnlyList<SessionVm> sessions, IReadOnlyList<PendingPermission> pending)
    {
        if (pending.Count > 0)
        {
            var target = sessions.FirstOrDefault(s => s.Id == pending[0].SessionId);
            if (target != null) return target;
        }

        return sessions.FirstOrDefault(s => s.IsActive) ?? sessions.FirstOrDefault();
    }

    private void UpdateCompact(SessionVm? active, int count)
    {
        if (active != null)
        {
            ProjectText.Text = active.ProjectName;
            ToolText.Text = active.PhaseText;
            StatusDot.Fill = active.AccentBrush;
            PulseRing.Stroke = active.AccentBrush;
            SetPulse(active.Phase is SessionPhase.Processing or SessionPhase.RunningTool
                or SessionPhase.Subagent or SessionPhase.Compacting);
        }
        else
        {
            ProjectText.Text = "Qoder";
            ToolText.Text = "空闲";
            StatusDot.Fill = IdleBrush;
            SetPulse(false);
        }

        CountBadge.Visibility = count > 1 ? Visibility.Visible : Visibility.Collapsed;
        CountText.Text = count.ToString();

        var context = active?.ContextText ?? "";
        ContextBadge.Visibility = context.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ContextValue.Text = context.Length > 0 ? $"ctx {context}" : "";
        ContextValue.Foreground = active == null ? BadgeTextBrush
            : active.ContextRatio >= 0.85 ? RedBrush
            : active.ContextRatio >= 0.6 ? AmberBrush
            : BadgeTextBrush;
    }

    private void UpdateStats()
    {
        var today = _app.Stats.Today;
        var parts = new List<string>(4);
        if (today.Tasks > 0) parts.Add($"完成 {today.Tasks} 个任务");
        if (today.Allowed > 0) parts.Add($"授权 {today.Allowed} 次");
        if (today.Denied > 0) parts.Add($"拒绝 {today.Denied} 次");
        if (today.AutoAllowed > 0) parts.Add($"自动放行 {today.AutoAllowed} 次");

        TodayStats.Text = parts.Count == 0 ? "今天还没有活动" : "今天 · " + string.Join(" · ", parts);
    }

    private void UpdateApproval(IReadOnlyList<PendingPermission> pending)
    {
        if (pending.Count == 0)
        {
            _pendingId = null;
            _current = null;
            return;
        }

        var first = pending[0];
        var isNew = _pendingId != first.RequestId;
        _pendingId = first.RequestId;
        _current = first;

        if (isNew)
        {
            _editMode = false;
            _selected.Clear();
            ApprovalEdit.Visibility = Visibility.Collapsed;
            ApprovalEdit.Text = first.EditableValue;
            EditButton.Content = "编辑后允许";
            DenyReason.Text = "";
            ApplyReasonPlaceholder();
        }

        var question = first.Kind == PendingKind.Question && first.Questions.Count > 0;

        ApprovalTool.Text = first.ToolName;
        ApprovalTitle.Text = first.Kind switch
        {
            PendingKind.Question => "Qoder 有问题等你回答",
            PendingKind.Plan => "计划审批",
            _ => "需要你的授权"
        };
        ApprovalDot.Fill = first.Kind switch
        {
            PendingKind.Question => QuestionBrush,
            PendingKind.Plan => PlanBrush,
            _ => AmberBrush
        };

        QuestionBox.Visibility = question ? Visibility.Visible : Visibility.Collapsed;
        ApprovalBox.Visibility = question ? Visibility.Collapsed : Visibility.Visible;

        if (question)
        {
            var q = first.Questions[0];
            QuestionText.Text = q.Text;
            QuestionHint.Text = q.MultiSelect ? "多选题：点选多个后按「允许」提交" : "点选项直接作答 · 答案会回传给 Qoder";

            if (isNew)
            {
                _optionRows = q.Options.Select(o => new OptionRow
                {
                    Label = o.Label,
                    Description = o.Description
                }).ToList();
                QuestionOptions.ItemsSource = _optionRows;
            }

            var textLines = Math.Min(3, (int)Math.Ceiling(q.Text.Length / 40.0));
            _approvalHeight = 150 + textLines * 16 + _optionRows.Count * 27;
        }
        else
        {
            if (isNew)
            {
                DiffView.ItemsSource = DiffBuilder.Build(first.ToolName, first.ToolInput);
                EditButton.Visibility = first.EditableKey != null ? Visibility.Visible : Visibility.Collapsed;
                AlwaysAllowButton.Content = $"总是允许 {first.ToolName}";
            }

            var lines = 0;
            foreach (var line in first.Display.Replace("\r", "").Split('\n'))
            {
                lines += Math.Max(1, (int)Math.Ceiling(line.Length / 62.0));
            }

            lines = Math.Max(1, lines);
            _inputHeight = Math.Clamp(lines * 15 + 14, 30, 96);
            ApprovalScroll.MinHeight = _inputHeight - 14;
            ApprovalScroll.MaxHeight = _inputHeight - 14;
            _approvalHeight = 170 + _inputHeight;
        }

        ApprovalQueue.Text = pending.Count > 1
            ? $"还有 {pending.Count - 1} 个待处理 · 超时后交给 Qoder 原生弹窗"
            : "允许 / 拒绝会直接回传给 Qoder · 超时交给原生弹窗";
    }

    private IslandState ChooseState(IReadOnlyList<SessionVm> sessions, IReadOnlyList<PendingPermission> pending)
    {
        if (pending.Count > 0) return IslandState.Approval;
        if (_settingsOpen) return IslandState.Settings;

        var hasSessions = sessions.Count > 0;
        if (_hover) return hasSessions ? IslandState.Expanded : IslandState.Compact;
        if (_pinned && hasSessions) return IslandState.Expanded;

        var recent = (DateTime.Now - _lastEventAt).TotalSeconds < Math.Max(2, _app.Settings.Value.AutoHideSeconds);
        if (hasSessions && (sessions.Any(s => s.IsActive) || recent)) return IslandState.Compact;

        return IslandState.Collapsed;
    }

    private void ApplyState(IslandState target)
    {
        var width = target switch
        {
            IslandState.Collapsed => CollapsedWidth,
            IslandState.Compact => CompactWidth,
            IslandState.Expanded => ExpandedWidth,
            IslandState.Settings => ExpandedWidth,
            _ => ApprovalWidth
        };

        var height = target switch
        {
            IslandState.Collapsed => CollapsedHeight,
            IslandState.Compact => CompactHeight,
            IslandState.Expanded => 104 + Math.Max(1, _items.Count) * 44,
            IslandState.Settings => SettingsHeight + SettingsExtraHeight,
            _ => Math.Min(330, _approvalHeight)
        };

        if (target != _state)
        {
            var wasCollapsed = _state == IslandState.Collapsed;
            var wasApproval = _state == IslandState.Approval;
            _state = target;

            if (target == IslandState.Collapsed)
            {
                ContentRoot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(120)));
            }
            else if (wasCollapsed)
            {
                ContentRoot.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180))
                {
                    BeginTime = TimeSpan.FromMilliseconds(30)
                });
            }

            if (target == IslandState.Expanded)
            {
                Show(SessionList, SessionListShift, 40);
                Show(Footer, FooterShift, 80);
            }
            else
            {
                HideUnless(SessionList, SessionListShift, () => _state != IslandState.Expanded);
                HideUnless(Footer, FooterShift, () => _state != IslandState.Expanded);
            }

            if (target == IslandState.Approval)
            {
                Show(ApprovalPanel, ApprovalShift, 40);
                HideUnless(CompactRow, null, () => _state == IslandState.Approval);
            }
            else
            {
                HideUnless(ApprovalPanel, ApprovalShift, () => _state != IslandState.Approval);

                if (wasApproval)
                {
                    Show(CompactRow, null, 40);
                }
                else
                {
                    CompactRow.BeginAnimation(UIElement.OpacityProperty, null);
                    CompactRow.Opacity = 1;
                    CompactRow.Visibility = Visibility.Visible;
                }
            }

            if (target == IslandState.Settings)
            {
                Show(SettingsPanel, SettingsShift, 60);
            }
            else
            {
                HideUnless(SettingsPanel, SettingsShift, () => _state != IslandState.Settings);
            }
        }

        // The island is clipped by the window, so grow the window before the
        // taller states (settings, long session lists) animate in.
        var windowHeight = Math.Max(360, height + 16);
        if (Math.Abs(Height - windowHeight) > 0.5) Height = windowHeight;

        // Only retarget the size animation when the destination actually changes.
        // Event bursts and the 1s timer call Refresh constantly; restarting the
        // animation on every call is what made the expansion feel jittery.
        if (Math.Abs(_targetWidth - width) > 0.5 || Math.Abs(_targetHeight - height) > 0.5)
        {
            _targetWidth = width;
            _targetHeight = height;
            AnimateIslandSize(width, height);
        }
    }

    private void AnimateIslandSize(double width, double height)
    {
        const double durationMs = 300;
        var duration = TimeSpan.FromMilliseconds(durationMs);
        var spline = new KeySpline(0.16, 1, 0.3, 1);

        Island.BeginAnimation(FrameworkElement.WidthProperty, MakeSizeAnimation(Island.Width, width, duration, spline));
        Island.BeginAnimation(FrameworkElement.HeightProperty, MakeSizeAnimation(Island.Height, height, duration, spline));
    }

    private static DoubleAnimationUsingKeyFrames MakeSizeAnimation(double from, double to, TimeSpan duration, KeySpline spline)
    {
        var animation = new DoubleAnimationUsingKeyFrames { Duration = duration };
        if (!double.IsNaN(from) && Math.Abs(from - to) > 0.1)
            animation.KeyFrames.Add(new LinearDoubleKeyFrame(from, KeyTime.FromPercent(0)));
        animation.KeyFrames.Add(new SplineDoubleKeyFrame(to, KeyTime.FromPercent(1), spline));
        return animation;
    }

    private static void Show(UIElement element, TranslateTransform? shift, int delayMs)
    {
        const int durationMs = 180;
        var begin = TimeSpan.FromMilliseconds(delayMs);

        element.Visibility = Visibility.Visible;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });

        shift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(durationMs))
        {
            BeginTime = begin,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        });
    }

    /// <summary>
    /// Fades a panel out while the island shrinks around it, then collapses it
    /// so disappearing content doesn't pop. The panel stays visible if the
    /// state flips back before the fade finishes.
    /// </summary>
    private void HideUnless(UIElement element, TranslateTransform? shift, Func<bool> stillHidden)
    {
        if (element.Visibility != Visibility.Visible) return;

        const int durationMs = 120;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        };
        fade.Completed += (_, _) =>
        {
            if (stillHidden()) element.Visibility = Visibility.Collapsed;
        };
        element.BeginAnimation(UIElement.OpacityProperty, fade);

        shift?.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
        });
    }

    private void SetPulse(bool on)
    {
        if (_pulse == on) return;
        _pulse = on;

        if (on)
        {
            var animation = new DoubleAnimation
            {
                From = 0.85,
                To = 0.05,
                Duration = TimeSpan.FromSeconds(1.2),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };
            PulseRing.BeginAnimation(UIElement.OpacityProperty, animation);
        }
        else
        {
            PulseRing.BeginAnimation(UIElement.OpacityProperty, null);
            PulseRing.Opacity = 0;
        }
    }

    private void MenuToggle_Click(object sender, RoutedEventArgs e)
    {
        _settingsOpen = !_settingsOpen;
        if (!_settingsOpen) _screenPickerOpen = false;
        Refresh();
    }

    private void SettingsRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string action }) return;

        switch (action)
        {
            case "back":
                _settingsOpen = false;
                _screenPickerOpen = false;
                break;

            case "position":
                _app.CycleMonitorMode();
                break;

            case "screen":
                _screenPickerOpen = !_screenPickerOpen;
                break;

            case var device when device.StartsWith("screen|", StringComparison.Ordinal):
                _app.SelectMonitor(device["screen|".Length..]);
                _screenPickerOpen = false;
                break;

            case "sound":
                _app.SetSounds(!_app.SoundsEnabled);
                break;

            case "tone":
                _app.SelectSound(SoundService.Next(_app.SoundName));
                break;

            case "notify":
                _app.SetNotifications(!_app.NotificationsEnabled);
                break;

            case "autostart":
                _app.SetAutoStart(!_app.Settings.Value.AutoStart);
                break;

            case "hooks":
                if (_app.Settings.Value.HooksInstalled) _app.RemoveHooks();
                else _app.InstallHooks(false);
                break;

            case "test":
                _app.TestNotification();
                break;

            case "quit":
                _app.ExitApplication();
                return;
        }

        Refresh();
    }

    private void SessionRow_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string id })
        {
            var session = _app.Store.Sessions.FirstOrDefault(s => s.Id == id);
            QoderFocuser.OpenOrFocus(session?.ProjectName);
        }

        e.Handled = true;
    }

    private void Allow_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        if (_current.Kind == PendingKind.Question && _selected.Count > 0)
        {
            var labels = _optionRows.Where(r => _selected.Contains(r.Label)).Select(r => r.Label);
            SubmitAnswers(_current, labels);
            return;
        }

        if (_editMode && _current.EditableKey != null)
        {
            var updated = CloneInput(_current);
            updated[_current.EditableKey] = ApprovalEdit.Text;
            ResolveCurrent("allow", updated);
            return;
        }

        ResolveCurrent("allow");
    }

    private void Deny_Click(object sender, RoutedEventArgs e)
    {
        var reason = DenyReason.Text == ReasonPlaceholder ? "" : DenyReason.Text.Trim();
        ResolveCurrent("deny", null, string.IsNullOrEmpty(reason) ? null : reason);
    }

    private void QuestionOption_Click(object sender, MouseButtonEventArgs e)
    {
        if (_current is not { Kind: PendingKind.Question } pending ||
            sender is not FrameworkElement { Tag: string label })
        {
            return;
        }

        var multi = pending.Questions.FirstOrDefault()?.MultiSelect == true;
        if (multi)
        {
            var row = _optionRows.FirstOrDefault(r => r.Label == label);
            if (row == null) return;

            row.Selected = !row.Selected;
            if (row.Selected) _selected.Add(label);
            else _selected.Remove(label);
        }
        else
        {
            SubmitAnswers(pending, new[] { label });
        }

        e.Handled = true;
    }

    private void SubmitAnswers(PendingPermission pending, IEnumerable<string> labels)
    {
        var question = pending.Questions.FirstOrDefault();
        if (question == null) return;

        var updated = CloneInput(pending);
        updated["answers"] = new JsonObject { [question.Text] = string.Join(", ", labels) };
        ResolveCurrent("allow", updated);
    }

    private void AlwaysAllow_Click(object sender, RoutedEventArgs e)
    {
        if (_current == null) return;

        var rule = AutoApprove.SuggestedRule(_current.ToolName, _current.ToolInput);
        if (rule.Length == 0) return;

        var settings = _app.Settings.Value;
        if (!settings.AutoApprove.Contains(rule))
        {
            settings.AutoApprove.Add(rule);
            _app.Settings.Save();
            Log.Write("auto-approve rule added: " + rule);
        }

        FlashFooter($"已加入白名单：{rule}");
        ResolveCurrent("allow");
    }

    private void EditToggle_Click(object sender, RoutedEventArgs e)
    {
        _editMode = !_editMode;
        ApprovalEdit.Visibility = _editMode ? Visibility.Visible : Visibility.Collapsed;
        EditButton.Content = _editMode ? "收起编辑" : "编辑后允许";

        if (_editMode)
        {
            SetTextMode(true);
            ApprovalEdit.Focus();
            ApprovalEdit.SelectAll();
        }
        else if (!DenyReason.IsFocused)
        {
            SetTextMode(false);
        }

        Refresh();
    }

    private void DenyReason_GotFocus(object sender, RoutedEventArgs e)
    {
        if (DenyReason.Text == ReasonPlaceholder)
        {
            DenyReason.Text = "";
            DenyReason.Foreground = ReasonTextBrush;
        }

        SetTextMode(true);
    }

    private void DenyReason_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!ApprovalEdit.IsFocused) SetTextMode(false);
        ApplyReasonPlaceholder();
    }

    private void ApprovalEdit_LostFocus(object sender, RoutedEventArgs e)
    {
        if (!DenyReason.IsFocused) SetTextMode(false);
    }

    // WM_MOUSEACTIVATE already answered MA_NOACTIVATE by the time WPF would
    // focus the box, so lift the restriction on the press itself.
    private void TextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e) => SetTextMode(true);

    /// <summary>
    /// The window carries WS_EX_NOACTIVATE and answers WM_MOUSEACTIVATE with
    /// MA_NOACTIVATE so hovering never steals focus from Qoder. A TextBox can
    /// only receive keystrokes while the window is active, so typing mode
    /// temporarily lifts both restrictions.
    /// </summary>
    private void SetTextMode(bool on)
    {
        if (_textMode == on) return;
        _textMode = on;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var style = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        style = on
            ? style & ~NativeMethods.WS_EX_NOACTIVATE
            : style | NativeMethods.WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, style);

        if (on) Activate();
    }

    private void ApplyReasonPlaceholder()
    {
        if (string.IsNullOrEmpty(DenyReason.Text))
        {
            DenyReason.Text = ReasonPlaceholder;
            DenyReason.Foreground = HintTextBrush;
        }
    }

    private void Interrupt_Click(object sender, RoutedEventArgs e)
    {
        FlashFooter(QoderRemote.Interrupt() ? "已向 Qoder 发送 Esc" : "找不到 Qoder 窗口");
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        FlashFooter(QoderRemote.SendPrompt("继续") ? "已发送「继续」" : "找不到 Qoder 窗口");
    }

    private void FlashFooter(string message)
    {
        FooterHint.Text = message;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FooterHint.Text = "点击会话聚焦 Qoder · 右键托盘更多设置";
        };
        timer.Start();
    }

    private static JsonObject CloneInput(PendingPermission pending) =>
        pending.ToolInput as JsonObject is { } existing
            ? (JsonObject)existing.DeepClone()
            : new JsonObject();

    private void ResolveCurrent(string decision, JsonNode? updatedInput = null, string? reason = null)
    {
        if (string.IsNullOrEmpty(_pendingId)) return;

        var requestId = _pendingId;
        _pendingId = null;
        _current = null;
        _app.ResolvePermission(requestId, decision, updatedInput, reason);
        Refresh();
    }

    private static Brush FrozenBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}

public sealed class OptionRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _selected;

    public string Label { get; init; } = "";
    public string Description { get; init; } = "";

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
