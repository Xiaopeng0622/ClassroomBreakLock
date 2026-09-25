using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Logging;

namespace ClassroomBreakLock.Views;

/// <summary>
/// 打开设置前的认证门。复用 AppDialog 的视觉语言（圆角卡片 + 细描边 + 柔和投影）。
///
/// 与锁屏页的区别：这是**浅色**管理侧弹窗，不是深色锁屏，
/// 所以配色走设置窗口那一套（Layer1 / CInk / CAccent），保持管理界面一致性。
///
/// 支持 U 盘与密码两种方式（TOTP / 应急码同样可用，按配置决定显示哪些）。
/// 认证通过后返回 true。
/// </summary>
public static class SettingsAuthGate
{
    // —— 浅色管理侧配色，与 CI.Tokens 保持一致 ——
    private static readonly Brush CardFill = Frozen("#FFFFFFFF");
    private static readonly Brush LineFill = Frozen("#FFE5E5E5");
    private static readonly Brush SoftFill = Frozen("#FFF9F9F9");
    private static readonly Brush TitleFill = Frozen("#FF1B1B1B");
    private static readonly Brush BodyFill = Frozen("#FF5D5D5D");
    private static readonly Brush FaintFill = Frozen("#FF8A8A8A");
    private static readonly Brush AccentFill = Frozen("#FF0067C0");
    private static readonly Brush AccentHoverFill = Frozen("#FF1975C5");
    private static readonly Brush AccentSoftFill = Frozen("#FFEFF6FC");
    private static readonly Brush DangerFill = Frozen("#FFC42B1C");
    private static readonly Brush OkFill = Frozen("#FF0F7B0F");
    private static readonly Brush GhostHoverFill = Frozen("#FFF5F5F5");

    private static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI");

    /// <summary>
    /// 弹出认证门。返回 true 表示认证通过、允许进入设置。
    /// </summary>
    /// <param name="owner">父窗口，用于居中（可为 null）</param>
    /// <param name="cfg">当前配置</param>
    /// <param name="auth">认证服务</param>
    /// <param name="reason">显示给用户的说明，例如"进入设置需要验证身份"</param>
    public static bool Require(Window? owner, AppConfig cfg, AuthService auth, string? reason = null)
    {
        var (hasUsb, hasPwd, hasTotp, hasEmergency) = auth.AvailableMethods();

        // 一个认证方式都没配：直接放行并说明原因。
        // 这是刻意的——否则用户会被永久锁在设置之外，连配置的地方都进不去。
        if (!hasUsb && !hasPwd && !hasTotp && !hasEmergency)
        {
            Log.Warn("尚未配置任何认证方式，设置门自动放行（提示用户尽快配置）");
            AppDialog.Warn(owner,
                "当前尚未配置任何认证方式，因此本次直接放行。\n\n" +
                "请进入「认证」页尽快配置 U 盘、密码或动态码，\n" +
                "否则设置界面对任何人都是敞开的。",
                "安全提醒");
            return true;
        }

        bool passed = false;

        Window dialog = BuildDialog(owner, cfg, auth, hasUsb, hasPwd, hasTotp, hasEmergency,
            reason ?? "使用 U 盘、密码或动态码完成验证", v => passed = v);

        // 弹窗显示后再重申一次置顶。
        // 锁屏窗口是 Topmost，即便调用方已暂停其置顶重申，
        // 弹窗第一次显示时仍可能被压在下面（Z 序竞争）。
        // 这里在 Loaded 之后主动抢一次前台，确保弹窗可见可点。
        dialog.Loaded += (_, _) =>
        {
            try
            {
                var hwnd = new System.Windows.Interop.WindowInteropHelper(dialog).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    Interop.NativeMethods.PinTopMost(hwnd);
                    Interop.NativeMethods.SetForegroundWindow(hwnd);
                }
            }
            catch (Exception ex)
            {
                Log.Warn($"认证弹窗置顶失败：{ex.Message}");
            }
        };

        dialog.ShowDialog();
        return passed;
    }

    private static Window BuildDialog(
        Window? owner,
        AppConfig cfg,
        AuthService auth,
        bool hasUsb,
        bool hasPwd,
        bool hasTotp,
        bool hasEmergency,
        string reason,
        Action<bool> onClose)
    {
        Window dialog = BaseWindow(owner, "身份验证", sizeToHeight: false);

        // ---------- 头部：锁形圆点 + 标题 + 说明 ----------
        StackPanel head = new() { Orientation = Orientation.Horizontal };

        Border dot = new()
        {
            Width = 30,
            Height = 30,
            CornerRadius = new CornerRadius(8),
            Background = AccentSoftFill,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 12, 0),
            Child = new TextBlock
            {
                Text = "🔒",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            }
        };
        head.Children.Add(dot);

        StackPanel headText = new() { VerticalAlignment = VerticalAlignment.Center };
        headText.Children.Add(new TextBlock
        {
            Text = "验证身份",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TitleFill
        });
        headText.Children.Add(new TextBlock
        {
            Text = reason,
            FontSize = 12.5,
            Foreground = FaintFill,
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 330
        });
        head.Children.Add(headText);
        // ---------- 认证方式切换（药丸形 chip） ----------
        StackPanel chips = new()
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 20, 0, 0)
        };

        // ---------- 各认证面板 ----------
        // 用 StackPanel + 显隐切换，而不是 Grid 叠放：
        // Grid 里多个子元素同格会互相撑高（取最高者），还默认拉伸，导致弹窗下方留一大片空白。
        StackPanel panels = new() { Margin = new Thickness(0, 16, 0, 0) };

        // 提示行（错误/成功）
        TextBlock message = new()
        {
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0),
            Visibility = Visibility.Collapsed
        };

        // —— U 盘面板 ——
        StackPanel panelUsb = new();
        TextBlock usbHint = new()
        {
            Text = "插入已登记的钥匙 U 盘，然后点击下方按钮校验",
            FontSize = 13,
            Foreground = BodyFill,
            TextWrapping = TextWrapping.Wrap
        };
        panelUsb.Children.Add(usbHint);
        Button usbBtn = PrimaryButton("检测 U 盘");
        usbBtn.Margin = new Thickness(0, 16, 0, 0);
        usbBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panelUsb.Children.Add(usbBtn);

        // —— 密码面板 ——
        StackPanel panelPwd = new();
        PasswordBox pwdBox = new()
        {
            Height = 38,
            FontFamily = UiFont,
            FontSize = 15,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.White,
            Foreground = TitleFill,
            BorderBrush = LineFill,
            BorderThickness = new Thickness(1),
            CaretBrush = TitleFill,
            PasswordChar = '●'
        };
        panelPwd.Children.Add(pwdBox);
        if (!string.IsNullOrWhiteSpace(cfg.Auth.Password.Hint))
        {
            panelPwd.Children.Add(new TextBlock
            {
                Text = $"提示：{cfg.Auth.Password.Hint}",
                FontSize = 12.5,
                Foreground = FaintFill,
                Margin = new Thickness(0, 8, 0, 0)
            });
        }
        Button pwdBtn = PrimaryButton("验证密码");
        pwdBtn.Margin = new Thickness(0, 16, 0, 0);
        pwdBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panelPwd.Children.Add(pwdBtn);

        // —— 动态码面板 ——
        StackPanel panelTotp = new();
        TextBox totpBox = new()
        {
            Height = 38,
            Width = 200,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 19,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            MaxLength = 6,
            Background = Brushes.White,
            Foreground = TitleFill,
            BorderBrush = LineFill,
            BorderThickness = new Thickness(1),
            CaretBrush = TitleFill
        };
        panelTotp.Children.Add(totpBox);
        Button totpBtn = PrimaryButton("验证动态码");
        totpBtn.Margin = new Thickness(0, 16, 0, 0);
        totpBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panelTotp.Children.Add(totpBtn);

        // —— 应急码面板 ——
        StackPanel panelEmergency = new();
        TextBox emgBox = new()
        {
            Height = 38,
            Width = 240,
            FontFamily = new FontFamily("Consolas, Cascadia Mono, monospace"),
            FontSize = 15,
            Padding = new Thickness(12, 0, 12, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 20,
            Background = Brushes.White,
            Foreground = TitleFill,
            BorderBrush = LineFill,
            BorderThickness = new Thickness(1),
            CaretBrush = TitleFill
        };
        panelEmergency.Children.Add(emgBox);
        panelEmergency.Children.Add(new TextBlock
        {
            Text = "应急码使用后自动作废并留痕",
            FontSize = 12.5,
            Foreground = FaintFill,
            Margin = new Thickness(0, 8, 0, 0)
        });
        Button emgBtn = PrimaryButton("提交应急码");
        emgBtn.Margin = new Thickness(0, 16, 0, 0);
        emgBtn.HorizontalAlignment = HorizontalAlignment.Left;
        panelEmergency.Children.Add(emgBtn);

        // 记录所有面板，切换时统一处理
        var allPanels = new List<(StackPanel Panel, Chip Chip)>();
        var chipButtons = new List<Chip>();

        void Select(StackPanel target)
        {
            foreach (var (panel, chip) in allPanels)
            {
                bool active = ReferenceEquals(panel, target);
                panel.Visibility = active ? Visibility.Visible : Visibility.Collapsed;
                chip.SetActive(active);
            }
            message.Visibility = Visibility.Collapsed;

            // 聚焦到该面板的输入控件
            if (ReferenceEquals(target, panelPwd)) pwdBox.Focus();
            else if (ReferenceEquals(target, panelTotp)) totpBox.Focus();
            else if (ReferenceEquals(target, panelEmergency)) emgBox.Focus();
        }

        void AddMethod(string label, StackPanel panel)
        {
            var chip = new Chip(label, UiFont);
            chip.Clicked += () => Select(panel);
            chips.Children.Add(chip.Root);
            chipButtons.Add(chip);
            allPanels.Add((panel, chip));
            panels.Children.Add(panel);
            panel.Visibility = Visibility.Collapsed;
        }

        if (hasUsb) AddMethod("U 盘", panelUsb);
        if (hasPwd) AddMethod("密码", panelPwd);
        if (hasTotp) AddMethod("动态码", panelTotp);
        if (hasEmergency) AddMethod("应急码", panelEmergency);

        // ---------- 结果处理 ----------
        void ShowResult(string text, bool ok)
        {
            message.Text = ok ? $"✓ {text}" : $"✗ {text}";
            message.Foreground = ok ? OkFill : DangerFill;
            message.Visibility = Visibility.Visible;
        }

        void Handle(AuthOutcome outcome)
        {
            ShowResult(outcome.Message, outcome.Success);
            if (outcome.Success)
            {
                Log.Info($"设置门认证通过：{outcome.Method} - {outcome.Message}");
                onClose(true);
                dialog.Close();
            }
        }

        usbBtn.Click += (_, _) =>
        {
            usbHint.Text = "正在检测…";
            usbBtn.IsEnabled = false;
            // 让 UI 先刷出"正在检测"，再做阻塞的磁盘枚举
            dialog.Dispatcher.InvokeAsync(() =>
            {
                var r = auth.TryUsb();
                usbHint.Text = r.Success
                    ? "插入已登记的钥匙 U 盘，然后点击下方按钮校验"
                    : r.Message;
                usbBtn.IsEnabled = true;
                Handle(r);
            }, System.Windows.Threading.DispatcherPriority.Background);
        };

        pwdBtn.Click += (_, _) =>
        {
            if (string.IsNullOrEmpty(pwdBox.Password))
            {
                ShowResult("请输入密码", false);
                return;
            }
            var r = auth.TryPassword(pwdBox.Password);
            if (r.Success) pwdBox.Clear();
            Handle(r);
        };

        totpBtn.Click += (_, _) => Handle(auth.TryTotp(totpBox.Text.Trim()));

        emgBtn.Click += (_, _) => Handle(auth.TryEmergency(emgBox.Text.Trim()));

        // ---------- 底部按钮 ----------
        StackPanel buttons = new()
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };

        Button cancel = FlatButton("取消");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 10, 0);
        cancel.Click += (_, _) =>
        {
            Log.Info("设置门认证被取消");
            onClose(false);
            dialog.Close();
        };
        buttons.Children.Add(cancel);

        // ---------- 组装 ----------
        StackPanel root = new();
        root.Children.Add(head);
        root.Children.Add(chips);
        root.Children.Add(panels);
        root.Children.Add(message);
        root.Children.Add(buttons);

        Border card = Card(root);
        dialog.Content = card;

        // 默认选中第一个可用的认证方式
        if (hasUsb) Select(panelUsb);
        else if (hasPwd) Select(panelPwd);
        else if (hasTotp) Select(panelTotp);
        else Select(panelEmergency);

        // 手动把窗口收拢到贴合内容：先量 Card（含外边距）再设窗口高度。
        // SizeToContent 在有复杂模板时会算多，留出大片空白。
        dialog.Loaded += (_, _) =>
        {
            SizeToFitContent(dialog, card);
            dialog.Activate();
        };

        // 切换认证方式时面板高度不同，窗口跟着重新贴合
        foreach (var chip in chipButtons)
        {
            chip.Clicked += () => dialog.Dispatcher.InvokeAsync(
                () => SizeToFitContent(dialog, card),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        // 回车提交当前面板
        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                onClose(false);
                dialog.Close();
                return;
            }
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            if (panelPwd.Visibility == Visibility.Visible) pwdBtn.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            else if (panelTotp.Visibility == Visibility.Visible) totpBtn.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            else if (panelEmergency.Visibility == Visibility.Visible) emgBtn.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            else if (panelUsb.Visibility == Visibility.Visible) usbBtn.RaiseEvent(
                new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        };

        return dialog;
    }

    // ================= 视觉构件 =================

    /// <summary>
    /// 把窗口高度收到刚好包住卡片。Card 自带 20px 外边距，所以窗口高度 = 卡片内容高 + 40。
    /// 用它替代 SizeToContent，避免复杂模板下算出的高度偏大、底部留白。
    /// </summary>
    private static void SizeToFitContent(Window dialog, Border card)
    {
        try
        {
            card.Measure(new Size(dialog.Width, double.PositiveInfinity));
            var contentHeight = card.DesiredSize.Height;
            if (contentHeight <= 0) return;

            // 卡片外边距上下各 20
            var target = Math.Ceiling(contentHeight);
            if (Math.Abs(dialog.Height - target) > 1)
            {
                dialog.Height = target;
            }
        }
        catch
        {
            // 量不出来就保持原样，不影响功能
        }
    }

    /// <summary>药丸形切换 chip，选中态用强调色浅底 + 强调色文字。</summary>
    private sealed class Chip
    {
        public Border Root { get; }
        private readonly TextBlock _text;
        private bool _active;

        public event Action? Clicked;

        public Chip(string label, FontFamily font)
        {
            _text = new TextBlock
            {
                Text = label,
                FontFamily = font,
                FontSize = 13.5,
                Foreground = BodyFill,
                VerticalAlignment = VerticalAlignment.Center
            };

            Root = new Border
            {
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(16, 7, 16, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Cursor = Cursors.Hand,
                Child = _text
            };

            Root.MouseLeftButtonUp += (_, _) => Clicked?.Invoke();
            Root.MouseEnter += (_, _) => { if (!_active) Root.Background = GhostHoverFill; };
            Root.MouseLeave += (_, _) => { if (!_active) Root.Background = Brushes.Transparent; };
        }

        public void SetActive(bool active)
        {
            _active = active;
            Root.Background = active ? AccentSoftFill : Brushes.Transparent;
            _text.Foreground = active ? AccentFill : BodyFill;
            _text.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
        }
    }

    private static Button PrimaryButton(string text)
    {
        Border face = new()
        {
            Background = AccentFill,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(22, 0, 22, 0),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 14,
                Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        Button button = new()
        {
            Content = face,
            Height = 38,
            MinWidth = 108,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            }
        };

        face.MouseEnter += (_, _) => face.Background = AccentHoverFill;
        face.MouseLeave += (_, _) => face.Background = AccentFill;

        return button;
    }

    private static Button FlatButton(string text)
    {
        Border face = new()
        {
            Background = Brushes.Transparent,
            BorderBrush = LineFill,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20, 0, 20, 0),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = UiFont,
                FontSize = 14,
                Foreground = TitleFill,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        Button button = new()
        {
            Content = face,
            Height = 38,
            MinWidth = 92,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            }
        };

        face.MouseEnter += (_, _) => face.Background = GhostHoverFill;
        face.MouseLeave += (_, _) => face.Background = Brushes.Transparent;

        return button;
    }

    private static Window BaseWindow(Window? owner, string title, bool sizeToHeight = true)
    {
        Window dialog = new()
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = sizeToHeight ? SizeToContent.Height : SizeToContent.Manual,
            Width = 460,
            WindowStartupLocation = owner is not null && owner.IsVisible
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            FontFamily = UiFont,
            Title = title
        };

        if (owner is not null && owner.IsVisible) dialog.Owner = owner;
        return dialog;
    }

    private static Border Card(UIElement content) => new()
    {
        Background = CardFill,
        BorderBrush = LineFill,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(26, 22, 26, 20),
        Margin = new Thickness(20),
        Child = content,
        Effect = new DropShadowEffect
        {
            BlurRadius = 20,
            ShadowDepth = 3,
            Direction = 270,
            Opacity = 0.16,
            Color = Colors.Black
        }
    };

    private static Brush Frozen(string hex)
    {
        SolidColorBrush b = new((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    /// <summary>仅供离线预览：构建但不显示，用于生成截图。</summary>
    public static Window BuildPreview(AppConfig cfg, AuthService auth)
    {
        var (usb, pwd, totp, emg) = auth.AvailableMethods();
        return BuildDialog(null, cfg, auth, usb, pwd, totp, emg,
            "进入设置会修改锁屏规则，请先验证身份", _ => { });
    }
}
