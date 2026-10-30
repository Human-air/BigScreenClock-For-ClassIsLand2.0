namespace EveningSelfStudyClock.Models;

/// <summary>
/// 「今天下一节目标课程几点开始」的纯计算。课表解析在
/// <see cref="ViewModels.FullScreenClockViewModel"/> 里，这里只管从时段表里挑。
///
/// 单独拆出来是为了能测：课前预烘的快慢全看这个判断准不准 —— 早了两分钟白占内存，
/// 晚了大屏就在开屏那一刻照旧集中解码（学校那台 CI 的看门狗就是这么被顶爆的）。
/// </summary>
public static class ClassStartSchedule
{
    /// <summary>课表里的一个时段。</summary>
    public readonly record struct Slot(TimeSpan Start, TimeSpan End, bool IsClass, string? Subject);

    /// <summary>
    /// <paramref name="now"/> 之后、今天还没开始的目标课程里最早的那一节的上课时刻。
    /// 没有（今天已经没有目标课 / 正在上的这一节就是目标课）返回 null。
    /// </summary>
    public static DateTime? NextTargetStart(IEnumerable<Slot> slots, Func<string?, bool> isTarget, DateTime now)
    {
        DateTime? best = null;
        foreach (var slot in slots)
        {
            if (!slot.IsClass || string.IsNullOrEmpty(slot.Subject)) continue;
            if (!isTarget(slot.Subject)) continue;
            if (slot.Start <= now.TimeOfDay) continue;

            var at = now.Date + slot.Start;
            if (best is null || at < best) best = at;
        }
        return best;
    }
}
