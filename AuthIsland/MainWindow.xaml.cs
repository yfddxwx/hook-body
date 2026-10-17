using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AuthIsland.Animation;
using AuthIsland.Cli;
using AuthIsland.Interop;
using AuthIsland.Services;
using AuthIsland.Visual;

namespace AuthIsland;

public partial class MainWindow : Window
{
    private enum UiState { Idle, Dormant, Alert, Result, Panel, Done }

    // ---- 尺寸常量（DIP）----
    private const double IdleW = 124, IdleH = 34, IdleR = 17;
    private const double DormantW = 54, DormantH = 18, DormantR = 9;
    private const double AlertW = 452, AlertH = 176, AlertR = 26;
    private const double ResultW = 236, ResultH = 62, ResultR = 31;
    private const double PanelW = 320, PanelH = 140, PanelR = 24;
    private const double DoneW = 336, DoneH = 72, DoneR = 24;

    // 全部静态 Brush 都 Freeze：动画每帧都会用到，冻结后不进属性系统，省开销
    private static readonly Brush OkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
    private static readonly Brush WarnBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB0, 0x20)));
    private static readonly Brush DangerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71)));

    // 无事件多久后开始"躲起来"：先缩成一点，再彻底隐藏（隐藏后仍有托盘入口）
    private static readonly TimeSpan DormantAfter = Seconds("AUTH_ISLAND_DORMANT_SEC", 15);
    private static readonly TimeSpan HideAfter = Seconds("AUTH_ISLAND_HIDE_SEC", 180);

    // 完成通知：显示多久自动收起。
    // 默认**总是提示**——用户要的就是「这轮跑完了」这一眼。
    // 想恢复「WorkBuddy 在前台就安静」的旧行为，设 AUTH_ISLAND_NOTIFY_QUIET=1。
    private static readonly TimeSpan DoneLinger = TimeSpan.FromSeconds(8);
    private static readonly bool QuietWhenForeground =
        Environment.GetEnvironmentVariable("AUTH_ISLAND_NOTIFY_QUIET") == "1";

    private readonly AuthServer _server = new();
    private readonly Queue<AuthRequest> _queue = new();

    private AuthRequest? _current;
    private UiState _state = UiState.Idle;

    private readonly SpringValue _w = new(IdleW);
    private readonly SpringValue _h = new(IdleH);
    private readonly SpringValue _r = new(IdleR);
    private readonly RectangleGeometry _clip = new();

    private bool _renderingHooked;
    private long _lastTick;
    private DispatcherTimer? _resultTimer;
    private DispatcherTimer? _idleTimer;
    private DispatcherTimer? _doneTimer;
    private TrayIcon? _tray;

    private DoneNotice? _done;

    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private DateTime? _lastRequestUtc;
    private bool _dormant;
    private bool _hidden;
    private bool _paused;
    private bool _wakeQueued;

    public MainWindow()
    {
        InitializeComponent();
        ApplyFrost();

        _server.RequestReceived += OnServerRequest;
        _server.RequestExpired += OnServerExpired;
        _server.NotifyReceived += OnNotifyReceived;

        try
        {
            _server.Start();
        }
        catch
        {
            // 端口被占用时仍然显示胶囊，只是收不到请求，不至于整个窗口起不来
        }

        AllowButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Resolve(true); };
        DenyButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; Resolve(false); };

        Capsule.MouseLeftButtonUp += OnCapsuleClick;
        Capsule.MouseEnter += OnCapsuleEnter;
        Capsule.MouseLeave += OnCapsuleLeave;

        PauseButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; SetPaused(!_paused); };
        HideButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; HideToTray(); };
        QuitButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ShutdownApp(); };

        _tray = new TrayIcon();
        _tray.ShowRequested += () => Dispatcher.Invoke(WakeFromAnywhere);
        _tray.ExitRequested += () => Dispatcher.Invoke(ShutdownApp);
        _tray.PauseChanged += paused => Dispatcher.Invoke(() => SetPaused(paused));

        Loaded += OnLoaded;
        Closed += (_, _) =>
        {
            _resultTimer?.Stop();
            _idleTimer?.Stop();
            _doneTimer?.Stop();
            UnhookRendering();
            _server.Dispose();
            _tray?.Dispose();
            _tray = null;
        };
    }

    private static TimeSpan Seconds(string name, int fallback)
    {
        var raw = Environment.GetEnvironmentVariable(name);
        return int.TryParse(raw, out var sec) && sec >= 0
            ? TimeSpan.FromSeconds(sec)
            : TimeSpan.FromSeconds(fallback);
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }

    /// <summary>把生成的噪声贴到两颗磨砂层上（见 Visual/FrostTexture.cs）。</summary>
    private void ApplyFrost()
    {
        FrostA.OpacityMask = FrostTexture.Create(83, 17, 0.55);
        FrostB.OpacityMask = FrostTexture.Create(127, 91, 0.45);
    }

    // ================= 生命周期 =================

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        PositionWindow();
        ApplyExtendedStyles();

        // 入场：从一条细线"长"成胶囊
        _w.Snap(48);
        _h.Snap(9);
        _r.Snap(4.5);
        ApplyChrome();

        AnimateTo(IdleW, IdleH, IdleR);
        FadeIn(IdleLayer, 0);

        StartIdleTimer();
        _lastActivityUtc = DateTime.UtcNow;
    }

    private void PositionWindow()
    {
        var wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - Width) / 2.0;
        Top = wa.Top + 8;
    }

    private void ApplyExtendedStyles()
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        int ex = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        ex |= NativeMethods.WS_EX_TOOLWINDOW;   // 不进任务栏 / Alt-Tab
        ex |= NativeMethods.WS_EX_NOACTIVATE;   // 点击不抢焦点
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, ex);
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    /// <summary>
    /// 胶囊以外的区域一律返回 HTTRANSPARENT，让鼠标穿透到底层窗口。
    /// 这样这个 560x300 的透明浮层就不会挡住屏幕顶部任何东西。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_NCHITTEST)
        {
            long lp = lParam.ToInt64();
            int sx = (short)(lp & 0xFFFF);
            int sy = (short)((lp >> 16) & 0xFFFF);

            Point local;
            try { local = PointFromScreen(new Point(sx, sy)); }
            catch { return IntPtr.Zero; }

            var origin = Capsule.TranslatePoint(new Point(0, 0), this);
            var hit = new Rect(
                origin.X - 4, origin.Y - 4,
                Capsule.ActualWidth + 8, Capsule.ActualHeight + 8);

            if (!hit.Contains(local))
            {
                handled = true;
                return NativeMethods.HTTRANSPARENT;
            }
        }
        else if (msg == NativeMethods.WM_MOUSEMOVE && _dormant && !_wakeQueued)
        {
            // 不依赖 WPF 的 MouseEnter：NOACTIVATE 窗口上它的触发条件比较微妙，
            // 直接用原始 WM_MOUSEMOVE 更稳。同一时间只排一次，避免刷爆队列。
            _wakeQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _wakeQueued = false;
                Wake();
            }), DispatcherPriority.Input);
        }

        return IntPtr.Zero;
    }

    // ================= 逐帧动画 =================

    private void HookRendering()
    {
        if (_renderingHooked) return;
        _lastTick = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += OnRendering;
        _renderingHooked = true;
    }

    private void UnhookRendering()
    {
        if (!_renderingHooked) return;
        CompositionTarget.Rendering -= OnRendering;
        _renderingHooked = false;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        long now = Stopwatch.GetTimestamp();
        double dt = (now - _lastTick) / (double)Stopwatch.Frequency;
        _lastTick = now;

        // 掉帧时钳住步长，避免弹簧数值炸掉
        if (dt <= 0.0005) return;
        if (dt > 0.05) dt = 0.016;

        bool moving = false;
        moving |= _w.Tick(dt);
        moving |= _h.Tick(dt);
        moving |= _r.Tick(dt);

        ApplyChrome();

        if (_current is not null) UpdateCountdown();

        if (!moving && _current is null &&
            _state is UiState.Idle or UiState.Dormant or UiState.Panel or UiState.Done)
        {
            UnhookRendering();
        }
    }

    private void ApplyChrome()
    {
        double w = Math.Max(0.001, _w.Value);
        double h = Math.Max(0.001, _h.Value);
        double radius = Math.Min(_r.Value, h / 2.0);

        Capsule.Width = w;
        Capsule.Height = h;
        Capsule.CornerRadius = new CornerRadius(radius);

        // 投影跟着胶囊一起形变，否则缩放时会露出一个方形阴影
        Shadow.Width = w;
        Shadow.Height = h;
        Shadow.CornerRadius = new CornerRadius(radius);

        // 内容裁剪跟随胶囊内框，形变过程中内容不会溢出圆角
        double cw = Math.Max(0, w - 2);
        double ch = Math.Max(0, h - 2);
        _clip.Rect = new Rect(0, 0, cw, ch);
        _clip.RadiusX = _clip.RadiusY = Math.Max(0, Math.Min(radius - 1, ch / 2.0));
        ContentHost.Clip = _clip;
    }

    private void AnimateTo(double w, double h, double r)
    {
        _w.SetTarget(w);
        _h.SetTarget(h);
        _r.SetTarget(r);
        HookRendering();
    }

    // ================= 内容淡入淡出 =================

    private static void Fade(UIElement el, double to, double ms, double delayMs = 0)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(ms))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        el.BeginAnimation(OpacityProperty, anim);
    }

    private static void FadeIn(UIElement el, double delayMs)
    {
        el.Opacity = 0;

        var slide = new TranslateTransform(0, 7);
        el.RenderTransform = slide;

        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        el.BeginAnimation(OpacityProperty, fade);

        var move = new DoubleAnimation(7, 0, TimeSpan.FromMilliseconds(230))
        {
            BeginTime = TimeSpan.FromMilliseconds(delayMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        slide.BeginAnimation(TranslateTransform.YProperty, move);
    }

    // ================= 休眠 / 唤醒 / 隐藏 =================

    private void StartIdleTimer()
    {
        if (_idleTimer is not null) return;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) => OnIdleTick();
        _idleTimer.Start();
    }

    private void OnIdleTick()
    {
        if (_current is not null) return;
        if (_state is UiState.Alert or UiState.Result or UiState.Panel or UiState.Done) return;

        var idle = DateTime.UtcNow - _lastActivityUtc;

        if (!_dormant && DormantAfter > TimeSpan.Zero && idle >= DormantAfter)
            EnterDormant();

        if (!_hidden && HideAfter > TimeSpan.Zero && idle >= HideAfter)
            HideToTray();
    }

    /// <summary>没有任何事件时，把胶囊缩成一颗几乎看不见的小点 —— 不挡视线。</summary>
    private void EnterDormant()
    {
        if (_state != UiState.Idle) return;

        _dormant = true;
        _state = UiState.Dormant;

        AnimateTo(DormantW, DormantH, DormantR);
        Fade(IdleLayer, 0, 140);
        Fade(DormantDot, 1, 200, 90);
        Fade(Capsule, 0.55, 340);
        Fade(Shadow, 0.08, 340);
    }

    /// <summary>从休眠态长回待机胶囊。</summary>
    private void Wake()
    {
        _lastActivityUtc = DateTime.UtcNow;

        if (_hidden)
        {
            ShowFromTray();
            return;
        }

        if (!_dormant) return;

        _dormant = false;
        if (_state == UiState.Dormant) _state = UiState.Idle;

        AnimateTo(IdleW, IdleH, IdleR);
        Fade(Capsule, 1, 200);
        Fade(Shadow, 0.30, 200);
        Fade(DormantDot, 0, 120);
        FadeIn(IdleLayer, 40);
    }

    private void WakeFromAnywhere()
    {
        if (_hidden) ShowFromTray();
        else Wake();
    }

    /// <summary>长时间无事件：整颗胶囊收起来，只留托盘图标。任何请求都会自动把它叫回来。</summary>
    private void HideToTray()
    {
        if (_hidden) return;
        if (_current is not null) return;   // 有挂起请求时不允许藏起来

        _hidden = true;

        Fade(Capsule, 0, 180);
        Fade(Shadow, 0, 180);

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(240) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (_hidden) Hide();
        };
        timer.Start();
    }

    private void ShowFromTray()
    {
        _hidden = false;
        _dormant = false;

        if (!IsVisible) Show();
        PositionWindow();

        // 从休眠尺寸"长"出来，避免突然整块出现
        _w.Snap(DormantW);
        _h.Snap(DormantH);
        _r.Snap(DormantR);
        ApplyChrome();

        if (_state is UiState.Dormant or UiState.Panel) _state = UiState.Idle;

        AnimateTo(IdleW, IdleH, IdleR);
        Fade(Capsule, 1, 200);
        Fade(Shadow, 0.30, 200);
        Fade(DormantDot, 0, 100);
        FadeIn(IdleLayer, 50);

        _lastActivityUtc = DateTime.UtcNow;
    }

    // ================= 交互 =================

    private void OnCapsuleClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        _lastActivityUtc = DateTime.UtcNow;

        if (_hidden)
        {
            ShowFromTray();
            return;
        }

        switch (_state)
        {
            case UiState.Idle:
            case UiState.Dormant:
                ShowPanel();
                break;
            case UiState.Panel:
                HidePanel();
                break;
            case UiState.Done:
                // 点完成卡片 = 直达会话
                var sessionId = _done?.SessionId ?? "";
                DismissDone();
                OpenSession(sessionId);
                break;
        }
    }

    private void OnCapsuleEnter(object sender, MouseEventArgs e)
    {
        if (_state == UiState.Done)
        {
            // 鼠标停在通知上就别自动收，否则还没看清就没了
            _doneTimer?.Stop();
            return;
        }

        Wake();
    }

    private void OnCapsuleLeave(object sender, MouseEventArgs e)
    {
        if (_state == UiState.Done) StartDoneTimer(TimeSpan.FromSeconds(2));
    }

    // ================= 任务完成通知 =================

    private void OnNotifyReceived(DoneNotice notice)
    {
        Dispatcher.InvokeAsync(() =>
        {
            HookRunner.Trace($"[DONE] arrived summary='{TruncateForLog(notice.Summary)}' state={_state} " +
                             $"current={_current is not null} hidden={_hidden} dormant={_dormant} quiet={QuietWhenForeground}");

            // 正在等用户授权时不要打扰 —— 那张卡比"某个任务跑完了"更要紧
            if (_current is not null)
            {
                HookRunner.Trace("[DONE] skip: authorizing");
                return;
            }

            // 默认总是提示；只有显式设置 AUTH_ISLAND_NOTIFY_QUIET=1 才在 WorkBuddy 前台时安静
            if (QuietWhenForeground && IsAgentForeground())
            {
                HookRunner.Trace("[DONE] skip: quiet-when-foreground");
                return;
            }

            HookRunner.Trace("[DONE] show");
            _lastActivityUtc = DateTime.UtcNow;

            if (_hidden) ShowFromTray();

            if (_state == UiState.Panel)
            {
                _state = UiState.Idle;
                PanelLayer.IsHitTestVisible = false;
                Fade(PanelLayer, 0, 80);
            }

            ShowDone(notice);
        });
    }

    private static string TruncateForLog(string text) =>
        text.Length <= 40 ? text : text[..40] + "…";

    private void ShowDone(DoneNotice notice)
    {
        _done = notice;
        _dormant = false;

        string summary = notice.Summary.Trim();
        DoneSummary.Text = string.IsNullOrEmpty(summary) ? "可以在会话里查看结果" : summary;

        // 已经在显示完成卡（连续两个任务收尾）：直接换文案，重新计时，
        // 不重新走一遍入场动画，避免"又弹一次"的观感。
        if (_state == UiState.Done)
        {
            StartDoneTimer(DoneLinger);
            return;
        }

        _state = UiState.Done;

        AnimateTo(DoneW, DoneH, DoneR);
        Fade(Capsule, 1, 160);
        Fade(Shadow, 0.30, 160);
        Fade(DormantDot, 0, 80);
        Fade(IdleLayer, 0, 90);
        Fade(ResultLayer, 0, 90);

        IdleLayer.IsHitTestVisible = false;
        DoneLayer.IsHitTestVisible = true;
        FadeIn(DoneLayer, 50);

        StartDoneTimer(DoneLinger);
    }

    private void StartDoneTimer(TimeSpan after)
    {
        _doneTimer?.Stop();

        _doneTimer = new DispatcherTimer { Interval = after };
        _doneTimer.Tick += (_, _) =>
        {
            _doneTimer?.Stop();
            if (_state == UiState.Done) TransitionToIdle();
        };
        _doneTimer.Start();
    }

    private void DismissDone()
    {
        _doneTimer?.Stop();
        _done = null;
        if (_state == UiState.Done) TransitionToIdle();
    }

    /// <summary>
    /// 点通知 → 打开会话。用 WorkBuddy 自己的深链 workbuddy://chat/&lt;session_id&gt;，
    /// 拿不到 session_id 就退到 home；深链调不起来就退到"把窗口提到前台"。
    /// </summary>
    private void OpenSession(string sessionId)
    {
        string url = string.IsNullOrWhiteSpace(sessionId)
            ? "workbuddy://home"
            : $"workbuddy://chat/{Uri.EscapeDataString(sessionId)}";

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            FocusAgentWindow();
        }
    }

    private static bool IsAgentForeground()
    {
        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out uint pid);
        return IsAgentProcess(pid);
    }

    private static bool IsAgentProcess(uint pid)
    {
        if (pid == 0) return false;

        try
        {
            using var p = Process.GetProcessById((int)pid);
            return p.ProcessName.Contains("WorkBuddy", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>兜底：把 WorkBuddy 的主窗口提到前台（深链失败时至少让用户看到它）。</summary>
    private static void FocusAgentWindow()
    {
        var procs = Process.GetProcessesByName("WorkBuddy");
        try
        {
            foreach (var p in procs)
            {
                try
                {
                    if (p.MainWindowHandle == IntPtr.Zero) continue;

                    NativeMethods.ShowWindow(p.MainWindowHandle, NativeMethods.SW_RESTORE);
                    NativeMethods.SetForegroundWindow(p.MainWindowHandle);
                    return;
                }
                catch
                {
                    // 单个进程失败不影响继续找
                }
            }
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    private void ShowPanel()
    {
        if (_current is not null) return;

        _dormant = false;
        _state = UiState.Panel;
        RefreshPanel();

        AnimateTo(PanelW, PanelH, PanelR);
        Fade(Capsule, 1, 160);
        Fade(Shadow, 0.30, 160);
        Fade(DormantDot, 0, 100);
        Fade(IdleLayer, 0, 90);

        PanelLayer.IsHitTestVisible = true;
        FadeIn(PanelLayer, 60);
    }

    private void HidePanel()
    {
        if (_state != UiState.Panel) return;

        _state = UiState.Idle;
        PanelLayer.IsHitTestVisible = false;
        Fade(PanelLayer, 0, 110);

        AnimateTo(IdleW, IdleH, IdleR);
        FadeIn(IdleLayer, 40);

        _lastActivityUtc = DateTime.UtcNow;
    }

    private void RefreshPanel()
    {
        PanelPort.Text = _server.IsRunning ? $"127.0.0.1:{_server.Port}" : "服务未启动";
        PanelDot.Fill = _paused ? WarnBrush : OkBrush;
        PanelState.Text = _paused ? "已暂停" : "待机";
        PauseText.Text = _paused ? "恢复拦截" : "暂停拦截";
        SyncPauseMenuItem();

        if (_paused)
        {
            PanelStatus.Text = "已暂停拦截：新请求不再弹卡片，直接交回各 Agent 自己的审批流程。";
        }
        else if (_lastRequestUtc is null)
        {
            PanelStatus.Text = "还没有收到过授权请求。若 Agent 已启动仍无请求，检查 hook 配置是否已加载（改完配置必须完全重启 Agent）。";
        }
        else
        {
            PanelStatus.Text = $"最近一次授权请求：{Ago(_lastRequestUtc.Value)}";
        }
    }

    private static string Ago(DateTime utc)
    {
        var span = DateTime.UtcNow - utc;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} 分钟前";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} 小时前";
        return $"{(int)span.TotalDays} 天前";
    }

    private void SetPaused(bool paused)
    {
        _paused = paused;
        _server.Paused = paused;
        _tray?.SetPaused(paused);

        StatusDot.Fill = paused ? WarnBrush : OkBrush;

        if (_state == UiState.Panel) RefreshPanel();

        // 暂停时正挂着一张卡：按"不决定"收掉，让宿主走原生流程
        if (paused && _current is not null) DismissAsPass();
    }

    private void DismissAsPass()
    {
        var req = _current;
        if (req is null) return;

        _current = null;
        req.Completion.TrySetResult(new AuthDecision
        {
            Decision = "pass",
            Reason = "拦截已暂停"
        });

        TransitionToIdle();
    }

    private void ShutdownApp()
    {
        Application.Current.Shutdown();
    }

    private void OnMenuShowClick(object sender, RoutedEventArgs e) => WakeFromAnywhere();

    private void OnMenuPauseClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem item) SetPaused(item.IsChecked);
    }

    /// <summary>右键菜单里的勾选状态跟着 _paused 走（菜单项在 ContextMenu 自己的 namescope 里，拿不到字段）。</summary>
    private void SyncPauseMenuItem()
    {
        var menu = Capsule.ContextMenu;
        if (menu is null) return;

        foreach (var item in menu.Items.OfType<System.Windows.Controls.MenuItem>())
        {
            if (item.Tag as string != "pause") continue;
            if (item.IsChecked != _paused) item.IsChecked = _paused;
        }
    }

    private void OnExitClick(object sender, RoutedEventArgs e) => ShutdownApp();

    // ================= 请求流转 =================

    private void OnServerRequest(AuthRequest req)
    {
        Dispatcher.InvokeAsync(() =>
        {
            _lastRequestUtc = DateTime.UtcNow;
            _lastActivityUtc = DateTime.UtcNow;

            if (_hidden) ShowFromTray();

            // 面板开着的时候来了请求：直接切卡片，不先回待机
            if (_state == UiState.Panel)
            {
                _state = UiState.Idle;
                PanelLayer.IsHitTestVisible = false;
                Fade(PanelLayer, 0, 80);
            }

            // 完成通知还挂着：授权优先，把通知收掉
            if (_state == UiState.Done)
            {
                _doneTimer?.Stop();
                _done = null;
                _state = UiState.Idle;
                DoneLayer.IsHitTestVisible = false;
                Fade(DoneLayer, 0, 80);
            }

            _queue.Enqueue(req);

            if (_current is null && _state != UiState.Result)
                ShowNext();
        });
    }

    private void OnServerExpired(AuthRequest req)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (ReferenceEquals(_current, req))
            {
                _current = null;
                TransitionToIdle();
            }
            else
            {
                // 已经不在展示的请求（客户端断开 / 排队中被取消），从队列里摘掉
                if (_queue.Contains(req))
                {
                    var kept = _queue.Where(r => !ReferenceEquals(r, req)).ToArray();
                    _queue.Clear();
                    foreach (var r in kept) _queue.Enqueue(r);
                    QueueText.Text = _queue.Count > 0 ? $"+{_queue.Count} 待处理" : "";
                }
            }
        });
    }

    private void ShowNext()
    {
        if (_queue.Count == 0)
        {
            _current = null;
            TransitionToIdle();
            return;
        }

        _current = _queue.Dequeue();
        _state = UiState.Alert;
        _dormant = false;

        ToolNameText.Text = string.IsNullOrWhiteSpace(_current.Tool) ? "Tool" : _current.Tool;
        DetailText.Text = string.IsNullOrWhiteSpace(_current.Detail) ? "（无参数）" : _current.Detail;
        ApplyRiskChip(_current.Risk, _current.Reason);
        QueueText.Text = _queue.Count > 0 ? $"+{_queue.Count} 待处理" : "";

        AnimateTo(AlertW, AlertH, AlertR);

        Fade(Capsule, 1, 140);
        Fade(Shadow, 0.30, 140);
        Fade(DormantDot, 0, 80);
        Fade(IdleLayer, 0, 90);
        Fade(ResultLayer, 0, 90);
        Fade(DoneLayer, 0, 80);
        DoneLayer.IsHitTestVisible = false;

        IdleLayer.IsHitTestVisible = false;
        AlertLayer.IsHitTestVisible = true;
        FadeIn(AlertLayer, 60);

        ProgressFill.Opacity = 1;
        HookRendering();
    }

    /// <summary>
    /// 标题旁的风险标签。卡片只显示「谁 + 干什么」，这里补上「为什么值得看一眼」——
    /// 高危红、需确认黄。没有原因就不显示，免得空占位。
    /// </summary>
    private void ApplyRiskChip(string? risk, string? reason)
    {
        var level = (risk ?? "").Trim().ToLowerInvariant();
        var label = (reason ?? "").Trim();

        var (background, foreground, fallback) = level switch
        {
            "high" => ("#2AF87171", "#FCA5A5", "高危"),
            "medium" => ("#24FBBF24", "#FCD34D", "需确认"),
            _ => ("#1FFFFFFF", "#B8B8C6", ""),
        };

        if (label.Length == 0) label = fallback;
        if (label.Length == 0)
        {
            RiskChip.Visibility = Visibility.Collapsed;
            return;
        }

        RiskChip.Visibility = Visibility.Visible;
        RiskChip.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(background));
        RiskText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(foreground));
        RiskText.Text = label;
    }

    private void Resolve(bool allow)
    {
        var req = _current;
        if (req is null) return;

        _current = null;
        _state = UiState.Result;

        req.Completion.TrySetResult(new AuthDecision
        {
            Decision = allow ? "allow" : "deny",
            Reason = allow ? "用户在授权胶囊上点击了允许" : "用户在授权胶囊上点击了拒绝"
        });

        ShowResult(allow);
    }

    private void ShowResult(bool allow)
    {
        AlertLayer.IsHitTestVisible = false;
        Fade(AlertLayer, 0, 110);
        ProgressFill.Opacity = 0;

        if (allow)
        {
            ResultBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0x4A, 0xDE, 0x80));
            ResultGlyph.Stroke = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            ResultGlyph.Data = Geometry.Parse("M4,8.5 L7,11.5 L12.5,5.5");
            ResultText.Text = "已允许";
        }
        else
        {
            ResultBadge.Background = new SolidColorBrush(Color.FromArgb(0x22, 0xF8, 0x71, 0x71));
            ResultGlyph.Stroke = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            ResultGlyph.Data = Geometry.Parse("M5,5 L11,11 M11,5 L5,11");
            ResultText.Text = "已拒绝";
        }

        AnimateTo(ResultW, ResultH, ResultR);
        FadeIn(ResultLayer, 30);

        _resultTimer?.Stop();
        _resultTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1150) };
        _resultTimer.Tick += (_, _) =>
        {
            _resultTimer?.Stop();

            if (_queue.Count > 0) ShowNext();
            else TransitionToIdle();
        };
        _resultTimer.Start();
    }

    private void TransitionToIdle()
    {
        _state = UiState.Idle;
        _dormant = false;
        _lastActivityUtc = DateTime.UtcNow;

        Fade(AlertLayer, 0, 100);
        Fade(ResultLayer, 0, 100);
        Fade(PanelLayer, 0, 100);
        Fade(DoneLayer, 0, 100);
        AlertLayer.IsHitTestVisible = false;
        PanelLayer.IsHitTestVisible = false;
        DoneLayer.IsHitTestVisible = false;
        IdleLayer.IsHitTestVisible = true;
        ProgressFill.Opacity = 0;

        AnimateTo(IdleW, IdleH, IdleR);
        Fade(Capsule, 1, 140);
        Fade(DormantDot, 0, 100);
        FadeIn(IdleLayer, 50);
    }

    private void UpdateCountdown()
    {
        var req = _current;
        if (req is null) return;

        double remain = (req.Deadline - DateTime.UtcNow).TotalMilliseconds;
        if (remain < 0) remain = 0;

        CountdownText.Text = $"{Math.Ceiling(remain / 1000):0}s";

        double ratio = Math.Clamp(remain / req.TimeoutMs, 0, 1);
        ProgressFill.Width = Math.Max(0, (_w.Value - 2) * ratio);
        ProgressFill.Background = ratio < 0.22 ? DangerBrush : WarnBrush;
    }
}
