using System.Windows;

namespace ClassroomBreakLock;

public partial class App : System.Windows.Application
{
    /// <summary>标记：只有主程序主动退出时，锁屏窗口才允许关闭。</summary>
    public static bool IsShuttingDown { get; private set; }

    public static void BeginShutdown() => IsShuttingDown = true;
}
