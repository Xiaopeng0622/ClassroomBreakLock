using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Views;

namespace ThemePreview;

/// <summary>
/// 离线渲染工具：把 LockWindow / SettingsWindow 渲染成 PNG，用于验收主题效果。
/// 窗口移到屏幕外 + 只渲染内容，不干扰桌面。
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        string outDir = args.Length > 0 && args[0] != "perf"
            ? args[0]
            : args.Length > 1
                ? args[1]
                : "preview";
        Directory.CreateDirectory(outDir);

        Application app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        app.Startup += (_, _) =>
        {
            try
            {
                if (args.Length > 0 && args[0] == "perf")
                {
                    Perf.Run(outDir);
                    return;
                }
                AppConfig cfg = AppConfig.CreateDefault();
                cfg.TestMode = true;
                cfg.ClassroomName = "高一(3)班";

                // 预览用：让锁屏页出现全部四种认证方式（芯片行才好验收）
                cfg.Auth.UsbKeys.Add(new UsbKey
                {
                    Label = "教师钥匙",
                    VolumeSerial = "1A2B3C4D",
                    KeyFileName = "key.dat",
                    KeyFileHash = "AAAA"
                });
                cfg.Auth.Password.Salt = "x";
                cfg.Auth.Password.Hash = "y";
                cfg.Auth.Totp.Enabled = true;
                cfg.Auth.Totp.Secret = "JBSWY3DPEHPK3PXP";
                cfg.Auth.Emergency.Salt = "x";
                cfg.Auth.Emergency.Hash = "y";

                AuthService auth = new AuthService(cfg);

                Render(new LockWindow(cfg, auth), 1600, 900, Path.Combine(outDir, "lock.png"));

                // 若本机配置里设了自定义背景图，额外渲染一张带背景的锁屏，用于验收外观效果
                try
                {
                    var realCfg = ClassroomBreakLock.Config.ConfigStore.Load();
                    if (realCfg.Appearance.HasImage)
                    {
                        Render(new LockWindow(realCfg, new AuthService(realCfg)), 1600, 900,
                            Path.Combine(outDir, "lock-custom-bg.png"));
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("custom bg preview skipped: " + ex.Message);
                }

                Render(new SettingsWindow(cfg, auth), 1240, 880, Path.Combine(outDir, "settings.png"), 0);
                Render(new SettingsWindow(cfg, auth), 1240, 1080, Path.Combine(outDir, "settings-schedule.png"), 1);
                Render(new SettingsWindow(cfg, auth), 1240, 880, Path.Combine(outDir, "settings-auth.png"), 2);
                // 个性化（锁屏背景）—— 插在认证之后，索引 3
                Render(new SettingsWindow(cfg, auth), 1240, 1000, Path.Combine(outDir, "settings-appearance.png"), 3);
                // 悬浮按钮页：多屏选择 / 透明度 / 靠边吸附（索引已因新增页而后移）
                Render(new SettingsWindow(cfg, auth), 1240, 1180, Path.Combine(outDir, "settings-floating.png"), 4);
                Render(new SettingsWindow(cfg, auth), 1240, 1000, Path.Combine(outDir, "settings-security.png"), 5);

                // 悬浮按钮本体：展开态 vs 吸附缩起态（对比尺寸是否真的变了）
                RenderFloating(cfg, collapsed: false, Path.Combine(outDir, "floating-expanded.png"));
                RenderFloating(cfg, collapsed: true, Path.Combine(outDir, "floating-collapsed.png"));

                // 设置认证门（新增）—— 验证弹窗风格是否与设置页一致
                // 高度传 0 表示"按内容自适应"，避免固定高度把弹窗拉长留白
                Render(SettingsAuthGate.BuildPreview(cfg, auth), 500, 0,
                    Path.Combine(outDir, "gate-auth.png"));

                // 「下课」第二层确认框
                Render(AppDialog.BuildPreview(
                        "确认要下课吗？",
                        "锁屏后需要使用 U 盘、密码或动态码才能解锁。",
                        confirm: true, danger: false, "下课锁屏", "取消"),
                    500, 0, Path.Combine(outDir, "confirm-dismiss.png"));

                // 「下课」第三层确认框（上课时段内触发，danger 样式）
                Render(AppDialog.BuildPreview(
                        "确认在上课时间下课吗？",
                        "当前正处于「第1节」上课时段，这个时间下课属于反常操作。\n\n" +
                        "确认后屏幕将立即锁定。如果只是误触，请选择「取消」。",
                        confirm: true, danger: true, "确认下课", "取消"),
                    500, 0, Path.Combine(outDir, "confirm-dismiss-inclass.png"));

                Console.WriteLine("PREVIEW_OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("PREVIEW_FAIL: " + ex);
            }
            finally
            {
                app.Shutdown();
            }
        };

        app.Run();
    }

    /// <summary>
    /// 渲染悬浮按钮本体。collapsed=true 时模拟"已吸附缩起"状态，
    /// 用来肉眼确认缩起后圆圈尺寸确实变小了（这个点曾经因为 XAML 绑定写错而失效）。
    /// </summary>
    private static void RenderFloating(AppConfig cfg, bool collapsed, string path)
    {
        var c = cfg.Clone();
        c.FloatingButton.SnappedEdge = collapsed ? "right" : "";
        c.FloatingButton.IsCollapsed = collapsed;

        var w = new FloatingButtonWindow(c);
        // 窗口尺寸是 Auto，给它一块足够大的画布再裁到按钮区域
        int size = collapsed ? 90 : 150;
        Render(w, size, size, path);
        w.StopTimer();
    }

    private static void Render(Window w, int width, int height, string path, int tabIndex = -1)    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -8000;
        w.Top = -8000;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.Topmost = false;
        w.WindowState = WindowState.Normal;
        w.Width = width;
        // height <= 0 表示自适应：让窗口按内容定高，渲染器再取实际高度
        bool autoHeight = height <= 0;
        if (!autoHeight)
        {
            w.Height = height;
            w.SizeToContent = SizeToContent.Manual;
        }
        w.Show();

        Pump();
        w.UpdateLayout();
        Pump();

        if (tabIndex >= 0)
        {
            foreach (TabControl tc in FindTabControls(w))
            {
                tc.SelectedIndex = tabIndex;
            }
            Pump();
            w.UpdateLayout();
            Pump();
        }

        if (autoHeight)
        {
            height = (int)Math.Ceiling(w.ActualHeight);
            if (height <= 0) height = 400;
        }

        FrameworkElement root = (FrameworkElement)w.Content;
        root.Measure(new Size(width, height));
        root.Arrange(new Rect(0, 0, width, height));
        root.UpdateLayout();
        Pump();

        DrawingVisual visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            Brush bg = w.Background ?? Brushes.White;
            dc.DrawRectangle(bg, null, new Rect(0, 0, width, height));
            VisualBrush vb = new VisualBrush(root)
            {
                Stretch = Stretch.None,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top
            };
            dc.DrawRectangle(vb, null, new Rect(0, 0, width, height));
        }

        RenderTargetBitmap rtb = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);

        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (FileStream fs = File.Create(path))
        {
            enc.Save(fs);
        }

        w.Hide();
        Console.WriteLine("saved " + path);
    }

    private static IEnumerable<TabControl> FindTabControls(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TabControl tc)
            {
                yield return tc;
            }
            foreach (TabControl sub in FindTabControls(child))
            {
                yield return sub;
            }
        }
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
        }
    }
}
