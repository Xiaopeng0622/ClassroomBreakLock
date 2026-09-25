using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Logging;
using RadioButton = System.Windows.Controls.RadioButton;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Key = System.Windows.Input.Key;

namespace ClassroomBreakLock.Views;

/// <summary>锁屏窗口。承载所有认证方式的交互。</summary>
public partial class LockWindow : Window
{
    private readonly AuthService _auth;
    private AppConfig _cfg;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _totpTimer;
    private readonly RadioButton _tabUsb;
    private readonly RadioButton _tabPwd;
    private readonly RadioButton _tabTotp;
    private readonly RadioButton _tabEmergency;

    /// <summary>认证成功后触发，参数是使用的方式。</summary>
    public event Action<AuthMethod>? Unlocked;

    /// <summary>请求打开设置界面（需要先认证）。</summary>
    public event Action? SettingsRequested;

    public LockWindow(AppConfig cfg, AuthService auth)
    {
        InitializeComponent();
        _cfg = cfg;
        _auth = auth;

        _tabUsb = MakeTab("U 盘", () => ShowPanel(PanelUsb));
        _tabPwd = MakeTab("密码", () => ShowPanel(PanelPassword));
        _tabTotp = MakeTab("动态码", () => ShowPanel(PanelTotp));
        _tabEmergency = MakeTab("应急码", () => ShowPanel(PanelEmergency));

        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTimer.Tick += (_, _) => TickClock();

        _totpTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _totpTimer.Tick += (_, _) => TickTotp();

        Loaded += (_, _) =>
        {
            RefreshFromConfig();
            TickClock();
            _clockTimer.Start();
            _totpTimer.Start();
            FocusFirstAvailable();
        };
    }

    public void UpdateConfig(AppConfig cfg)
    {
        _cfg = cfg;
        _auth.UpdateConfig(cfg);
        RefreshFromConfig();
    }

    private RadioButton MakeTab(string text, Action onClick)
    {
        var rb = new RadioButton
        {
            Content = text,
            Style = (Style)FindResource("TabBtn"),
            GroupName = "authTabs",
            Margin = new Thickness(0, 0, 8, 0)
        };
        rb.Checked += (_, _) => onClick();
        TabBar.Children.Add(rb);
        return rb;
    }

    private void ShowPanel(UIElement panel)
    {
        PanelUsb.Visibility = panel == PanelUsb ? Visibility.Visible : Visibility.Collapsed;
        PanelPassword.Visibility = panel == PanelPassword ? Visibility.Visible : Visibility.Collapsed;
        PanelTotp.Visibility = panel == PanelTotp ? Visibility.Visible : Visibility.Collapsed;
        PanelEmergency.Visibility = panel == PanelEmergency ? Visibility.Visible : Visibility.Collapsed;
        ClearMessage();

        if (panel == PanelPassword) PwdBox.Focus();
        else if (panel == PanelTotp) TotpBox.Focus();
        else if (panel == PanelEmergency) EmergencyBox.Focus();
    }

    /// <summary>按配置刷新可见的认证方式，自动选中第一个可用的。</summary>
    public void RefreshFromConfig()
    {
        ClassroomText.Text = _cfg.ClassroomName;
        TitleText.Text = _cfg.Alerts.LockTitle;
        SubtitleText.Text = _cfg.Alerts.LockSubtitle;

        // 每日一言：同一天内恒定，换天才换
        DailyQuoteText.Text = DailyQuote.ForDisplay();

        ApplyAppearance();

        var (usb, pwd, totp, emg) = _auth.AvailableMethods();

        _tabUsb.Visibility = usb ? Visibility.Visible : Visibility.Collapsed;
        _tabPwd.Visibility = pwd ? Visibility.Visible : Visibility.Collapsed;
        _tabTotp.Visibility = totp ? Visibility.Visible : Visibility.Collapsed;
        _tabEmergency.Visibility = emg ? Visibility.Visible : Visibility.Collapsed;

        PwdHintText.Text = string.IsNullOrWhiteSpace(_cfg.Auth.Password.Hint)
            ? "" : $"提示：{_cfg.Auth.Password.Hint}";

        if (usb) _tabUsb.IsChecked = true;
        else if (pwd) _tabPwd.IsChecked = true;
        else if (totp) _tabTotp.IsChecked = true;
        else if (emg) _tabEmergency.IsChecked = true;
        else
        {
            ShowMessage("尚未配置任何认证方式，请先用应急通道进入设置完成配置。", true);
            PanelUsb.Visibility = Visibility.Visible;
        }
    }

    private void FocusFirstAvailable()
    {
        if (PanelPassword.Visibility == Visibility.Visible) PwdBox.Focus();
        else if (PanelTotp.Visibility == Visibility.Visible) TotpBox.Focus();
        else if (PanelEmergency.Visibility == Visibility.Visible) EmergencyBox.Focus();
    }

    private void TickClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("HH:mm:ss");
        DateText.Text = now.ToString("yyyy年M月d日 dddd", new System.Globalization.CultureInfo("zh-CN"));

        var periods = _engine?.GetTodayPeriods(now);
        if (periods is not null && periods.Count > 0)
        {
            var next = periods.FirstOrDefault(p => p.StartTime > now.TimeOfDay);
            FooterText.Text = next is null
                ? $"今日课程已结束 · 共 {periods.Count} 个时段"
                : $"下一时段：{next.Name} {next.Start} - {next.End}";
        }
        else
        {
            FooterText.Text = "今日无课次安排";
        }
    }

    /// <summary>
    /// 应用个性化外观：背景图、模糊、遮罩、不透明度。
    ///
    /// 任何一项加载失败都必须安静回退到内置深色背景——
    /// 锁屏是全屏压盖的界面，绝不能因为一张图坏了就白屏或抛异常。
    /// </summary>
    public void ApplyAppearance()
    {
        try
        {
            var ap = _cfg.Appearance;

            var path = AppearanceAssets.ResolveImagePath(ap);
            if (path is null)
            {
                // 没有可用图片：隐藏图片层，遮罩也归零
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundImage.Source = null;
                BackgroundOverlay.Opacity = 0;
                return;
            }

            // 用 OnLoad + 立即关闭流，避免文件被进程长期占用
            // （否则用户想换图时删不掉旧文件）
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.EndInit();
            bmp.Freeze();

            BackgroundImage.Source = bmp;
            BackgroundImage.Opacity = ap.BackgroundOpacity;
            BackgroundImage.Stretch = ap.Stretch switch
            {
                "uniform" => Stretch.Uniform,
                "fill" => Stretch.Fill,
                _ => Stretch.UniformToFill
            };

            // 模糊：半径为 0 时把 Effect 摘掉，避免白白走一遍模糊管线
            if (ap.BlurRadius > 0.5)
            {
                BackgroundBlur.Radius = ap.BlurRadius;
                BackgroundImage.Effect = BackgroundBlur;
            }
            else
            {
                BackgroundImage.Effect = null;
            }

            BackgroundOverlay.Opacity = ap.OverlayOpacity;
            BackgroundImage.Visibility = Visibility.Visible;

            Log.Info($"锁屏背景已应用：{ap.BackgroundImageFile} " +
                     $"模糊={ap.BlurRadius:0} 遮罩={ap.OverlayOpacity:0.00} " +
                     $"不透明度={ap.BackgroundOpacity:0.00} 填充={ap.Stretch}");
        }
        catch (Exception ex)
        {
            // 背景图出问题不影响解锁：静默回退
            Log.Warn($"应用锁屏背景失败，回退内置背景：{ex.Message}");
            try
            {
                BackgroundImage.Visibility = Visibility.Collapsed;
                BackgroundImage.Source = null;
                BackgroundOverlay.Opacity = 0;
            }
            catch { }
        }
    }

    private Scheduling.ScheduleEngine? _engine;
    public void AttachEngine(Scheduling.ScheduleEngine engine) => _engine = engine;

    private void TickTotp()
    {
        if (PanelTotp.Visibility != Visibility.Visible) return;
        if (!_cfg.Auth.Totp.IsConfigured) return;
        var left = Totp.SecondsRemaining(DateTime.UtcNow, _cfg.Auth.Totp.Step);
        TotpTimerText.Text = $"当前动态码剩余 {left} 秒（剩余 {left / (double)_cfg.Auth.Totp.Step:P0}）";
    }

    private void SetStatus(string text, bool ok)
    {
        MessageText.Text = text;
        MessageText.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
            : new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
    }

    private void ShowMessage(string text, bool ok) => SetStatus(text, ok);
    private void ClearMessage() => MessageText.Text = "";

    private void HandleOutcome(AuthOutcome outcome)
    {
        SetStatus(outcome.Message, outcome.Success);
        if (outcome.Success)
        {
            PwdBox.Clear();
            TotpBox.Clear();
            EmergencyBox.Clear();
            Log.Info($"认证成功：{outcome.Method} - {outcome.Message}");
            Unlocked?.Invoke(outcome.Method);
        }
    }

    private void OnUsbClick(object sender, RoutedEventArgs e)
    {
        UsbHintText.Text = "正在检测…";
        // 让 UI 先把"正在检测"刷出来再干活
        Dispatcher.InvokeAsync(() =>
        {
            var r = _auth.TryUsb();
            UsbHintText.Text = r.Message;
            HandleOutcome(r);
        }, DispatcherPriority.Background);
    }

    private void OnPwdClick(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PwdBox.Password))
        {
            SetStatus("请输入密码", false);
            return;
        }
        HandleOutcome(_auth.TryPassword(PwdBox.Password));
    }

    private void OnTotpClick(object sender, RoutedEventArgs e)
        => HandleOutcome(_auth.TryTotp(TotpBox.Text.Trim()));

    private void OnEmergencyClick(object sender, RoutedEventArgs e)
        => HandleOutcome(_auth.TryEmergency(EmergencyBox.Text.Trim()));

    private void OnSettingsClick(object sender, RoutedEventArgs e)
        => SettingsRequested?.Invoke();

    /// <summary>回车键直接提交当前面板。</summary>
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;

        if (PanelPassword.Visibility == Visibility.Visible) OnPwdClick(sender, e);
        else if (PanelTotp.Visibility == Visibility.Visible) OnTotpClick(sender, e);
        else if (PanelEmergency.Visibility == Visibility.Visible) OnEmergencyClick(sender, e);
        else if (PanelUsb.Visibility == Visibility.Visible) OnUsbClick(sender, e);
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 锁屏窗口不允许被常规方式关闭；真退出走 Application.Shutdown
        if (!App.IsShuttingDown)
        {
            e.Cancel = true;
            Log.Warn("拦截了一次锁屏窗口关闭请求");
        }
    }

    public void StopTimers()
    {
        _clockTimer.Stop();
        _totpTimer.Stop();
    }
}
