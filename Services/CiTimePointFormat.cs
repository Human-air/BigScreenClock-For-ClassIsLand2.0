using System.Globalization;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 照搬 ClassIsland 的「时间点附加信息」显示格式（ClassIsland.Core.Converters 里那几个转换器）。
/// 大屏的课程信息行末尾就用这里的输出，保证与 CI 主界面看到的一模一样。
/// </summary>
/// <remarks>
/// CI 的显示由 DetailIndex 决定（0~5）：
/// 0 时间点持续时间（起止时刻，如 17:00-17:30）、1 已过时间/持续时间（4m/1h30m）、
/// 2 剩余时间/持续时间（-1h25m/1h30m）、3 已过时间百分比（6%）、
/// 4 剩余时间（-1h25m）、5 倒计时（-1:24:51）。
/// </remarks>
public static class CiTimePointFormat
{
    /// <summary>附加信息类型 → 显示文本。0（时间点持续时间）返回空串：课程行本身就写着起止时间。</summary>
    public static string ExtraInfo(int type, long elapsedSeconds, long leftSeconds, long totalSeconds)
    {
        return type switch
        {
            1 => Multi(elapsedSeconds, totalSeconds, ceiling: false),
            2 => "-" + Multi(leftSeconds, totalSeconds, ceiling: true),
            3 => Percent(elapsedSeconds, totalSeconds),
            4 => "-" + Single(leftSeconds, ceiling: true),
            5 => "-" + Exact(leftSeconds),
            _ => "",
        };
    }

    /// <summary>剩余时间（精确到秒）：1:24:51 / 24:51 / 45s。</summary>
    public static string Exact(long seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalSeconds >= 3600)
            return $"{(long)Math.Floor(span.TotalHours)}:{span.Minutes:00}:{span.Seconds:00}";
        if (span.TotalSeconds >= 60)
            return $"{span.Minutes}:{span.Seconds:00}";
        if (span.TotalSeconds >= 0)
            return $"{span.Seconds}s";
        return "";
    }

    /// <summary>按分钟显示：1h30m / 4min（ceiling = 剩余时间那样向上取整）。</summary>
    public static string Single(long seconds, bool ceiling)
    {
        var totalMinutes = TimeSpan.FromSeconds(seconds).TotalMinutes;
        var rounded = ceiling ? Math.Ceiling(totalMinutes) : Math.Floor(totalMinutes);
        var span = TimeSpan.FromMinutes(rounded);

        if (span.TotalHours >= 1)
            return $"{(long)Math.Floor(span.TotalHours)}h{span.Minutes:00}m";
        if (span.TotalSeconds >= 0)
            return $"{span.Minutes}min";
        return "";
    }

    /// <summary>「已过/剩余」与「持续时间」拼在一起：4m/1h30m 或 -1h25m/1h30m（减号由调用方加）。</summary>
    public static string Multi(long seconds, long totalSeconds, bool ceiling)
    {
        // CI 先把秒整除 60，再按 ceiling 取整（注意不是对小数分钟取整）
        var first = seconds / 60;
        var firstSpan = TimeSpan.FromMinutes(ceiling ? Math.Ceiling((double)first) : Math.Floor((double)first));
        var totalSpan = TimeSpan.FromMinutes(Math.Round(totalSeconds / 60.0));
        return ShortSpan(firstSpan) + "/" + ShortSpan(totalSpan);
    }

    /// <summary>已过时间百分比，用当前区域设置（中文下就是「6%」）。</summary>
    public static string Percent(long elapsedSeconds, long totalSeconds)
    {
        if (totalSeconds <= 0) return "";
        var ratio = (double)elapsedSeconds / totalSeconds;
        return ratio.ToString("P0", CultureInfo.CurrentCulture);
    }

    private static string ShortSpan(TimeSpan span)
    {
        if (span.TotalHours >= 1)
            return $"{(long)Math.Floor(span.TotalHours)}h{span.Minutes:00}m";
        return $"{span.Minutes}m";
    }
}
