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
/// 点击后判定本节课结束 -> 进入课间解锁状态。这是除时间判定之外的第二条解锁入口。
/// </summary>
public partial class FloatingButtonWindow : Window
{
    private AppConfig _cfg;
    private readonly DispatcherTimer _idleTimer;
    private DateTime _lastMouseMove = DateTime.Now;
    private bool _dragging;
    private NativeMethods.POINT _dragStartCursor;
    private double _dragStartLeft, _dragStartTop;
    private bool _suppressed;
    private DispatcherTimer? _bubbleTimer;
    private double _bubbleExtra;

    /// <summary>按钮区域的固定高度（按钮本身 + 上下留白）。</summary>
    private double ButtonAreaHeight => _cfg.FloatingButton.Size + 24;

    /// <summary>气泡显示时的最小窗口宽度。</summary>
    private const double BubbleMinWidth = 300;

    /// <summary>用户点击了"下课"。</summary>
    public event Action? ClassDismissed;

    /// <summary>用户长按 3 秒请求打开设置。</summary>
    public event Action? SettingsRequested;

    public double ButtonSize => _cfg.FloatingButton.Size;

    public FloatingButtonWindow(AppConfig cfg)
    {
        InitializeComponent();
        _cfg = cfg;
        DataContext = this;

        _idleTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _idleTimer.Tick += (_, _) => TickIdle();

        Loaded += (_, _) =>
        {
            ApplyConfig(_cfg);
            _idleTimer.Start();
        };
    }

    public void ApplyConfig(AppConfig cfg)
    {
        _cfg = cfg;
        DataContext = null;
        DataContext = this;

        ButtonText.Text = cfg.FloatingButton.Text;
        Opacity = cfg.FloatingButton.Opacity;
        Visibility = cfg.FloatingButton.Enabled ? Visibility.Visible : Visibility.Collapsed;

        // 换配置时把气泡收掉，免得残留一个宽窗口
        Bubble.Visibility = Visibility.Collapsed;
        _bubbleExtra = 0;

        RepositionToCorner();
        UpdateSubText();
    }

    /// <summary>
    /// 贴到主屏右下角。
    /// 注意：锚定的是**按钮底部**而不是窗口底部——气泡在上面，
    /// 所以窗口变高时按钮不会跟着跑。
    /// </summary>
    private void RepositionToCorner()
    {
        var wa = SystemParameters.WorkArea;
        Width = Math.Max(ButtonAreaHeight, _bubbleExtra > 0 ? BubbleMinWidth : ButtonAreaHeight);
        Height = ButtonAreaHeight + _bubbleExtra;
        Left = wa.Right - Width - _cfg.FloatingButton.MarginRight;
        Top = wa.Bottom - _cfg.FloatingButton.MarginBottom - Height;
    }

    /// <summary>浮标下方的小字，显示当前状态或距离下节课的时间。</summary>
    public void UpdateSubText(string? text = null)
    {
        ButtonSubText.Text = text ?? "";
        ButtonSubText.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>显示一个气泡提示，几秒后自动消失。文案会自动换行，不再被截断。</summary>
    public void ShowBubble(string text, int seconds = 4)
    {
        BubbleText.Text = text;
        Bubble.Visibility = Visibility.Visible;

        // 先量出气泡实际高度，再把窗口向上撑开（按钮位置不变）
        Bubble.Measure(new Size(BubbleMinWidth - 24, double.PositiveInfinity));
        _bubbleExtra = Math.Ceiling(Bubble.DesiredSize.Height) + 10;
        RepositionToCorner();
        UpdateLayout();

        // 旧的计时器必须先停，否则连续弹两个气泡时前一个会把它提前关掉
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
        if (suppressed) Visibility = Visibility.Collapsed;
        else Visibility = _cfg.FloatingButton.Enabled ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TickIdle()
    {
        if (!IsVisible || _suppressed) return;
        var fade = _cfg.FloatingButton.FadeAfterIdleSeconds;
        if (fade <= 0) { Opacity = _cfg.FloatingButton.Opacity; return; }

        var idle = (DateTime.Now - _lastMouseMove).TotalSeconds;
        // 鼠标靠近时恢复全亮，长时间不动就淡下去，不挡课件
        if (idle > fade)
            Opacity = Math.Max(0.25, _cfg.FloatingButton.Opacity * 0.35);
        else
            Opacity = _cfg.FloatingButton.Opacity;
    }

    private void OnMouseEnter(object sender, MouseEventArgs e)
    {
        _lastMouseMove = DateTime.Now;
        Opacity = _cfg.FloatingButton.Opacity;
        NativeMethods.PinTopMost(new System.Windows.Interop.WindowInteropHelper(this).Handle);
    }

    private void OnMouseLeave(object sender, MouseEventArgs e) => _lastMouseMove = DateTime.Now;

    private void OnClick(object sender, RoutedEventArgs e)
    {
        if (_dragging) return;
        Log.Info("用户点击了悬浮“下课”按钮");
        ClassDismissed?.Invoke();
    }

    // ---------------- 拖动 ----------------

    private void OnDragStart(object sender, MouseButtonEventArgs e)
    {
        if (!_cfg.FloatingButton.Draggable) return;
        if (e.ClickCount >= 2)
        {
            // 双击进设置
            SettingsRequested?.Invoke();
            return;
        }
        _dragging = false;
        NativeMethods.GetCursorPos(out _dragStartCursor);
        _dragStartLeft = Left;
        _dragStartTop = Top;
        MainButton.CaptureMouse();
    }

    private void OnDragMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || !MainButton.IsMouseCaptured) return;
        if (!_cfg.FloatingButton.Draggable) return;

        NativeMethods.GetCursorPos(out var p);
        var dx = p.X - _dragStartCursor.X;
        var dy = p.Y - _dragStartCursor.Y;

        if (!_dragging && (Math.Abs(dx) > 4 || Math.Abs(dy) > 4)) _dragging = true;
        if (!_dragging) return;

        // 用 Win32 光标坐标换算成 WPF 逻辑坐标（考虑 DPI）
        var dpi = VisualTreeHelper.GetDpi(this);
        Left = _dragStartLeft + dx / dpi.DpiScaleX;
        Top = _dragStartTop + dy / dpi.DpiScaleY;
    }

    private void OnDragEnd(object sender, MouseButtonEventArgs e)
    {
        MainButton.ReleaseMouseCapture();
        if (_dragging)
        {
            // 拖动结束后记住新位置（换算回右下角边距）。
            // 注意窗口高度包含上方的气泡，所以下边距要减掉气泡那一段，
            // 否则拖过（且气泡还开着）会把按钮位置算偏。
            var wa = SystemParameters.WorkArea;
            _cfg.FloatingButton.MarginRight = (int)Math.Max(0, wa.Right - Left - Width);
            _cfg.FloatingButton.MarginBottom = (int)Math.Max(0, wa.Bottom - Top - Height + _bubbleExtra);
            Log.Info($"悬浮按钮位置更新：右距 {_cfg.FloatingButton.MarginRight} 下距 {_cfg.FloatingButton.MarginBottom}");
        }
        // 让 Click 事件在拖动后不触发
        Dispatcher.InvokeAsync(() => _dragging = false, DispatcherPriority.Background);
    }

    public void StopTimer()
    {
        _idleTimer.Stop();
        _bubbleTimer?.Stop();
        _bubbleTimer = null;
    }
}
