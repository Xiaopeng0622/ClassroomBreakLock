using System.Text.Json.Serialization;

namespace ClassroomBreakLock.Config;

/// <summary>
/// 一节课 / 一个自习时段的定义。一天由若干 Period 组成。
/// </summary>
public sealed class ClassPeriod
{
    /// <summary>节次名，例如 "第1节" / "早读" / "午休"。</summary>
    public string Name { get; set; } = "第1节";

    /// <summary>开始时间 HH:mm。</summary>
    public string Start { get; set; } = "08:00";

    /// <summary>结束时间 HH:mm。</summary>
    public string End { get; set; } = "08:45";

    /// <summary>本时段是否要对多媒体上锁。false 表示这个时段允许正常使用。</summary>
    public bool LockDuring { get; set; } = true;

    /// <summary>本时段的课前提前解锁分钟数；null 表示用全局默认值。</summary>
    public int? EarlyUnlockMinutesOverride { get; set; }

    [JsonIgnore]
    public TimeSpan StartTime => ParseTime(Start, new TimeSpan(8, 0, 0));

    [JsonIgnore]
    public TimeSpan EndTime => ParseTime(End, StartTime.Add(TimeSpan.FromMinutes(45)));

    public static TimeSpan ParseTime(string? text, TimeSpan fallback)
        => TimeSpan.TryParse(text, out var t) ? t : fallback;

    public ClassPeriod Clone() => new()
    {
        Name = Name,
        Start = Start,
        End = End,
        LockDuring = LockDuring,
        EarlyUnlockMinutesOverride = EarlyUnlockMinutesOverride
    };
}

/// <summary>按星期几配置的作息表。索引 0=周日 ... 6=周六。</summary>
public sealed class WeeklySchedule
{
    /// <summary>该星期是否启用锁屏（false = 周末/休息日，全天不锁）。</summary>
    public bool Enabled { get; set; } = true;

    public List<ClassPeriod> Periods { get; set; } = new();

    public WeeklySchedule Clone() => new()
    {
        Enabled = Enabled,
        Periods = Periods.Select(p => p.Clone()).ToList()
    };
}

/// <summary>某个具体日期的覆盖规则（调休、考试、临时放假）。</summary>
public sealed class DateOverride
{
    /// <summary>日期 yyyy-MM-dd。</summary>
    public string Date { get; set; } = "2025-01-01";

    /// <summary>这天是否上锁。false = 临时放假，全天解锁。</summary>
    public bool Locked { get; set; }

    /// <summary>备注，例如 "国庆放假" / "调休上课"。</summary>
    public string Note { get; set; } = "";

    [JsonIgnore]
    public DateOnly? DateValue => DateOnly.TryParse(Date, out var d) ? d : null;
}
