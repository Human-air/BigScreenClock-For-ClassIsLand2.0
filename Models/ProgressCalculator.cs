using System;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 课程/时段进行进度计算（纯算法，无任何外部依赖，便于单元测试）。
/// 上课时显示本课已进行进度，课间显示当前休息进度，均用同一公式。
/// </summary>
public static class ProgressCalculator
{
    /// <summary>
    /// 给定当前时刻与时段起止，返回 0~100 的进行进度（clamp）。
    /// 用与课程定位相同的时刻与时段起止计算，保证时间显示与进度一致。
    /// </summary>
    public static double Calc(TimeSpan now, TimeSpan start, TimeSpan end)
    {
        var total = (end - start).TotalSeconds;
        if (total <= 0) return 0;
        return Math.Clamp((now - start).TotalSeconds / total * 100, 0, 100);
    }
}
