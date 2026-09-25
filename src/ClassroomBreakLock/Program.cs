using System.Windows;

namespace ClassroomBreakLock;

/// <summary>
/// 程序入口。除了正常的 WPF 启动，还要处理 --watchdog 看门狗模式
/// （看门狗必须是纯控制台逻辑，不能初始化 WPF）。
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // 看门狗模式
        if (args.Length >= 3 && args[0] == "--watchdog")
        {
            if (int.TryParse(args[1], out var pid) && int.TryParse(args[2], out var interval))
            {
                AppHost.RunWatchdog(pid, interval);
                return 0;
            }
            return 1;
        }

        var app = new App();
        app.InitializeComponent();

        AppHost? host = null;
        app.Startup += (_, _) =>
        {
            host = new AppHost();
            host.Start();
        };
        app.Exit += (_, _) => host?.Shutdown();

        return app.Run();
    }
}
