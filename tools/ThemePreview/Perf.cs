using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ClassroomBreakLock.Auth;
using ClassroomBreakLock.Config;
using ClassroomBreakLock.Views;

namespace ThemePreview;

/// <summary>卡顿诊断：量化「构造 / 首次渲染 / 切换页面」的耗时。</summary>
internal static class Perf
{
    private const int Width = 1240;
    private const int Height = 880;

    public static void Run(string outDir)
    {
        RunImporterSelfTest();
        RunBridgeSelfTest();

        Directory.CreateDirectory(outDir);

        // 自定义圆角弹窗预览（离线合成，不出现在屏幕上）
        ShootDialog(
            Path.Combine(outDir, "dialog-info.png"),
            AppDialog.BuildPreview(
                "导入完成",
                "已导入整周作息（共 133 个时段）：\n\n周日 17 节、周一 18 节、周二 18 节、周三 18 节、周四 18 节、周五 27 节、周六 17 节\n\n单双周已合并为同一份作息（课间锁只看时间，不区分单双周）。",
                false,
                false),
            560,
            330);

        ShootDialog(
            Path.Combine(outDir, "dialog-confirm.png"),
            AppDialog.BuildPreview(
                "确认退出",
                "确定要退出课间锁吗？退出后课间将不再自动锁屏。",
                true,
                true),
            480,
            260);

        AppConfig cfg = AppConfig.CreateDefault();
        cfg.TestMode = true;
        cfg.ClassroomName = "高一(3)班";
        AuthService auth = new AuthService(cfg);

        // 悬浮按钮 + 气泡（验证长提示词是否完整展开、按钮位置是否稳定）
        var floatWin = new FloatingButtonWindow(cfg);
        floatWin.ShowBubble("测试模式：不会自动锁屏\n双击图标可进设置", 8);
        Shoot(floatWin, Path.Combine(outDir, "floating.png"));
        Console.WriteLine($"[float] 气泡窗口尺寸 = {floatWin.Width:F0} x {floatWin.Height:F0}");

        Stopwatch sw = Stopwatch.StartNew();
        SettingsWindow settings = new SettingsWindow(cfg, auth);
        Console.WriteLine($"[perf] SettingsWindow ctor      : {sw.ElapsedMilliseconds} ms");

        settings.WindowStartupLocation = WindowStartupLocation.Manual;
        settings.Left = -8000;
        settings.Top = -8000;
        settings.ShowInTaskbar = false;
        settings.ShowActivated = false;
        settings.Topmost = false;
        settings.WindowState = WindowState.Normal;
        settings.Width = Width;
        settings.Height = Height;

        sw.Restart();
        settings.Show();
        Pump();
        settings.UpdateLayout();
        Pump();
        Console.WriteLine($"[perf] SettingsWindow first show: {sw.ElapsedMilliseconds} ms");

        sw.Restart();
        Shoot(settings, Path.Combine(outDir, "perf_first.png"));
        Console.WriteLine($"[perf] SettingsWindow 1st render: {sw.ElapsedMilliseconds} ms");

        List<TabControl> tabs = FindTabControls(settings);
        if (tabs.Count > 0)
        {
            TabControl tc = tabs[0];
            Console.WriteLine($"[perf] tabs = {tc.Items.Count}");

            // 第一轮：逐页切过去（不含截图，纯切换开销）
            for (int i = 0; i < tc.Items.Count; i++)
            {
                sw.Restart();
                tc.SelectedIndex = i;
                Pump();
                settings.UpdateLayout();
                Console.WriteLine($"[perf] switch -> tab {i} ({((TabItem)tc.Items[i]).Header}) : {sw.ElapsedMilliseconds} ms");
            }

            // 第二轮：倒着切回去，看内容是否被重建
            for (int i = tc.Items.Count - 1; i >= 0; i--)
            {
                sw.Restart();
                tc.SelectedIndex = i;
                Pump();
                settings.UpdateLayout();
                Console.WriteLine($"[perf] revisit tab {i} ({((TabItem)tc.Items[i]).Header}) : {sw.ElapsedMilliseconds} ms");
            }
        }

        sw.Restart();
        Shoot(settings, Path.Combine(outDir, "perf_last.png"));
        Console.WriteLine($"[perf] screenshot render       : {sw.ElapsedMilliseconds} ms");

        Console.WriteLine($"[perf] visual count (settings)  : {CountVisuals(settings)}");

        // 第二次构造（JIT 已热）——反映真实“打开设置”的开销
        sw.Restart();
        SettingsWindow warm = new SettingsWindow(cfg, auth);
        Console.WriteLine($"[perf] SettingsWindow ctor #2   : {sw.ElapsedMilliseconds} ms");
        warm.Close();

        Stopwatch lw = Stopwatch.StartNew();
        LockWindow lwnd = new LockWindow(cfg, auth);
        Console.WriteLine($"[perf] LockWindow ctor          : {lw.ElapsedMilliseconds} ms");
        lwnd.WindowStartupLocation = WindowStartupLocation.Manual;
        lwnd.Left = -8000;
        lwnd.Top = -8000;
        lwnd.ShowInTaskbar = false;
        lwnd.ShowActivated = false;
        lwnd.Topmost = false;
        lwnd.WindowState = WindowState.Normal;
        lwnd.Width = 1600;
        lwnd.Height = 900;
        lw.Restart();
        lwnd.Show();
        Pump();
        lwnd.UpdateLayout();
        Pump();
        Console.WriteLine($"[perf] LockWindow first show    : {lw.ElapsedMilliseconds} ms");

        settings.Close();
        lwnd.Hide();
        Console.WriteLine("PERF_DONE");
    }

    /// <summary>ClassIsland 同步接收端自检：真的起一个本地端口，真的发一次请求。</summary>
    private static void RunBridgeSelfTest()
    {
        const int port = 8799;
        var events = new List<string>();

        using var bridge = new ClassroomBreakLock.Sync.ClassIslandBridge(port, "secret");
        bridge.EventReceived += e => events.Add(e.Describe());
        bridge.Start();

        Console.WriteLine($"[bridge] running={bridge.IsRunning} prefix={bridge.Prefix} err={bridge.LastError ?? "-"}");

        if (!bridge.IsRunning)
        {
            return;
        }

        using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(5) };

        try
        {
            string state = http.GetStringAsync($"http://127.0.0.1:{port}/state?token=secret").GetAwaiter().GetResult();
            Console.WriteLine($"[bridge] GET /state(带令牌) -> {Truncate(state)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] GET /state 失败: {ex.Message}");
        }

        try
        {
            http.GetStringAsync($"http://127.0.0.1:{port}/state").GetAwaiter().GetResult();
            Console.WriteLine("[bridge] 未带令牌竟然通过了（不应发生）");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] 未带令牌被拒（正确）: {ex.Message}");
        }

        try
        {
            string bad = http.GetStringAsync($"http://127.0.0.1:{port}/class/start?token=wrong").GetAwaiter().GetResult();
            Console.WriteLine($"[bridge] 错误 token 竟然通过了（不应发生）: {Truncate(bad)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] 错误 token 被拒（正确）: {ex.Message}");
        }

        try
        {
            string start = http.GetStringAsync($"http://127.0.0.1:{port}/class/start?subject=数学&token=secret").GetAwaiter().GetResult();
            Console.WriteLine($"[bridge] POST 上课 -> {Truncate(start)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] POST 上课 失败: {ex.Message}");
        }

        try
        {
            string end = http.GetStringAsync($"http://127.0.0.1:{port}/class/end?token=secret").GetAwaiter().GetResult();
            Console.WriteLine($"[bridge] POST 下课 -> {Truncate(end)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] POST 下课 失败: {ex.Message}");
        }

        try
        {
            string unknown = http.GetStringAsync($"http://127.0.0.1:{port}/nope?token=secret").GetAwaiter().GetResult();
            Console.WriteLine($"[bridge] 未知路径 -> {Truncate(unknown)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge] 未知路径（预期非 200）: {ex.Message}");
        }

        Thread.Sleep(120);
        Console.WriteLine($"[bridge] 收到的事件: {string.Join(" / ", events)}（共 {events.Count} 次）");
        Console.WriteLine($"[bridge] 计数={bridge.EventCount} 最后={bridge.LastEventText}");
    }

    private static string Truncate(string s) => s.Length <= 140 ? s : s.Substring(0, 140) + "…";

    /// <summary>课表导入器自检：用样例 YAML / CSV / CESE 验证解析结果。</summary>
    private static void RunImporterSelfTest()
    {
        string yaml = """
            periods:
              - name: 早读
                start: "07:30"
                end: "07:55"
                lock: false
              - name: 第1节
                start: 08:00
                end: 08:45
                early: 3
              - name: 第2节
                start: 08:55
                end: 09:40
            第3节: 10:00-10:45
            """;

        string cese = """
            version: 1
            subjects:
            - name: 语文
              simplified_name: 语
              teacher: 谭道菊
              room: ''
            schedules:
            - name: 周一
              classes:
              - subject: 语文
                start_time: 06:50:00
                end_time: 07:20:00
              - subject: 物理
                start_time: 08:00:00
                end_time: 08:40:00
              enable_day: 1
              weeks: all
            - name: 周五（单周）
              classes:
              - subject: 数学
                start_time: 14:30:00
                end_time: 15:10:00
              enable_day: 5
              weeks: odd
            - name: 周五(双周)
              classes:
              - subject: 数学
                start_time: 14:30:00
                end_time: 15:10:00
              - subject: 地理
                start_time: 16:15:00
                end_time: 16:55:00
              enable_day: 5
              weeks: even
            """;

        string csv = "节次,开始,结束,锁屏\n第1节,08:00,08:45,是\n第2节,08:55,09:40,否\n";

        foreach ((string label, string name, string content) in new[]
                 {
                     ("YAML 简单", "cbl_test.yml", yaml),
                     ("CESE 整周", "cbl_cese.yml", cese),
                     ("CSV", "cbl_test.csv", csv)
                 })
        {
            string path = Path.Combine(Path.GetTempPath(), name);
            File.WriteAllText(path, content);
            ScheduleImporter.Result r = ScheduleImporter.ParseFile(path);

            Console.WriteLine($"[import] {label}: 整周={r.HasWeekInfo} 合计 {r.TotalPeriodCount} 节, {r.Warnings.Count} 条警告");

            if (r.HasWeekInfo)
            {
                foreach (ScheduleImporter.DayResult day in r.Days)
                {
                    string list = string.Join(", ", day.Periods.Select(p => $"{p.Name}{p.Start}-{p.End}"));
                    Console.WriteLine($"[import]   {day.DayName}({day.DayIndex}): {day.Periods.Count} 节 -> {list}");
                }
            }
            else
            {
                foreach (ClassPeriod p in r.Periods)
                {
                    Console.WriteLine($"[import]   {p.Name} {p.Start}-{p.End} lock={p.LockDuring} early={p.EarlyUnlockMinutesOverride?.ToString() ?? "-"}");
                }
            }

            foreach (string w in r.Warnings)
            {
                Console.WriteLine($"[import]   ! {w}");
            }
        }

        // 用晓芃真实导出的那份文件跑一遍（存在才跑）
        string real = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".openclaw", "media", "inbound",
            "26.3.18---d5f070aa-7047-400d-a0c7-a5e2087301cd.yaml");

        if (File.Exists(real))
        {
            ScheduleImporter.Result r = ScheduleImporter.ParseFile(real);
            Console.WriteLine($"[import] 晓芃真实课表: 整周={r.HasWeekInfo} 合计 {r.TotalPeriodCount} 节, {r.Warnings.Count} 条警告");
            foreach (ScheduleImporter.DayResult day in r.Days)
            {
                Console.WriteLine($"[import]   {day.DayName}({day.DayIndex}): {day.Periods.Count} 节 首节 {day.Periods.FirstOrDefault()?.Start}  末节 {day.Periods.LastOrDefault()?.End}");
            }

            foreach (string w in r.Warnings.Take(5))
            {
                Console.WriteLine($"[import]   ! {w}");
            }
        }
    }

    /// <summary>把弹窗内容离线合成为一张预览图（不依赖真实显示）。</summary>
    private static void ShootDialog(string path, Window dialog, int width, int height)
    {
        if (dialog.Content is not FrameworkElement root)
        {
            return;
        }

        dialog.Content = null;   // 从窗口里拿出来单独渲染
        root.Margin = new Thickness(24);

        var host = new Grid
        {
            Width = width,
            Height = height,
            Background = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3))
        };
        host.Children.Add(root);

        host.Measure(new Size(width, height));
        host.Arrange(new Rect(0, 0, width, height));
        host.UpdateLayout();

        var visual = new DrawingVisual();
        using (DrawingContext dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new VisualBrush(host), null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream fs = File.Create(path);
        encoder.Save(fs);
    }

    private static void Shoot(Window w, string path)
    {
        FrameworkElement root = (FrameworkElement)w.Content;
        root.Measure(new Size(w.Width, w.Height));
        root.Arrange(new Rect(0, 0, w.Width, w.Height));
        root.UpdateLayout();

        DrawingVisual dv = new DrawingVisual();
        using (DrawingContext dc = dv.RenderOpen())
        {
            dc.DrawRectangle(w.Background ?? Brushes.White, null, new Rect(0, 0, w.Width, w.Height));
            dc.DrawRectangle(
                new VisualBrush(root) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, w.Width, w.Height));
        }

        RenderTargetBitmap rtb = new RenderTargetBitmap((int)w.Width, (int)w.Height, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);

        PngBitmapEncoder enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (FileStream fs = File.Create(path))
        {
            enc.Save(fs);
        }
    }

    private static List<TabControl> FindTabControls(DependencyObject root)
    {
        List<TabControl> found = new List<TabControl>();
        Walk(root, found);
        return found;
    }

    private static void Walk(DependencyObject root, List<TabControl> found)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is TabControl tc)
            {
                found.Add(tc);
            }
            Walk(child, found);
        }
    }

    private static int CountVisuals(DependencyObject root)
    {
        int total = 1;
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            total += CountVisuals(VisualTreeHelper.GetChild(root, i));
        }
        return total;
    }

    private static void Pump()
    {
        for (int i = 0; i < 6; i++)
        {
            Dispatcher.CurrentDispatcher.Invoke(new Action(() => { }), DispatcherPriority.ApplicationIdle);
        }
    }
}
