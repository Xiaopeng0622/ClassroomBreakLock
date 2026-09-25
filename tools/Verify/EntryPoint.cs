namespace BreakLockVerify;

/// <summary>
/// 校验入口：密码学 + 作息表引擎 + 悬浮按钮几何。
/// 直接引用主程序源码，保证验证的就是实际运行的那份逻辑。
/// </summary>
public static class EntryPoint
{
    public static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        var cryptoFailures = CryptoChecks.Run();
        Console.WriteLine();
        var scheduleFailures = ScheduleChecks.Run();
        Console.WriteLine();
        var floatFailures = FloatingButtonChecks.Run();
        Console.WriteLine();
        var authCfgFailures = AuthConfigChecks.Run();
        Console.WriteLine();
        var visibilityFailures = WindowVisibilityChecks.Run();
        Console.WriteLine();
        var hoverFailures = HoverStateChecks.Run();
        Console.WriteLine();
        var quoteFailures = DailyQuoteChecks.Run();
        Console.WriteLine();
        var appearanceFailures = AppearanceChecks.Run();

        var total = cryptoFailures + scheduleFailures + floatFailures
                  + authCfgFailures + visibilityFailures + hoverFailures
                  + quoteFailures + appearanceFailures;
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine(total == 0
            ? "全部校验通过 ✓"
            : $"共 {total} 项失败 ✗（密码学 {cryptoFailures}，作息表 {scheduleFailures}，" +
              $"悬浮按钮 {floatFailures}，认证配置 {authCfgFailures}，" +
              $"窗口可见性 {visibilityFailures}，吸附交互 {hoverFailures}，" +
              $"每日一言 {quoteFailures}，个性化 {appearanceFailures}）");
        return total;
    }
}
