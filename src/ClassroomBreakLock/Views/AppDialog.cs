using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;

namespace ClassroomBreakLock.Views;

/// <summary>弹窗种类，决定强调色与图标。</summary>
public enum DialogKind
{
    Info,
    Warn,
    Error,
    Question
}

/// <summary>
/// 自带圆角的自定义弹窗，用来替换系统 MessageBox。
/// 系统弹窗是直角老样式，跟 Win11 界面放一起很掉价；这里全部用代码搭 UI，
/// 不依赖 XAML 资源字典，任何地方都能直接调。
/// </summary>
public static class AppDialog
{
    private static readonly Brush CardFill = Brush("#FFFFFFFF");
    private static readonly Brush LineFill = Brush("#FFE3E3E3");
    private static readonly Brush TitleFill = Brush("#FF1B1B1B");
    private static readonly Brush BodyFill = Brush("#FF5D5D5D");
    private static readonly Brush AccentFill = Brush("#FF0067C0");
    private static readonly Brush AccentHoverFill = Brush("#FF1975C5");
    private static readonly Brush DangerFill = Brush("#FFC42B1C");
    private static readonly Brush DangerHoverFill = Brush("#FFD13A2A");
    private static readonly Brush WarnFill = Brush("#FF9D5D00");
    private static readonly Brush GhostHoverFill = Brush("#FFF5F5F5");

    public static void Info(Window? owner, string message, string title = "提示")
        => Show(owner, title, message, DialogKind.Info, false, "好的", "取消", false);

    public static void Warn(Window? owner, string message, string title = "注意")
        => Show(owner, title, message, DialogKind.Warn, false, "知道了", "取消", false);

    public static void Error(Window? owner, string message, string title = "出错了")
        => Show(owner, title, message, DialogKind.Error, false, "知道了", "取消", false);

    /// <summary>确认框：返回 true 表示点了确定。</summary>
    public static bool Confirm(
        Window? owner,
        string message,
        string title = "确认",
        string okText = "确定",
        string cancelText = "取消",
        bool danger = false)
        => Show(owner, title, message, danger ? DialogKind.Warn : DialogKind.Question, true, okText, cancelText, danger);

    private static bool Show(
        Window? owner,
        string title,
        string message,
        DialogKind kind,
        bool confirm,
        string okText,
        string cancelText,
        bool danger)
    {
        bool accepted = false;
        Window dialog = BuildCore(owner, title, message, kind, confirm, okText, cancelText, danger,
            v => accepted = v);
        dialog.ShowDialog();
        return accepted;
    }

    /// <summary>仅供离线预览使用：只构建不显示。</summary>
    public static Window BuildPreview(string title, string message, bool confirm, bool danger)
        => BuildCore(null, title, message, danger ? DialogKind.Warn : DialogKind.Info, confirm,
            confirm ? "确定" : "好的", "取消", danger, null);

    private static Window BuildCore(
        Window? owner,
        string title,
        string message,
        DialogKind kind,
        bool confirm,
        string okText,
        string cancelText,
        bool danger,
        Action<bool>? onClose)
    {
        Window dialog = BaseWindow(owner, title, 448, SizeToContent.Height);

        // ---- 内容 ----
        StackPanel head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new Ellipse
        {
            Width = 10,
            Height = 10,
            Fill = AccentOf(kind),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0)
        });
        head.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TitleFill,
            VerticalAlignment = VerticalAlignment.Center
        });

        TextBlock body = new TextBlock
        {
            Text = message,
            FontSize = 14,
            LineHeight = 22,
            Foreground = BodyFill,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 14, 0, 0)
        };

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 24, 0, 0)
        };

        Button cancelButton = FlatButton(cancelText, false, false);
        cancelButton.IsCancel = true;
        cancelButton.Margin = new Thickness(0, 0, 10, 0);
        cancelButton.Click += (_, _) =>
        {
            onClose?.Invoke(false);
            dialog.Close();
        };

        Button okButton = FlatButton(okText, true, danger);
        okButton.IsDefault = true;
        okButton.Click += (_, _) =>
        {
            onClose?.Invoke(true);
            dialog.Close();
        };

        if (confirm)
        {
            buttons.Children.Add(cancelButton);
        }

        buttons.Children.Add(okButton);

        StackPanel root = new StackPanel();
        root.Children.Add(head);
        root.Children.Add(body);
        root.Children.Add(buttons);

        dialog.Content = Card(root, 20);

        dialog.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                dialog.Close();
            }
        };

        dialog.Loaded += (_, _) =>
        {
            dialog.Activate();
            okButton.Focus();
        };

        return dialog;
    }

    private static Brush AccentOf(DialogKind kind) => kind switch
    {
        DialogKind.Error => DangerFill,
        DialogKind.Warn => WarnFill,
        _ => AccentFill
    };

    /// <summary>单行输入框（圆角自定义样式），取消返回 null。</summary>
    public static string? Input(Window? owner, string title, string message, string defaultValue = "")
        => TextDialog(owner, title, message, defaultValue, false);

    /// <summary>多行粘贴框（圆角自定义样式），取消返回 null。</summary>
    public static string? MultilineInput(Window? owner, string title, string message, string initial = "")
        => TextDialog(owner, title, message, initial, true);

    private static string? TextDialog(
        Window? owner,
        string title,
        string message,
        string initial,
        bool multiline)
    {
        string? result = null;

        Window dialog = BaseWindow(owner, title, multiline ? 560 : 460, SizeToContent.Manual);
        if (multiline)
        {
            dialog.Height = 470;
        }

        StackPanel root = new StackPanel();
        root.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = TitleFill
        });
        root.Children.Add(new TextBlock
        {
            Text = message,
            FontSize = 13,
            LineHeight = 20,
            Foreground = BodyFill,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0)
        });

        TextBox box = new TextBox
        {
            Text = initial,
            FontFamily = new FontFamily("Microsoft YaHei UI, Consolas"),
            FontSize = multiline ? 13.5 : 14,
            Foreground = TitleFill,
            Background = Brushes.White,
            BorderBrush = LineFill,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, multiline ? 8 : 0, 10, 0),
            Height = multiline ? double.NaN : 34,
            MinHeight = multiline ? 220 : 0,
            Margin = new Thickness(0, 16, 0, 0),
            AcceptsReturn = multiline,
            AcceptsTab = false,
            TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap,
            VerticalScrollBarVisibility = multiline ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalContentAlignment = multiline ? VerticalAlignment.Top : VerticalAlignment.Center
        };
        root.Children.Add(box);

        StackPanel buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 20, 0, 0)
        };

        Button cancelButton = FlatButton("取消", false, false);
        cancelButton.IsCancel = true;
        cancelButton.Margin = new Thickness(0, 0, 10, 0);
        cancelButton.Click += (_, _) => dialog.Close();

        Button okButton = FlatButton("确定", true, false);
        okButton.IsDefault = true;
        okButton.Click += (_, _) =>
        {
            result = box.Text;
            dialog.Close();
        };

        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        root.Children.Add(buttons);

        dialog.Content = Card(root, 0);
        dialog.Loaded += (_, _) =>
        {
            dialog.Activate();
            box.Focus();
            box.CaretIndex = box.Text.Length;
        };

        dialog.ShowDialog();
        return result;
    }

    private static Button FlatButton(string text, bool primary, bool danger)
    {
        Brush fill = primary ? (danger ? DangerFill : AccentFill) : Brushes.Transparent;
        Brush hover = primary ? (danger ? DangerHoverFill : AccentHoverFill) : GhostHoverFill;
        Brush ink = primary ? Brushes.White : TitleFill;

        Border face = new Border
        {
            Background = fill,
            BorderBrush = LineFill,
            BorderThickness = primary ? new Thickness(0) : new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(20, 0, 20, 0),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 14,
                Foreground = ink,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };

        Button button = new Button
        {
            Content = face,
            Height = 36,
            MinWidth = 92,
            Cursor = Cursors.Hand,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Template = new ControlTemplate(typeof(Button))
            {
                VisualTree = new FrameworkElementFactory(typeof(ContentPresenter))
            }
        };

        face.MouseEnter += (_, _) => face.Background = hover;
        face.MouseLeave += (_, _) => face.Background = fill;

        return button;
    }

    /// <summary>统一创建无边框、透明底色、可置顶的弹窗宿主。</summary>
    private static Window BaseWindow(Window? owner, string title, double width, SizeToContent sizeToContent)
    {
        Window dialog = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            Topmost = true,
            SizeToContent = sizeToContent,
            Width = width,
            WindowStartupLocation = owner is not null && owner.IsVisible
                ? WindowStartupLocation.CenterOwner
                : WindowStartupLocation.CenterScreen,
            FontFamily = new FontFamily("Microsoft YaHei UI, Segoe UI"),
            Title = title
        };

        if (owner is not null && owner.IsVisible)
        {
            dialog.Owner = owner;
        }

        return dialog;
    }

    /// <summary>圆角卡片外壳（带投影，支持拖动）。</summary>
    private static Border Card(UIElement content, double topMargin)
    {
        Border card = new Border
        {
            Background = CardFill,
            BorderBrush = LineFill,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(26, 22, 26, 20),
            Margin = new Thickness(20, topMargin, 20, 20),
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

        card.MouseLeftButtonDown += (_, e) =>
        {
            Window? host = Window.GetWindow(card);
            if (host is not null && e.ButtonState == MouseButtonState.Pressed)
            {
                try
                {
                    host.DragMove();
                }
                catch
                {
                    // 拖不动就算了
                }
            }
        };

        return card;
    }

    private static Brush Brush(string hex)
    {
        SolidColorBrush brush = new((Color)ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }
}
