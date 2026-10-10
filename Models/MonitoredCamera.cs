namespace EveningSelfStudyClock.Models;

/// <summary>
/// 设置里勾选的一个摄像头，存成 <c>设备名|||接口路径</c> 一行。
///
/// 为什么要连路径一起存：MF 给的友好名**不保证唯一**。同一台机器上两个摄像头报同一个名字
/// 很常见（希沃一体机的内置摄像头 + 视频展台就是），只按名字匹配会把它们当成同一个，
/// 于是「勾了内置的、展台被调用」也会报警。接口路径是唯一的，认人得靠它。
///
/// 老配置里只有名字（没有分隔符），解析出来路径为空 —— 匹配退化成只认名字，
/// 也就是升级前的行为，不会因为多了这个字段就把以前的勾选弄丢。
/// </summary>
public readonly record struct MonitoredCamera(string Name, string SymbolicLink)
{
    /// <summary>名字与路径之间的分隔符。设备名里出现这个组合的概率可以忽略。</summary>
    private const string Separator = "|||";

    /// <summary>解析设置里存的一行。只有名字（老配置）时路径为空。</summary>
    public static MonitoredCamera Parse(string entry)
    {
        entry ??= "";
        var at = entry.IndexOf(Separator, StringComparison.Ordinal);
        return at < 0
            ? new MonitoredCamera(entry.Trim(), "")
            : new MonitoredCamera(entry[..at].Trim(), entry[(at + Separator.Length)..].Trim());
    }

    /// <summary>写回设置的一行。</summary>
    public string ToSettingValue()
        => SymbolicLink.Length == 0 ? Name : Name + Separator + SymbolicLink;

    /// <summary>
    /// 这条勾选是否就是上报里的那个设备。
    ///
    /// 两边**都**拿得到路径时只认路径——这正是修「两个摄像头同名」的关键：路径不同就是不同设备，
    /// 名字一样也不认。有一边拿不到路径（老配置、或 MF 没给）才退回按名字比，
    /// 宁可回到旧行为，也不能因为认不出路径就整个不提醒。
    /// </summary>
    public bool Matches(string deviceName, string deviceLink)
    {
        if (SymbolicLink.Length > 0 && deviceLink.Length > 0)
            return string.Equals(SymbolicLink, deviceLink, StringComparison.OrdinalIgnoreCase);

        return Name.Length > 0 && string.Equals(Name, deviceName, StringComparison.OrdinalIgnoreCase);
    }
}
