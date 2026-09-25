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
                Render(new SettingsWindow(cfg, auth), 1240, 880, Path.Combine(outDir, "settings.png"), 0);
                Render(new SettingsWindow(cfg, auth), 1240, 1080, Path.Combine(outDir, "settings-schedule.png"), 1);
                Render(new SettingsWindow(cfg, auth), 1240, 880, Path.Combine(outDir, "settings-auth.png"), 2);
                Render(new SettingsWindow(cfg, auth), 1240, 1000, Path.Combine(outDir, "settings-security.png"), 4);
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

    private static void Render(Window w, int width, int height, string path, int tabIndex = -1)
    {
        w.WindowStartupLocation = WindowStartupLocation.Manual;
        w.Left = -8000;
        w.Top = -8000;
        w.ShowInTaskbar = false;
        w.ShowActivated = false;
        w.Topmost = false;
        w.WindowState = WindowState.Normal;
        w.Width = width;
        w.Height = height;
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
