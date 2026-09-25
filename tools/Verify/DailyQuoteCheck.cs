using System;
using System.Collections.Generic;
using System.Linq;
using ClassroomBreakLock.Config;

namespace BreakLockVerify;

/// <summary>每日一言的选取逻辑校验。</summary>
public static class DailyQuoteChecks
{
    public static int Run()
    {
        var failures = 0;
        void Check(string name, bool ok, string detail = "")
        {
            Console.WriteLine($"{(ok ? "  ✓" : "  ✗")} {name}{(detail.Length > 0 ? "  → " + detail : "")}");
            if (!ok) failures++;
        }

        Console.WriteLine("=== 语录库健康度 ===");
        Check($"语录库非空（共 {DailyQuote.Count} 条）", DailyQuote.Count > 0);
        Check("语录数量足够（≥20，避免重复感）", DailyQuote.Count >= 20, $"{DailyQuote.Count} 条");
        Check("没有空条目或纯空白条目",
            DailyQuote.All.All(q => !string.IsNullOrWhiteSpace(q)));
        Check("没有重复条目",
            DailyQuote.All.Distinct().Count() == DailyQuote.Count,
            $"去重后 {DailyQuote.All.Distinct().Count()} / 原 {DailyQuote.Count}");
        Check("单条长度合理（≤120 字，避免撑破布局）",
            DailyQuote.All.All(q => q.Length <= 120),
            $"最长 {DailyQuote.All.Max(q => q.Length)} 字");

        Console.WriteLine();
        Console.WriteLine("=== 同一天内恒定 ===");
        var day = new DateTime(2026, 3, 15);
        var first = DailyQuote.ForDate(day);
        var sameDayAllEqual = true;
        for (var h = 0; h < 24; h++)
        {
            // 同名不同时刻都必须取到同一句
            if (DailyQuote.ForDate(day.AddHours(h)) != first) { sameDayAllEqual = false; break; }
        }
        Check("当天 0~23 时取到的都是同一句", sameDayAllEqual);
        Check("重复调用结果一致（无随机性）",
            DailyQuote.ForDate(day) == DailyQuote.ForDate(day));

        Console.WriteLine();
        Console.WriteLine("=== 跨天轮换 ===");
        var seen = new HashSet<string>();
        for (var i = 0; i < DailyQuote.Count; i++)
        {
            seen.Add(DailyQuote.ForDate(day.AddDays(i)));
        }
        Check($"连续 {DailyQuote.Count} 天不重复（轮遍整个库）",
            seen.Count == DailyQuote.Count, $"{seen.Count} / {DailyQuote.Count} 种");

        var next = DailyQuote.ForDate(day.AddDays(1));
        Check("相邻两天内容不同", next != first);

        Console.WriteLine();
        Console.WriteLine("=== 边界与健壮性 ===");
        // 极早/极晚的日期不应越界或抛异常
        var extremes = new[]
        {
            new DateTime(1970, 1, 1),
            new DateTime(1980, 6, 15),
            new DateTime(2000, 1, 1),
            new DateTime(2038, 1, 19),
            new DateTime(2099, 12, 31),
        };
        var allOk = extremes.All(d =>
        {
            try { return !string.IsNullOrEmpty(DailyQuote.ForDate(d)); }
            catch { return false; }
        });
        Check("极端日期（1970~2099）都能正常取到语录", allOk);

        // 取到的内容必须真的在库里
        var inLibrary = extremes.All(d => DailyQuote.All.Contains(DailyQuote.ForDate(d)));
        Check("取到的内容确实来自语录库", inLibrary);

        Console.WriteLine();
        Console.WriteLine("=== 展示格式 ===");
        var disp = DailyQuote.ForDisplay(day);
        Check("展示文本带「每日一言 ·」前缀", disp.StartsWith("每日一言 · "), disp.Substring(0, Math.Min(40, disp.Length)) + "…");
        Check("展示文本包含实际语录内容", disp.Contains(DailyQuote.ForDate(day)));

        Console.WriteLine();
        Console.WriteLine("=== 今天实际会显示什么 ===");
        Console.WriteLine($"      {DailyQuote.ForDisplay()}");

        return failures;
    }
}
