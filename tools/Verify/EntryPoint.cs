namespace BreakLockVerify;

/// <summary>
/// 校验入口：跑 TOTP/PBKDF2 密码学测试 + 作息表引擎边界测试。
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

        var total = cryptoFailures + scheduleFailures;
        Console.WriteLine();
        Console.WriteLine(new string('=', 60));
        Console.WriteLine(total == 0
            ? "全部校验通过 ✓"
            : $"共 {total} 项失败 ✗（密码学 {cryptoFailures}，作息表 {scheduleFailures}）");
        return total;
    }
}
