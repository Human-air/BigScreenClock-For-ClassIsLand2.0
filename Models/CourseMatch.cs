namespace EveningSelfStudyClock.Models;

/// <summary>
/// 「触发课程」的匹配规则：**精确匹配**（忽略首尾空白与大小写）。
///
/// 历史：早期用「包含匹配」（选定「英语」时「职场通用英语（一）」也算命中，
/// 当年是为了让「晚自习」能匹配「晚自习（语文）」），但设置页是具体课程下拉框，
/// 用户按字面理解选择课程，包含匹配会连带触发名字里含该词的其它课程（issue #1）。
/// 现在改为精确匹配；要触发「晚自习（语文）」这类课程，请在下拉框里直接选它本身。
/// </summary>
public static class CourseMatch
{
    /// <summary>课程名是否命中目标列表中的任意一项（精确匹配）。</summary>
    public static bool IsMatch(string? courseName, IEnumerable<string>? targets)
    {
        if (string.IsNullOrWhiteSpace(courseName) || targets == null) return false;

        var name = courseName.Trim();
        foreach (var target in targets)
        {
            if (string.IsNullOrWhiteSpace(target)) continue;
            if (string.Equals(name, target.Trim(), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
