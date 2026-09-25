using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Interop;
using ClassroomBreakLock.Logging;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseButtonEventArgs = System.Windows.Input.MouseButtonEventArgs;
using MouseButtonState = System.Windows.Input.MouseButtonState;

namespace ClassroomBreakLock.Views;

/// <summary>
/// 桌面右下角悬浮的"下课"按钮。
///
/// 语义：老师下课离开时点击它 → 二次确认（上课时段内还有第三层）→ 立即上锁。
///
/// 多显示器：定位走 MonitorService，不用 SystemParameters.WorkArea（那个只有主屏）。
/// 支持自动记住所在屏，或在设置里强制指定某一块屏。
///
/// 靠边吸附：拖到屏幕左右边缘附近自动吸附，并可缩成一个小图标；鼠标移上去再展开。
/// </summary>
public partial class FloatingButtonWindow : Window
{
    private AppConfig _cfg;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastMouseMove = DateTime.Now;
    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private bool _suppressed;
    private DispatcherTimer? _bubbleTimer;
    private double _bubbleExtra;
    private bool _confirmOpen;

    /// <summary>当前所在的显示器（缓存，避免每次 tick 都枚举）。</summary>
    private MonitorInfo? _monitor;

    /// <summary>是否处于"靠边缩起"状态。</summary>
    private bool _collapsed;

    /// <summary>忽略鼠标离开触发的收起（拖动/确认框期间用）。</summary>
    private bool _holdExpanded;

    /// <summary>按钮区域的固定高度（按钮本身 + 上下留白）。</summary>
    private double ButtonAreaHeight => _cfg.FloatingButton.Size + 24;

    /// <summary>气泡显示时的最小窗口宽度。</summary>
    private const double BubbleMinWidth = 300;

    // 几何常量 ------------------------------------------------------------
    private const double ExpandPadding = 8;      // 展开时按钮周围的余量
    private const double CollapsedPadding = 4;   // 缩起时余量

    /// <summary>用户确认“下课”，请求立即上锁。</summary>
    public event Action? ClassDismissed;

    /// <summary>用户双击按钮，请求打开设置（需认证）。</summary>
    public event Action? SettingsRequested;

    /// <summary>位置或状态发生变化、需要写回配置时触发。</summary>
    public event Action? LayoutChanged;

    /// <summary>
    /// 第三层确认的判定：返回非 null 表示"现在正处于上课时段"，
    /// 需要在第二层确认之后再弹一次第三层确认（返回的字符串是时段名，用于提示）。
    /// </summary>
    public Func<string?>? DuringClassProbe { get; set; }

    public double ButtonSize => _cfg.FloatingButton.Size;

    /// <summary>当前实际展示的直径（缩起时是 CollapsedSize）。</summary>
    public double CurrentButtonSize => _collapsed
        ? _cfg.FloatingButton.CollapsedSize
        : _cfg.FloatingButton.Size;

    public FloatingButtonWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        DataContext = this;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) => TickIdle();

        // 可见性自愈：虚拟桌面切换会让窗口被 DWM 遮蔽，
        // 系统不会自动恢复。这里用比调度 tick 更快的节奏独立巡检，
        // 保证切回桌面后按钮能立刻出现，而不是等下一次调度。
        _visibilityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _visibilityTimer.Tick += (_, _) => EnsureVisibleIfNeeded();

        Loaded += (_, _) =>
        {
            RefreshMonitors();
            ApplyConfig(_cfg);
            _idleTimer.Start();
            _visibilityTimer.Start();
        };

        // 显示器热插拔：屏幕变了要重新定位，否则按钮可能跑到看不见的地方
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        Closed += (_, _) =>
        {
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= OnDisplayChanged;
            _visibilityTimer?.Stop();
        };
    }

    private readonly DispatcherTimer _visibilityTimer;

    private void OnDisplayChanged(object? sender, EventArgs e)
    {
        // 系统事件在别的线程上，切回 UI 线程再动窗口
        Dispatcher.InvokeAsync(() =>
        {
            Log.Info("检测到显示器配置变化，重新定位悬浮按钮");
            // 屏幕变了：缓存的显示器信息与 DPI 都失效，必须清掉
            MonitorService.InvalidateCache();
            RefreshMonitors();
            RepositionToCorner();
        }, DispatcherPriority.Background);
    }

    private void RefreshMonitors()
    {
        try
        {
            var monitors = MonitorService.Enumerate();
            _monitor = MonitorService.Resolve(_cfg, monitors);
            Log.Info($"悬浮按钮目标显示器：{_monitor.Display}（共 {monitors.Count} 块）");
        }
        catch (Exception ex)
        {
            Log.Warn($"解析显示器失败：{ex.Message}");
            _monitor = null;
        }
    }

    public void ApplyConfig(AppConfig cfg)
    {
        // ⚠️ 坑：调用方传进来的常常就是**同一个** AppConfig 实例（配置被就地修改后原对象回传），
        // 所以不能靠"新旧 cfg 对比"来判断设置有没有变——那样永远是 false，
        // 屏幕设置就永远不会生效。这里改为记录**上次实际用过的值**来比较。
        var targetChanged = cfg.FloatingButton.TargetScreenIndex != _appliedScreenIndex;
        var lastScreenChanged =
            cfg.FloatingButton.LastScreenDeviceName != _appliedLastScreenDevice;

        _cfg = cfg;
        DataContext = null;
        DataContext = this;

        ButtonText.Text = cfg.FloatingButton.Text;
        Visibility = cfg.FloatingButton.Enabled ? Visibility.Visible : Visibility.Collapsed;

        // 换配置时把气泡收掉，免得残留一个宽窗口
        Bubble.Visibility = Visibility.Collapsed;
        _bubbleExtra = 0;

        // 还原持久化的缩起状态
        _collapsed = cfg.FloatingButton.SnapToEdge
                     && cfg.FloatingButton.CollapseWhenSnapped
                     && cfg.FloatingButton.IsCollapsed
                     && cfg.FloatingButton.IsSnapped;

        // 还原"用户拖到过的自由位置"（吸附状态下不算自由位置）
        _hasFreePosition = cfg.FloatingButton.HasFreePosition && !cfg.FloatingButton.IsSnapped;

        // 屏幕设置变了（或自动模式记录的屏变了）就重新解析目标显示器。
        // 首次应用（哨兵值）不算"变化"，避免启动时打一条无意义的日志。
        var firstApply = _appliedScreenIndex == int.MinValue;
        if (!firstApply && (targetChanged || lastScreenChanged))
        {
            Log.Info($"显示器设置变化：目标序号 {_appliedScreenIndex} → {cfg.FloatingButton.TargetScreenIndex}，" +
                     $"记录屏 '{_appliedLastScreenDevice}' → '{cfg.FloatingButton.LastScreenDeviceName}'");
            RefreshMonitors();
        }
        else if (firstApply)
        {
            RefreshMonitors();
        }
        else
        {
            _monitor ??= MonitorService.Resolve(cfg);
        }

        _appliedScreenIndex = cfg.FloatingButton.TargetScreenIndex;
        _appliedLastScreenDevice = cfg.FloatingButton.LastScreenDeviceName;

        ApplyVisualState();

        // 自由位置直接落到存下来的坐标；否则走常规定位
        if (_hasFreePosition && _monitor is not null)
        {
            PlaceAtStoredFreePosition();
        }
        else
        {
            RepositionToCorner();
        }

        UpdateSubText();
    }

    /// <summary>把窗口摆到配置里存的自由坐标（配置存的是物理像素，相对目标屏工作区）。</summary>
    private void PlaceAtStoredFreePosition()
    {
        if (_monitor is null) return;

        var buttonSize = CurrentButtonSize;
        var pad = _collapsed ? CollapsedPadding : ExpandPadding;
        var contentWidth = buttonSize + pad * 2;
        Width = Math.Max(contentWidth, _bubbleExtra > 0 ? BubbleMinWidth : contentWidth);
        Height = contentWidth + _bubbleExtra;

        var scale = MonitorService.GetPrimaryScale();
        var wa = _monitor.WorkArea;   // 物理像素

        // 配置里存的是物理像素偏移 → 绝对物理坐标 → 转 WPF 单位
        var physLeft = wa.Left + _cfg.FloatingButton.FreeX;
        var physTop = wa.Top + _cfg.FloatingButton.FreeY;
        Left = MonitorService.ToWpfUnits(physLeft, scale);
        Top = MonitorService.ToWpfUnits(physTop, scale);

        ClampToMonitorWpf();
    }

    /// <summary>上次应用配置时实际生效的显示器设置（用于检测变化）。</summary>
    private int _appliedScreenIndex = int.MinValue;
    private string _appliedLastScreenDevice = "\u0000";
    /// <summary>按缩起/展开状态刷新按钮尺寸与视觉。</summary>
    private void ApplyVisualState()
    {
        var size = CurrentButtonSize;
        MainButton.Width = size;
        MainButton.Height = size;

        if (_collapsed)
        {
            // 缩起：只留一个小圆点，文字全部隐藏
            ButtonContent.Visibility = Visibility.Collapsed;
            CollapsedDot.Visibility = Visibility.Visible;
            MainButton.ToolTip = "点击展开“下课”按钮";
        }
        else
        {
            ButtonContent.Visibility = Visibility.Visible;
            CollapsedDot.Visibility = Visibility.Collapsed;
            ButtonText.Text = _cfg.FloatingButton.Text;
            MainButton.ToolTip = "点击后确认下课，随即锁定多媒体";
        }

        // 缩起时按"缩起不透明度"另外压暗，更不打扰课堂
        Opacity = _collapsed
            ? Math.Max(0.1, _cfg.FloatingButton.Opacity * _cfg.FloatingButton.FadeToRatio)
            : _cfg.FloatingButton.Opacity;
    }

    /// <summary>
    /// 按当前状态重新计算窗口位置。
    ///
    /// ⚠️ 两件必须记住的事：
    ///
    /// 1. **坐标空间**：MonitorInfo 里全是**物理像素**，而 Window.Left/Top 是
    ///    **WPF 逻辑单位**（以主屏 DPI 为基准）。所以赋值前一定要过 `ToWpf`。
    ///    混合 DPI 多屏下漏了这步，窗口就会跑到屏幕外——这正是之前
    ///    "设置屏幕不生效 / 按钮消失"的根因。
    ///
    /// 2. **自由摆放**：只有吸附状态才从配置边距推算位置。用户拖到屏幕中间后，
    ///    任何一次重新定位都不能把它打回配置的右下角。
    /// </summary>
    private void RepositionToCorner()
    {
        if (_monitor is null) RefreshMonitors();
        if (_monitor is null) return;

        var fb = _cfg.FloatingButton;
        var wa = _monitor.WorkArea;          // 物理像素
        var scale = MonitorService.GetPrimaryScale();

        var buttonSize = CurrentButtonSize;
        var pad = _collapsed ? CollapsedPadding : ExpandPadding;
        var contentWidth = buttonSize + pad * 2;
        var oldWidth = Width;
        var oldHeight = Height;
        Width = Math.Max(contentWidth, _bubbleExtra > 0 ? BubbleMinWidth : contentWidth);
        Height = contentWidth + _bubbleExtra;

        // 窗口在 WPF 单位下的尺寸
        var wpfW = Width;
        var wpfH = Height;

        double physLeft, physTop;

        if (fb.IsSnapped)
        {
            // 已吸附：紧贴对应的左右边缘（垂直位置用配置的下边距）
            if (fb.SnappedEdge == "left")
            {
                physLeft = wa.Left + 2;
                MainButton.HorizontalAlignment = HorizontalAlignment.Left;
                Bubble.HorizontalAlignment = HorizontalAlignment.Left;
            }
            else
            {
                physLeft = wa.Right - MonitorService.ToPhysicalPixels(wpfW, scale) - 2;
                MainButton.HorizontalAlignment = HorizontalAlignment.Right;
                Bubble.HorizontalAlignment = HorizontalAlignment.Right;
            }
            physTop = wa.Bottom - MonitorService.ToPhysicalPixels(wpfH, scale)
                      - MonitorService.ToPhysicalPixels(fb.MarginBottom, scale);
        }
        else if (_hasFreePosition)
        {
            // 自由摆放：保持用户拖到的位置，尺寸变化时按中心点不变修正
            var centerX = Left + oldWidth / 2;
            var centerY = Top + oldHeight / 2;
            Left = centerX - wpfW / 2;
            Top = centerY - wpfH / 2;

            MainButton.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.HorizontalAlignment = HorizontalAlignment.Right;
            ClampToMonitorWpf();
            return;
        }
        else
        {
            // 从未拖动过：按配置的右下角边距摆放
            physLeft = wa.Right - MonitorService.ToPhysicalPixels(wpfW, scale)
                       - MonitorService.ToPhysicalPixels(fb.MarginRight, scale);
            physTop = wa.Bottom - MonitorService.ToPhysicalPixels(wpfH, scale)
                      - MonitorService.ToPhysicalPixels(fb.MarginBottom, scale);
            MainButton.HorizontalAlignment = HorizontalAlignment.Right;
            Bubble.HorizontalAlignment = HorizontalAlignment.Right;
        }

        // 直接用 Win32 按**物理像素**摆放窗口。
        //
        // 为什么不用 WPF 的 Left/Top：
        //   混合 DPI 多屏下 WPF 的逻辑坐标换算规则复杂，实测出现过
        //   "日志算对了 (1796,844)、窗口实际却在 (2948,1093)" 的情况。
        //   SetWindowPos 收物理坐标，所见即所得，不受 WPF 坐标空间影响。
        ApplyPhysicalPosition(physLeft, physTop, wpfW, wpfH, scale);

        // 同时把 WPF 的 Left/Top 也同步一份，保持两边一致
        // （某些 WPF 内部逻辑如 DragMove 会读它）
        Left = MonitorService.ToWpfUnits(physLeft, scale);
        Top = MonitorService.ToWpfUnits(physTop, scale);

        // 定位详情只在 Debug 级别记录，避免高频刷屏
        if (Log.IsDebugEnabled)
        {
            Log.Debug($"[定位] 屏={_monitor.DeviceName} 工作区(物理)=({wa.Left},{wa.Top})-({wa.Right},{wa.Bottom}) " +
                      $"主屏缩放={scale:0.##} 窗口逻辑={wpfW:0}x{wpfH:0} " +
                      $"=> 物理位置=({physLeft:0},{physTop:0}) " +
                      $"模式={(_hasFreePosition ? "自由" : fb.IsSnapped ? "吸附" : "边距")}");
        }

        // 记录当前所在屏，供"自动"模式下次恢复
        if (_monitor.DeviceName != fb.LastScreenDeviceName)
        {
            fb.LastScreenDeviceName = _monitor.DeviceName;
            LayoutChanged?.Invoke();
        }
    }

    /// <summary>
    /// 用 Win32 把窗口摆到物理坐标，尺寸也一并按物理像素设定。
    /// 这样跨屏、混合 DPI 都不会出现"算对了但落错地方"。
    /// </summary>
    private void ApplyPhysicalPosition(double physLeft, double physTop,
        double wpfWidth, double wpfHeight, double scale)
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            var wPhys = (int)Math.Round(MonitorService.ToPhysicalPixels(wpfWidth, scale));
            var hPhys = (int)Math.Round(MonitorService.ToPhysicalPixels(wpfHeight, scale));

            NativeMethods.MoveToPhysical(hwnd,
                (int)Math.Round(physLeft), (int)Math.Round(physTop), wPhys, hPhys);
        }
        catch (Exception ex)
        {
            Log.Warn($"按物理坐标摆放悬浮按钮失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 把窗口夹回目标屏的工作区内（在全都在 WPF 单位下做）。
    /// </summary>
    private void ClampToMonitorWpf()
    {
        if (_monitor is null) return;
        var scale = MonitorService.GetPrimaryScale();
        var waWpf = new Rect(
            MonitorService.ToWpfUnits(_monitor.WorkArea.Left, scale),
            MonitorService.ToWpfUnits(_monitor.WorkArea.Top, scale),
            MonitorService.ToWpfUnits(_monitor.WorkArea.Width, scale),
            MonitorService.ToWpfUnits(_monitor.WorkArea.Height, scale));

        var x = Math.Clamp(Left, waWpf.Left, Math.Max(waWpf.Left, waWpf.Right - Width));
        var y = Math.Clamp(Top, waWpf.Top, Math.Max(waWpf.Top, waWpf.Bottom - Height));
        Left = x;
        Top = y;
    }

    /// <summary>用户是否已把按钮拖到自定义位置（true 时不再用配置边距覆盖）。</summary>
    private bool _hasFreePosition;

    /// <summary>浮标下方的小字，显示当前状态或距离下节课的时间。</summary>
    public void UpdateSubText(string? text = null)
    {
        if (_collapsed)
        {
            ButtonSubText.Visibility = Visibility.Collapsed;
            return;
        }
        ButtonSubText.Text = text ?? "";
        ButtonSubText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>显示一个气泡提示，几秒后自动消失。</summary>
    public void ShowBubble(string text, int seconds = 4)
    {
        // 缩起状态下先展开，否则气泡没地方显示
        if (_collapsed) Expand(hold: true);

        BubbleText.Text = text;
        Bubble.Visibility = Visibility.Visible;

        Bubble.Measure(new Size(BubbleMinWidth - 24, double.PositiveInfinity));
        _bubbleExtra = Math.Ceiling(Bubble.DesiredSize.Height) + 10;
        RepositionToCorner();
        UpdateLayout();

        _bubbleTimer?.Stop();
        _bubbleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Max(2, seconds)) };
        _bubbleTimer.Tick += (_, _) => HideBubble();
        _bubbleTimer.Start();
    }

    private void HideBubble()
    {
        _bubbleTimer?.Stop();
        _bubbleTimer = null;
        Bubble.Visibility = Visibility.Collapsed;
        _bubbleExtra = 0;
        RepositionToCorner();
    }

    /// <summary>锁屏期间隐藏自己，避免和锁屏层抢视觉。</summary>
    public void SetSuppressed(bool suppressed)
    {
        _suppressed = suppressed;
        if (suppressed)
        {
            Visibility = Visibility.Collapsed;
        }
        else
        {
            Visibility = _cfg.FloatingButton.Enabled ? Visibility.Visible : Visibility.Collapsed;
            _needsRecovery = false;   // 主动显示过就不需要恢复了
        }
    }

    // ---------------- 虚拟桌面 / 可见性恢复 ----------------

    /// <summary>窗口被系统隐藏后需要恢复（切换虚拟桌面 / 会话切换会触发）。</summary>
    private bool _needsRecovery;

    /// <summary>
    /// 自检并恢复窗口可见性。
    ///
    /// 为什么需要这个：
    ///   切换虚拟桌面时，不在当前桌面上的窗口会被 DWM **cloak**（遮蔽，实测 Cloaked=2）。
    ///   此时 `IsWindowVisible()` 依然返回 true，WPF 的 `Visibility` 也还是 Visible，
    ///   但窗口实际看不见——表现就是"切个桌面回来，下课按钮消失了"。
    ///
    /// 策略：一旦发现被遮蔽就记下来；等不再遮蔽时，主动把 HWND 重新显示并重申置顶。
    /// 日志做了节流，避免在别的桌面上停留时刷屏。
    /// </summary>
    public void EnsureVisibleIfNeeded()
    {
        // 锁屏期间或配置里关掉了，就不该显示——这不是"被隐藏"，是预期行为
        if (_suppressed) return;
        if (!_cfg.FloatingButton.Enabled) return;
        if (!IsLoaded) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var cloaked = NativeMethods.IsWindowCloaked(hwnd);

        if (cloaked)
        {
            // 被遮蔽：记下状态，但日志只打一次，别每 400ms 刷一条
            if (!_needsRecovery)
            {
                _needsRecovery = true;
                _lastCloakLogAt = DateTime.Now;
                Log.Info("悬浮按钮被系统遮蔽（切换到了其它虚拟桌面），将在回到本桌面时恢复");
            }
            else if ((DateTime.Now - _lastCloakLogAt).TotalMinutes >= 5)
            {
                _lastCloakLogAt = DateTime.Now;
                Log.Info("悬浮按钮仍处于遮蔽状态（已在其它虚拟桌面停留较久）");
            }
            return;
        }

        // 不遮蔽了。若之前被遮蔽过，或 HWND 本身不可见，就恢复一次。
        var hwndVisible = NativeMethods.IsWindowVisible(hwnd);
        if (_needsRecovery || !hwndVisible)
        {
            var reason = _needsRecovery ? "已回到所在虚拟桌面" : "窗口被系统隐藏";
            _needsRecovery = false;
            RecoverWindow(hwnd, reason);
        }
    }

    private DateTime _lastCloakLogAt = DateTime.MinValue;

    /// <summary>把被系统隐藏的窗口重新显示出来，并重申置顶。</summary>
    private void RecoverWindow(IntPtr hwnd, string reason)
    {
        try
        {
            // SW_SHOWNOACTIVATE：显示但不抢焦点，避免打断老师正在用的程序
            NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWNOACTIVATE);
            NativeMethods.PinTopMost(hwnd);

            // WPF 内部状态也可能没同步，强制刷一次可见性。
            // 注意用 try：极端情况下（窗口正在关闭）设置会抛，不该影响主流程。
            try
            {
                if (Visibility != Visibility.Visible) Visibility = Visibility.Visible;
            }
            catch { }

            _lastRecoverAt = DateTime.Now;
            _recoverCount++;
            Log.Info($"悬浮按钮已恢复显示（{reason}，累计第 {_recoverCount} 次）");
        }
        catch (Exception ex)
        {
            Log.Warn($"恢复悬浮按钮显示失败：{ex.Message}");
        }
    }

    private DateTime _lastRecoverAt = DateTime.MinValue;
    private int _recoverCount;

    /// <summary>是否正在拖动（宿主用来跳过拖动期间的可见性巡检等重活）。</summary>
    public bool IsDragging => _dragging || _pendingDragCheck;

    /// <summary>最近一次恢复的时间与次数（供设置页自检显示）。</summary>
    public (DateTime At, int Count) RecoveryStats => (_lastRecoverAt, _recoverCount);

    // ---------------- 缩起 / 展开 ----------------

    /// <summary>缩成小图标并贴边。</summary>
    private void Collapse(bool snap)
    {
        if (_collapsed) return;

        var fb = _cfg.FloatingButton;
        _collapsed = true;

        if (snap)
        {
            // 按当前横坐标判断贴哪一边
            var wa = _monitor?.WorkArea ?? SystemParameters.WorkArea;
            var centerX = Left + Width / 2;
            fb.SnappedEdge = centerX < wa.Left + wa.Width / 2 ? "left" : "right";
        }

        fb.IsCollapsed = true;
        ApplyVisualState();
        RepositionToCorner();
        LayoutChanged?.Invoke();

        Log.Info($"悬浮按钮已缩起（贴 {(fb.SnappedEdge == "left" ? "左" : "右")}边），尺寸 {CurrentButtonSize}");
    }

    /// <summary>展开成正常大小。</summary>
    private void Expand(bool hold = false)
    {
        if (!_collapsed && !hold) return;

        var wasCollapsed = _collapsed;
        _collapsed = false;
        _cfg.FloatingButton.IsCollapsed = false;

        if (wasCollapsed)
        {
            ApplyVisualState();
            RepositionToCorner();
            LayoutChanged?.Invoke();
            Log.Info("悬浮按钮已展开");
        }

        if (hold) _holdExpanded = true;
    }

    private void TickIdle()
    {
        if (!IsVisible || _suppressed) return;
        var fb = _cfg.FloatingButton;

        // 缩起状态：保持较暗，不参与淡出逻辑
        if (_collapsed)
        {
            Opacity = Math.Max(0.1, fb.Opacity * fb.FadeToRatio);
            return;
        }

        if (fb.FadeAfterIdleSeconds <= 0)
        {
            Opacity = fb.Opacity;
            return;
        }

        var idle = (DateTime.Now - _lastMouseMove).TotalSeconds;
        // 鼠标靠近时恢复全亮，长时间不动就淡下去，不挡课件
        if (idle > fb.FadeAfterIdleSeconds)
            Opacity = Math.Max(0.1, fb.Opacity * fb.FadeToRatio);
        else
            Opacity = fb.Opacity;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _lastMouseMove = DateTime.Now;
        Opacity = _cfg.FloatingButton.Opacity;

        // ⚠️ 这里**不要**调用 SetWindowPos/PinTopMost。
        //    鼠标移入时改变 Z 序会打断正在进行的鼠标消息序列，
        //    导致随后的 MouseLeftButtonDown 收不到——表现就是"按钮点不动、拖不动"。
        //    置顶由 _visibilityTimer 定期重申即可，不差这一下。

        // 有意不做任何展开动作：吸附后的展开改由单击驱动（见 ToggleSnapExpansion），
        // 不做悬停判定，从结构上消除"缩起↔展开"横跳。
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _lastMouseMove = DateTime.Now;

        if (_holdExpanded)
        {
            _holdExpanded = false;
            return;
        }

        // 有意不做任何缩起动作：先前的"鼠标移开就缩回"正是横跳的来源之一。
        // 现在只有单击才会改变吸附后的缩起状态。
    }

    // ---------------- 吸附后的展开/缩起 ----------------
    //
    // 设计（按用户要求）：**吸附到边缘后，只要不左键点击，就一直保持缩起状态。**
    //
    // 历史教训：之前用"鼠标悬停展开、移开缩起"的交互，但缩起是 28px、展开是 84px，
    // 且展开方向与贴边方向相关，导致鼠标停在原地不动时也会
    // 「缩起 → 鼠标落在圆点内 → 展开 → 鼠标落到窗口外 → 缩起」无限横跳。
    //
    // 现在改为**点击驱动**：吸附缩起后，只有单击才展开，再单击（或点击后移开）
    // 才缩回。不依赖任何鼠标位置判定，结构上就不可能出现振荡。
    //
    // _holdExpanded：点击展开后的保持标志。展开状态下再次点击按钮即缩回。

    /// <summary>
    /// 点击驱动：在吸附缩起与展开之间切换。
    /// 返回 true 表示这次点击被"展开/缩起"消费掉了，不应再触发"下课"确认。
    /// </summary>
    private bool ToggleSnapExpansion()
    {
        var fb = _cfg.FloatingButton;

        // 没启用吸附、或当前没贴边，就不接管点击
        if (!fb.SnapToEdge || !fb.CollapseWhenSnapped || !fb.IsSnapped) return false;

        if (_collapsed)
        {
            Expand(hold: true);   // 点击展开，并保持展开
            Log.Info("点击展开吸附的悬浮按钮");
        }
        else
        {
            Collapse(snap: false);
            Log.Info("点击缩回吸附的悬浮按钮");
        }
        return true;
    }

    private void OnClick(object sender, RoutedEventArgs e)
    {
        // 刚拖完不要触发"下课"。
        // 用时间戳判断而不是 _dragging 标志：Click 与拖动收尾的时序在不同机器上
        // 不一定谁先谁后，时间窗口更可靠。
        if (_dragging || (DateTime.Now - _lastDragEndAt).TotalMilliseconds < 300)
        {
            Log.Info("拖动刚结束，忽略本次点击");
            return;
        }

        // 吸附状态下：单击只在「缩起/展开」之间切换，不触发"下课"。
        // 这是按需求定的交互——吸附后不点击就一直是缩起状态，
        // 点击是唯一能改变它的操作，且不会误触发锁屏。
        if (ToggleSnapExpansion()) return;

        // 确认框已经开着就忽略重复点击，免得叠出好几个
        if (_confirmOpen) return;
        _confirmOpen = true;

        try
        {
            Log.Info("用户点击了悬浮“下课”按钮，等待二次确认");

            // ---------- 第二层：确认下课 ----------
            bool ok = AppDialog.Confirm(
                this,
                "锁屏后需要使用 U 盘、密码或动态码才能解锁。",
                "确认要下课吗？",
                "下课锁屏",
                "取消");

            if (!ok)
            {
                Log.Info("用户在二次确认中取消了下课锁屏");
                return;
            }

            // ---------- 第三层：上课时段内下课，反常操作，再确认一次 ----------
            var currentPeriod = DuringClassProbe?.Invoke();
            if (currentPeriod is not null)
            {
                Log.Warn($"检测到在上课时段「{currentPeriod}」内请求下课，触发第三层确认");

                bool ok2 = AppDialog.Confirm(
                    this,
                    $"当前正处于「{currentPeriod}」上课时段，这个时间下课属于反常操作。\n\n" +
                    "确认后屏幕将立即锁定。如果只是误触，请选择「取消」。",
                    "确认在上课时间下课吗？",
                    "确认下课",
                    "取消",
                    danger: true);

                if (!ok2)
                {
                    Log.Info("用户在上课时间确认中取消了下课锁屏");
                    return;
                }

                Log.Audit("下课按钮", true, $"上课时段「{currentPeriod}」内确认下课，强制上锁");
            }
            else
            {
                Log.Audit("下课按钮", true, "非上课时段确认下课，上锁");
            }

            Log.Info("用户确认下课，请求立即上锁");
            ClassDismissed?.Invoke();
        }
        finally
        {
            _confirmOpen = false;
        }
    }

    // ---------------- 拖动 ----------------
    //
    // 为什么用 Preview（隧道）事件 + Win32 轮询：
    //
    //   1. WPF 的 Button 会在冒泡阶段把 MouseLeftButtonDown 转成自己的 Click 逻辑
    //      并标记 Handled，挂在 Button 上的普通 MouseLeftButtonDown 收不到。
    //      实测证据：Win32 的 WM_LBUTTONDOWN 已到达窗口，但 OnDragStart 从未执行。
    //      → 改用 PreviewMouseLeftButtonDown（隧道阶段，先于 Button 处理）
    //
    //   2. Button 自己会做鼠标捕获，MouseMove/MouseLeftButtonUp 不可靠。
    //      → 按下后改用 16ms 定时器轮询 Win32 按键状态与光标位置自行推进。

    /// <summary>拖动期间的轮询计时器（16ms ≈ 60fps）。</summary>
    private DispatcherTimer? _dragTimer;

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        Log.Info($"悬浮按钮 PreviewMouseDown：ClickCount={e.ClickCount} " +
                 $"Draggable={_cfg.FloatingButton.Draggable} 当前物理位置={GetWindowPhysicalPosition()}");

        if (!_cfg.FloatingButton.Draggable)
        {
            Log.Warn("拖动被拒绝：配置里 Draggable=false");
            return;
        }

        if (e.ClickCount >= 2)
        {
            // 双击进设置
            SettingsRequested?.Invoke();
            return;
        }

        _dragging = false;
        _holdExpanded = true;
        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragStartPhys = GetWindowPhysicalPosition();

        // ⚠️ 关键：**不能**在这里直接把窗口交给系统拖动。
        //
        //    一旦调用 BeginNativeDragMove，系统会接管整个鼠标交互，
        //    鼠标抬起被系统消费，WPF 收不到 Click —— 结果就是"单击无法锁定下课"。
        //    （实测日志：每次单击都打出「进入系统原生移动循环」，然后没有任何 Click）
        //
        //    正确做法：先按住观察几帧，确认位移超过阈值、确实是拖动意图，
        //    这时才移交系统。在那之前保持"可能是点击"的状态。
        _pendingDragCheck = true;

        _dragTimer?.Stop();
        _dragTimer = new DispatcherTimer(DispatcherPriority.Input)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _dragTimer.Tick += (_, _) => CheckDragIntent();
        _dragTimer.Start();
    }

    /// <summary>是否在等待判断"这次按下是点击还是拖动"。</summary>
    private bool _pendingDragCheck;

    /// <summary>
    /// 判断拖动意图：位移超过阈值才交给系统拖动；
    /// 左键先松开则是单击，什么都不做，让 Button 正常触发 Click。
    /// </summary>
    private void CheckDragIntent()
    {
        // 左键已松开 → 是单击，收工（不拦截，Click 会正常触发）
        if (!NativeMethods.IsLeftButtonDown())
        {
            _dragTimer?.Stop();
            _dragTimer = null;
            _pendingDragCheck = false;
            _holdExpanded = false;
            Log.Info("判定为单击（未超过拖动阈值），交给按钮处理");
            return;
        }

        if (!NativeMethods.GetCursorPos(out var p)) return;

        var dx = p.X - _dragStartCursor.X;
        var dy = p.Y - _dragStartCursor.Y;

        if (Math.Abs(dx) <= DragThresholdPx && Math.Abs(dy) <= DragThresholdPx) return;

        // 确认是拖动：停掉观察计时器，把窗口交给系统移动循环
        _dragTimer?.Stop();
        _dragTimer = null;
        _pendingDragCheck = false;
        _dragging = true;
        _hasFreePosition = true;
        if (_collapsed) Expand(hold: true);

        Log.Info($"确认拖动意图（位移 dx={dx} dy={dy}），进入自绘拖动循环");

        // ⚠️ 这里**不用**系统的模态移动循环（SendMessage WM_NCLBUTTONDOWN / HTCAPTION）。
        //
        //    系统移动循环会运行一个**嵌套消息泵**，它与 WPF 的 Dispatcher 抢消息，
        //    实测表现是拖动一顿一顿、甚至要使劲拖才动。
        //    改用我们自己的轮询：不阻塞 Dispatcher，节奏完全由我们控制。
        SuspendBackgroundTimersDuringDrag();
        StartDragPolling();
    }

    /// <summary>
    /// 启动拖动轮询。
    ///
    /// 频率与优先级的选择（踩过坑，别随手改）：
    ///   · 曾用 DispatcherPriority.Render + 8ms —— **跨屏拖动时整个程序卡死**。
    ///     原因：跨屏会让窗口在不同 D3D 设备间迁移，这是同步阻塞的；
    ///     Render 优先级又高于输入处理，高频 tick 叠加阻塞会把消息泵彻底饿死，
    ///     连"未响应"提示都弹不出来。
    ///   · 现在用 Normal 优先级 + 16ms（约 60fps）：与显示器刷新率对齐，
    ///     低于输入处理优先级，保证鼠标/键盘消息始终能被处理。
    /// </summary>
    private void StartDragPolling()
    {
        _dragStartPhys = GetWindowPhysicalPosition();
        NativeMethods.GetCursorPos(out _dragStartCursor);

        // 重置性能统计
        _dragMoves = 0;
        _dragMoveCostTotal = 0;
        _dragMoveCostMax = 0;
        _lastDragX = double.MinValue;
        _lastDragY = double.MinValue;

        _dragTimer?.Stop();
        _dragTimer = new DispatcherTimer(DispatcherPriority.Normal)
        {
            Interval = TimeSpan.FromMilliseconds(DragPollIntervalMs)
        };
        _dragTimer.Tick += (_, _) => PollDrag();
        _dragTimer.Start();
    }

    /// <summary>拖动轮询间隔（毫秒）。16ms ≈ 60fps，与常见刷新率对齐。</summary>
    private const int DragPollIntervalMs = 16;

    /// <summary>判定为拖动所需的位移阈值（像素）。</summary>
    private const int DragThresholdPx = 5;

    /// <summary>拖动期间被暂停的计时器，松手后恢复。</summary>
    private readonly List<(DispatcherTimer Timer, bool WasEnabled)> _suspendedTimers = new();

    /// <summary>
    /// 拖动期间暂停一切非必要的后台计时器，把 UI 线程让给窗口移动。
    /// 400ms 的可见性巡检里有一次跨进程的 DwmGetWindowAttribute 调用，
    /// 会和窗口移动抢主线程。
    /// </summary>
    private void SuspendBackgroundTimersDuringDrag()
    {
        _suspendedTimers.Clear();

        void Suspend(DispatcherTimer? t)
        {
            if (t is null) return;
            _suspendedTimers.Add((t, t.IsEnabled));
            t.Stop();
        }

        Suspend(_visibilityTimer);   // 内含跨进程 DWM 调用，最重
        Suspend(_idleTimer);         // 淡出逻辑
        _bubbleTimer?.Stop();
    }

    /// <summary>恢复拖动期间暂停的计时器。</summary>
    private void ResumeBackgroundTimersAfterDrag()
    {
        foreach (var (timer, wasEnabled) in _suspendedTimers)
        {
            if (wasEnabled && !timer.IsEnabled) timer.Start();
        }
        _suspendedTimers.Clear();
    }

    /// <summary>
    /// 拖动轮询：读光标 → 换算位移 → 直接搬窗口。
    /// 全程物理坐标，不经过 WPF 的 Left/Top（那个在混合 DPI 下不可靠）。
    /// </summary>
    private void PollDrag()
    {
        // 左键已松开 → 收尾
        if (!NativeMethods.IsLeftButtonDown())
        {
            EndDragInternal();
            return;
        }

        if (!NativeMethods.GetCursorPos(out var p)) return;

        var dx = p.X - _dragStartCursor.X;
        var dy = p.Y - _dragStartCursor.Y;

        var px = _dragStartPhys.X + dx;
        var py = _dragStartPhys.Y + dy;

        // 位置没变就不碰 Win32：鼠标静止时省掉全部无谓调用
        if (Math.Abs(px - _lastDragX) < 0.5 && Math.Abs(py - _lastDragY) < 0.5) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        // ⚠️ 跨屏保护：如果这一步会把窗口挪到**另一块显示器**上，
        //    先跳过这一帧，让 WPF/DWM 有时间完成渲染目标迁移。
        //
        //    跨屏时窗口要在不同的 D3D 设备间重建交换链，是同步重活；
        //    如果每 16ms 都触发一次，消息泵会被饿死 —— 实测表现为
        //    "从屏幕1拖到屏幕2时整个程序卡死（未响应）"。
        //    跳帧后下一轮再试，用少量延迟换取不卡死。
        if (IsCrossingMonitor(px, py))
        {
            _crossSkipCount++;
            if (_crossSkipCount <= 3)
            {
                Log.Info($"跨屏拖动：跳过本帧，等待渲染目标迁移（第 {_crossSkipCount} 次）");
            }
            // 跨屏那一帧只记录不移动，避免阻塞
            _lastDragX = px;
            _lastDragY = py;
            return;
        }
        _crossSkipCount = 0;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var ok = NativeMethods.MoveOnlyPhysical(hwnd, (int)Math.Round(px), (int)Math.Round(py));
        sw.Stop();

        _lastDragX = px;
        _lastDragY = py;
        _dragMoves++;
        _dragMoveCostTotal += sw.Elapsed.TotalMilliseconds;
        _dragMoveCostMax = Math.Max(_dragMoveCostMax, sw.Elapsed.TotalMilliseconds);

        // 单次移动耗时异常大（>50ms）说明这次触发了重活（多半是跨屏迁移），
        // 记一笔便于排查；同时把耗时计入统计。
        if (sw.Elapsed.TotalMilliseconds > 50)
        {
            Log.Warn($"拖动帧耗时异常：{sw.Elapsed.TotalMilliseconds:0.0}ms（可能触发了跨屏渲染迁移）");
        }

        // 兜底：如果已经卡到离谱（比如某次调用阻塞了几百毫秒），
        // 主动结束本次拖动，避免整个程序变成"未响应"。
        if (sw.Elapsed.TotalMilliseconds > 400)
        {
            Log.Error($"拖动单帧耗时 {sw.Elapsed.TotalMilliseconds:0}ms，超过安全阈值，主动中止拖动以防卡死");
            EndDragInternal();
        }
    }

    /// <summary>连续跳过的跨屏帧数。</summary>
    private int _crossSkipCount;

    /// <summary>
    /// 判断把窗口移到 (x,y) 是否会跨越显示器边界。
    /// 用窗口中心点判定，避免边缘擦碰造成误判。
    /// </summary>
    private bool IsCrossingMonitor(double newX, double newY)
    {
        try
        {
            var scale = MonitorService.GetPrimaryScale();
            var wPhys = MonitorService.ToPhysicalPixels(Width, scale);
            var hPhys = MonitorService.ToPhysicalPixels(Height, scale);

            var center = new System.Windows.Point(newX + wPhys / 2, newY + hPhys / 2);
            var monitors = MonitorService.EnumerateCached();
            var target = MonitorService.HitTest(monitors, center);

            if (target is null) return false;

            // 目标屏和当前记录的屏不同 → 是跨屏
            return _monitor is not null && target.DeviceName != _monitor.DeviceName;
        }
        catch
        {
            return false;
        }
    }

    private double _lastDragX = double.MinValue;
    private double _lastDragY = double.MinValue;
    private int _dragMoves;
    private double _dragMoveCostTotal;
    private double _dragMoveCostMax;

    /// <summary>拖动开始时缓存窗口尺寸与缩放，避免每帧枚举显示器。</summary>
    private void CacheDragGeometry()
    {
        var scale = MonitorService.GetPrimaryScale();
        _dragWinW = (int)Math.Round(MonitorService.ToPhysicalPixels(Width, scale));
        _dragWinH = (int)Math.Round(MonitorService.ToPhysicalPixels(Height, scale));
        _lastDragX = int.MinValue;
        _lastDragY = int.MinValue;
    }

    private int _dragWinW;
    private int _dragWinH;

    /// <summary>取窗口当前的物理像素位置（以 Win32 为准）。</summary>
    private Point GetWindowPhysicalPosition()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero && NativeMethods.GetWindowRect(hwnd, out var r))
                return new System.Windows.Point(r.Left, r.Top);
        }
        catch { }
        var s = MonitorService.GetPrimaryScale();
        return new System.Windows.Point(
            MonitorService.ToPhysicalPixels(Left, s),
            MonitorService.ToPhysicalPixels(Top, s));
    }

    /// <summary>拖动起点的物理位置。</summary>
    private System.Windows.Point _dragStartPhys;

    /// <summary>结束拖动并落位（吸附判定、写回配置）。</summary>
    private void EndDragInternal()
    {
        _dragTimer?.Stop();
        _dragTimer = null;

        if (_dragging)
        {
            _lastDragEndAt = DateTime.Now;
            ReportPollDragPerf();
            FinishDrag();
        }
        else
        {
            Log.Info("轮询发现左键已松开，但未达到拖动阈值（视为单击）");
        }

        Dispatcher.InvokeAsync(() =>
        {
            _dragging = false;
            _holdExpanded = false;
        }, DispatcherPriority.Background);
    }

    /// <summary>输出自绘拖动的性能摘要。</summary>
    private void ReportPollDragPerf()
    {
        ResumeBackgroundTimersAfterDrag();

        if (_dragMoves == 0) return;

        var avg = _dragMoveCostTotal / _dragMoves;
        Log.Info($"[拖动性能/自绘] 移动次数={_dragMoves} " +
                 $"单次移动耗时 平均={avg:0.00}ms 最大={_dragMoveCostMax:0.00}ms " +
                 $"（若平均值很小而体感仍卡，说明瓶颈在窗口重绘而非移动调用）");
    }

    /// <summary>最近一次拖动结束的时刻（用于抑制紧随其后的 Click）。</summary>
    private DateTime _lastDragEndAt = DateTime.MinValue;

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        // 实际推进交给 PollDrag()，这里留空只是保持 XAML 事件签名兼容
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        // 正常的收尾由 PollDrag() 轮询到左键松开时触发；
        // 这里作为兜底（例如鼠标在窗口外松开）。
        if (_dragTimer is not null) EndDragInternal();
    }

    /// <summary>拖动结束：判定落在哪块屏、是否吸附、然后落位。</summary>
    private void FinishDrag()
    {
        var fb = _cfg.FloatingButton;
        var scale = MonitorService.GetPrimaryScale();

        // 1) 判断按钮中心落在哪块屏。
        //    HitTest 用的是物理坐标，所以先把 WPF 单位的 Center 换算过去。
        var centerPhys = new Point(
            MonitorService.ToPhysicalPixels(Left + Width / 2, scale),
            MonitorService.ToPhysicalPixels(Top + Height / 2, scale));
        var monitors = MonitorService.Enumerate();
        var target = MonitorService.HitTest(monitors, centerPhys) ?? _monitor;

        if (target is not null && target.DeviceName != _monitor?.DeviceName)
        {
            _monitor = target;
            fb.LastScreenDeviceName = target.DeviceName;
            Log.Info($"悬浮按钮被拖到 {target.Display}");
        }

        // 2) 夹回该屏工作区（内部按 WPF 单位处理）
        ClampToMonitorWpf();

        // 3) 靠边吸附判定。
        //    EvaluateSnap 用物理像素比较，所以把当前左上角换算成物理坐标。
        var snapped = false;
        if (fb.SnapToEdge && _monitor is not null)
        {
            var leftPhys = MonitorService.ToPhysicalPixels(Left, scale);
            var topPhys = MonitorService.ToPhysicalPixels(Top, scale);
            var btnWPhys = MonitorService.ToPhysicalPixels(MainButton.ActualWidth, scale);

            var (snapLeft, shouldSnap) = MonitorService.EvaluateSnap(
                _monitor, btnWPhys, MonitorService.ToPhysicalPixels(MainButton.ActualHeight, scale),
                new Point(leftPhys, topPhys), fb.SnapThreshold);

            if (shouldSnap)
            {
                snapped = true;
                fb.SnappedEdge = snapLeft ? "left" : "right";
                Log.Info($"触发靠边吸附 → {(snapLeft ? "左" : "右")}边");
            }
            else
            {
                // 离开边缘就取消吸附状态
                fb.SnappedEdge = "";
                if (_collapsed) Expand();
            }
        }

        // 4) 回写边距与自由坐标（**一律存物理像素**，与 MonitorInfo 的坐标空间一致）
        if (_monitor is not null)
        {
            var wa = _monitor.WorkArea;   // 物理像素
            var leftPhys = MonitorService.ToPhysicalPixels(Left, scale);
            var topPhys = MonitorService.ToPhysicalPixels(Top, scale);
            var wPhys = MonitorService.ToPhysicalPixels(Width, scale);
            var hPhys = MonitorService.ToPhysicalPixels(Height + _bubbleExtra, scale);

            fb.MarginRight = (int)Math.Max(0, wa.Right - leftPhys - wPhys);
            fb.MarginBottom = (int)Math.Max(0, wa.Bottom - topPhys - hPhys);

            if (!snapped)
            {
                _hasFreePosition = true;
                fb.FreeX = (int)Math.Round(leftPhys - wa.Left);
                fb.FreeY = (int)Math.Round(topPhys - wa.Top);
                fb.HasFreePosition = true;
            }
            else
            {
                // 吸附了就不再是自由摆放
                _hasFreePosition = false;
                fb.HasFreePosition = false;
            }
        }

        // 5) 吸附后缩起
        if (snapped && fb.CollapseWhenSnapped)
        {
            Collapse(snap: false);   // SnappedEdge 上面已经设好
        }
        else
        {
            RepositionToCorner();
        }

        Log.Info($"拖动结束：落位 Left={Left:0} Top={Top:0} " +
                 $"吸附={(fb.IsSnapped ? fb.SnappedEdge : "无")} " +
                 $"自由位置={_hasFreePosition}({fb.FreeX},{fb.FreeY}) " +
                 $"边距=右{fb.MarginRight}/下{fb.MarginBottom} 屏={_monitor?.DeviceName}");

        LayoutChanged?.Invoke();
    }

    public void StopTimer()    {
        _idleTimer.Stop();
        _visibilityTimer?.Stop();
        _bubbleTimer?.Stop();
        _bubbleTimer = null;
        _dragTimer?.Stop();
        _dragTimer = null;
    }
}
