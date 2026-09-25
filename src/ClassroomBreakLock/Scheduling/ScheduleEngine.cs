using ClassroomBreakLock.Config;

namespace ClassroomBreakLock.Scheduling;

/// <summary>调度决策的结果。</summary>
public enum LockDecision
{
    /// <summary>应该上锁。</summary>
    Lock,

    /// <summary>应该解锁（课间 / 课前提前解锁窗口 / 休息日）。</summary>
    Unlock
}

/// <summary>一次调度判定的完整解释，便于设置界面上做"当前状态"预览与日志。</summary>
public sealed record ScheduleVerdict(
    LockDecision Decision,
    string Reason,
    ClassPeriod? CurrentPeriod,
    ClassPeriod? NextPeriod,
    TimeSpan? UnlockAt,
    TimeSpan? LockAt)
{
    public bool ShouldLock => Decision == LockDecision.Lock;

    public string Describe()
    {
        var cur = CurrentPeriod is null ? "无" : $"{CurrentPeriod.Name}";
        var next = NextPeriod is null ? "无" : $"{NextPeriod.Name}({NextPeriod.Start})";
        return $"{(ShouldLock ? "锁定" : "解锁")} | 依据：{Reason} | 当前节次：{cur} | 下一节次：{next}";
    }
}

/// <summary>
/// 作息表判定引擎——把"现在几点"翻译成"该锁还是该开"。
///
/// 判定优先级（从上到下，命中即返回）：
///   1. 总开关关闭        -> 解锁
///   2. 当日日期覆盖规则  -> 按覆盖
///   3. 该星期未启用      -> 解锁
///   4. 落在某节课时段内  -> 锁（若该时段 LockDuring=false 则解锁）
///   5. 处于课前提前解锁窗口 -> 解锁
///   6. 课间（两节课之间）  -> 锁还是解锁，由"课间是否解锁 + 学生能否自由使用"策略决定
///   7. 当天所有课都上完  -> 解锁
///   8. 第一节课之前      -> 解锁
/// </summary>
public sealed class ScheduleEngine
{
    private readonly AppConfig _cfg;

    public ScheduleEngine(AppConfig cfg) => _cfg = cfg;

    public ScheduleVerdict Evaluate(DateTime now)
    {
        if (!_cfg.Enabled)
            return new ScheduleVerdict(LockDecision.Unlock, "总开关已关闭（维护模式）", null, null, null, null);

        // 2) 具体日期覆盖
        var today = DateOnly.FromDateTime(now);
        var ov = _cfg.Overrides.FirstOrDefault(o => o.DateValue == today);
        if (ov is not null && !ov.Locked)
        {
            return new ScheduleVerdict(LockDecision.Unlock,
                $"日期覆盖：{ov.Note}".TrimEnd('：'), null, null, null, null);
        }

        var dayIndex = (int)now.DayOfWeek; // 0=周日
        if (dayIndex < 0 || dayIndex >= _cfg.Weekly.Count)
            return new ScheduleVerdict(LockDecision.Unlock, "星期表越界", null, null, null, null);

        var day = _cfg.Weekly[dayIndex];
        if (ov is not null && ov.Locked)
        {
            // 强制上锁日：即使该星期未启用也锁（用于调休上课）
        }
        else if (!day.Enabled)
        {
            return new ScheduleVerdict(LockDecision.Unlock, "今日未启用锁屏（休息日）", null, null, null, null);
        }

        var periods = day.Periods
            .Where(p => p.EndTime > p.StartTime)
            .OrderBy(p => p.StartTime)
            .ToList();

        if (periods.Count == 0)
            return new ScheduleVerdict(LockDecision.Unlock, "今日无课次安排", null, null, null, null);

        var t = now.TimeOfDay;

        // 4) 时段内
        for (int i = 0; i < periods.Count; i++)
        {
            var p = periods[i];
            if (t >= p.StartTime && t < p.EndTime)
            {
                var next = i + 1 < periods.Count ? periods[i + 1] : null;
                if (!p.LockDuring)
                {
                    return new ScheduleVerdict(LockDecision.Unlock,
                        $"「{p.Name}」设为不上锁", p, next, null, null);
                }
                return new ScheduleVerdict(LockDecision.Lock,
                    $"处于「{p.Name}」上课时段", p, next, null, p.EndTime);
            }
        }

        // 5) 课前提前解锁窗口 —— 对"下一个即将开始的、需要上锁的时段"生效
        var nextLocking = periods.FirstOrDefault(p => p.LockDuring && p.StartTime > t);
        if (nextLocking is not null)
        {
            var lead = nextLocking.EarlyUnlockMinutesOverride ?? _cfg.EarlyUnlockMinutes;
            if (lead > 0)
            {
                var windowStart = nextLocking.StartTime - TimeSpan.FromMinutes(lead);
                // 如果当前还处在上一节课里，上面的"时段内"分支已经处理过了；
                // 这里处理的是课间进入了提前窗口的情况。
                if (t >= windowStart && t < nextLocking.StartTime)
                {
                    return new ScheduleVerdict(LockDecision.Unlock,
                        $"「{nextLocking.Name}」课前 {lead} 分钟自动解锁",
                        null, nextLocking, null, nextLocking.StartTime);
                }
            }
        }

        // 6) 课间
        var prev = periods.LastOrDefault(p => p.EndTime <= t);
        var next2 = periods.FirstOrDefault(p => p.StartTime > t);

        if (prev is not null && next2 is not null)
        {
            // 只有"上一节和下一节都要上锁"时才考虑课间锁屏
            if (prev.LockDuring && next2.LockDuring && !_cfg.AutoUnlockDuringBreak)
            {
                var lockAt = prev.EndTime + TimeSpan.FromSeconds(_cfg.LockDelaySeconds);
                if (t < lockAt)
                {
                    return new ScheduleVerdict(LockDecision.Unlock,
                        $"刚下课 {_cfg.LockDelaySeconds} 秒缓冲期", null, next2, null, lockAt);
                }
                return new ScheduleVerdict(LockDecision.Lock,
                    $"「{prev.Name}」与「{next2.Name}」之间课间锁定",
                    null, next2, null, next2.StartTime);
            }

            return new ScheduleVerdict(LockDecision.Unlock,
                _cfg.AutoUnlockDuringBreak ? "设置为课间不锁屏" : "相邻时段无需锁定",
                null, next2, null, null);
        }

        // 7) 课程全部结束
        if (prev is not null && next2 is null)
            return new ScheduleVerdict(LockDecision.Unlock, "今日课程已全部结束", null, null, null, null);

        // 8) 第一节课之前
        if (prev is null && next2 is not null)
            return new ScheduleVerdict(LockDecision.Unlock,
                $"上课前自由使用（首节 {next2.Start}）", null, next2, null, next2.StartTime);

        return new ScheduleVerdict(LockDecision.Unlock, "未匹配任何规则", null, null, null, null);
    }

    /// <summary>取今日生效的作息表（供 UI 展示）。</summary>
    public IReadOnlyList<ClassPeriod> GetTodayPeriods(DateTime now)
    {
        var dayIndex = (int)now.DayOfWeek;
        if (dayIndex < 0 || dayIndex >= _cfg.Weekly.Count) return Array.Empty<ClassPeriod>();
        var day = _cfg.Weekly[dayIndex];
        if (!day.Enabled) return Array.Empty<ClassPeriod>();
        return day.Periods.OrderBy(p => p.StartTime).ToList();
    }

    /// <summary>
    /// 现在是否**正处于某个上课时段内**（不管该时段是否设置了锁屏）。
    ///
    /// 用途：「下课」按钮的第三层确认 —— 老师在上课时间点下课是反常操作，
    /// 需要额外确认一次。所以这里判的是"时间上是否在上课"，而不是"当前是否该锁"。
    ///
    /// 与 Evaluate() 的区别：Evaluate 会被总开关、日期覆盖、课前窗口等影响，
    /// 这里只看作息表本身的时段归属。
    /// </summary>
    public ClassPeriod? GetCurrentPeriod(DateTime now)
    {
        // 总开关关闭 / 休息日 / 全天放假的覆盖日，都不算"上课时间"
        if (!_cfg.Enabled) return null;

        var today = DateOnly.FromDateTime(now);
        var ov = _cfg.Overrides.FirstOrDefault(o => o.DateValue == today);
        if (ov is not null && !ov.Locked) return null;

        var dayIndex = (int)now.DayOfWeek;
        if (dayIndex < 0 || dayIndex >= _cfg.Weekly.Count) return null;

        var day = _cfg.Weekly[dayIndex];
        // 强制上锁日（调休）即使该星期原本未启用，也按作息表判
        if (!day.Enabled && !(ov is not null && ov.Locked)) return null;

        var t = now.TimeOfDay;
        return day.Periods
            .Where(p => p.EndTime > p.StartTime)
            .OrderBy(p => p.StartTime)
            .FirstOrDefault(p => t >= p.StartTime && t < p.EndTime);
    }

    /// <summary>现在是否处于上课时段内。</summary>
    public bool IsDuringClass(DateTime now) => GetCurrentPeriod(now) is not null;
}
