using ClassroomBreakLock.Config;
using ClassroomBreakLock.Scheduling;

namespace BreakLockVerify;

/// <summary>作息表引擎边界测试：验证"什么时候该锁、什么时候该开"符合预期。</summary>
public static class ScheduleChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        var cfg = AppConfig.CreateDefault();
        cfg.EarlyUnlockMinutes = 2;
        cfg.LockDelaySeconds = 5;
        cfg.AutoUnlockDuringBreak = false;

        var wed = (int)DayOfWeek.Wednesday;
        cfg.Weekly[wed].Enabled = true;
        var engine = new ScheduleEngine(cfg);

        DateTime Wed(int h, int m, int s = 0) => new(2025, 9, 24, h, m, s);

        Console.WriteLine("=== 场景：周三作息（第1节 08:00-08:45，第2节 08:55-09:40）===");

        var v1 = engine.Evaluate(Wed(7, 0));
        Check("07:00 上课前 → 解锁", !v1.ShouldLock, v1.Reason);

        var v2 = engine.Evaluate(Wed(8, 10));
        Check("08:10 第1节中 → 锁定", v2.ShouldLock, v2.Reason);

        var v3 = engine.Evaluate(Wed(8, 50));
        Check("08:50 课间（未进提前窗口）→ 锁定", v3.ShouldLock, v3.Reason);

        var v4 = engine.Evaluate(Wed(8, 53, 30));
        Check("08:53:30 进入课前2分钟窗口 → 解锁", !v4.ShouldLock, v4.Reason);

        var v5 = engine.Evaluate(Wed(8, 55, 30));
        Check("08:55:30 已上课 → 锁定", v5.ShouldLock, v5.Reason);

        var v6 = engine.Evaluate(Wed(8, 45, 3));
        Check("08:45:03 下课后缓冲期内 → 解锁", !v6.ShouldLock, v6.Reason);

        var v7 = engine.Evaluate(Wed(8, 45, 10));
        Check("08:45:10 缓冲期已过 → 锁定", v7.ShouldLock, v7.Reason);

        var v8 = engine.Evaluate(Wed(12, 0));
        Check("12:00 午休（LockDuring=false）→ 解锁", !v8.ShouldLock, v8.Reason);

        var v9 = engine.Evaluate(Wed(22, 0));
        Check("22:00 晚自习结束 → 解锁", !v9.ShouldLock, v9.Reason);

        Console.WriteLine();
        Console.WriteLine("=== 星期与日期覆盖 ===");
        var sunday = new DateTime(2025, 9, 28, 8, 10, 0);
        var v10 = engine.Evaluate(sunday);
        Check("周日 08:10 → 解锁（默认周末不启用）", !v10.ShouldLock, v10.Reason);

        cfg.Overrides.Add(new DateOverride { Date = "2025-09-24", Locked = false, Note = "运动会" });
        var engine3 = new ScheduleEngine(cfg);
        var v12 = engine3.Evaluate(Wed(8, 10));
        Check("周三被覆盖为放假 → 解锁", !v12.ShouldLock, v12.Reason);

        Console.WriteLine();
        Console.WriteLine("=== 课前提前解锁分钟数可按节次覆盖 ===");
        var cfg2 = AppConfig.CreateDefault();
        cfg2.EarlyUnlockMinutes = 2;
        cfg2.Weekly[wed].Enabled = true;
        var p2 = cfg2.Weekly[wed].Periods.First(p => p.Name == "第2节");
        p2.EarlyUnlockMinutesOverride = 10;
        var engine4 = new ScheduleEngine(cfg2);
        var v13 = engine4.Evaluate(Wed(8, 46, 30));
        Check("08:46:30 进入第2节提前10分钟窗口 → 解锁", !v13.ShouldLock, v13.Reason);

        var v13b = engine4.Evaluate(Wed(8, 40));
        Check("08:40 第1节中（提前窗口不覆盖上课态）→ 仍锁定", v13b.ShouldLock, v13b.Reason);

        Console.WriteLine();
        Console.WriteLine("=== 总开关 / 课间解锁策略 ===");
        var cfg3 = AppConfig.CreateDefault();
        cfg3.Enabled = false;
        cfg3.Weekly[wed].Enabled = true;
        var v14 = new ScheduleEngine(cfg3).Evaluate(Wed(8, 10));
        Check("总开关关闭 → 全天解锁", !v14.ShouldLock, v14.Reason);

        var cfg4 = AppConfig.CreateDefault();
        cfg4.Weekly[wed].Enabled = true;
        cfg4.AutoUnlockDuringBreak = true;
        var v15 = new ScheduleEngine(cfg4).Evaluate(Wed(8, 50));
        Check("课间解锁策略开启 → 课间保持解锁", !v15.ShouldLock, v15.Reason);

        return failures;
    }
}
