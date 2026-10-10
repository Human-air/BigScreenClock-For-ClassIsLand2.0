namespace EveningSelfStudyClock.Models;

/// <summary>
/// 一个摄像头设备。
/// </summary>
/// <param name="Name">设备名（如「ASUS FHD webcam」），设置页里显示的就是它。</param>
/// <param name="SymbolicLink">
/// 设备接口路径。**唯一标识**：同一台机器上两个摄像头很可能报同一个友好名
/// （希沃一体机内置 + 视频展台就是），只按名字认人会把它们当成同一个，勾一个等于勾两个。
/// MF 活动上报里的这一串是全大写、枚举出来的是小写，比较时忽略大小写即可。
/// 换 USB 口会变，所以它只用来「当下认出是哪一个」，不当作长期身份。
/// </param>
public sealed record CameraDevice(string Name, string SymbolicLink);

/// <summary>
/// 摄像头服务报上来的一条占用：哪个设备正被哪个程序用着。
/// 名字与路径都留着——名字给人看，路径用来跟设置里勾的设备对上（见 <see cref="MonitoredCamera.Matches"/>）。
/// </summary>
/// <param name="Name">设备友好名；MF 不肯给名字时为空。</param>
/// <param name="SymbolicLink">设备接口路径（上报里是大写）；拿不到时为空。</param>
/// <param name="Process">正在使用它的程序名。</param>
public sealed record CameraActivity(string Name, string SymbolicLink, string Process);

/// <summary>
/// 设备列表里的显示名。两个摄像头同名时（一体机内置 + 视频展台）光看名字分不出哪行是哪个，
/// 给它们各带一小段设备路径：一目了然知道自己勾的是哪一台。
/// </summary>
public static class CameraDeviceNaming
{
    /// <summary>
    /// 从设备路径里取一小段能区分设备的标识。
    ///
    /// USB 摄像头路径形如 <c>\\?\usb#vid_1bcf&amp;pid_2c99&amp;mi_00#7&amp;2b0f5c9&amp;0&amp;0000#{65e8773d-…}\global</c>，
    /// 用 <c>#</c> 切成 4 段，**唯一的是第 3 段**（实例段）里那串哈希 <c>2b0f5c9</c>，取它。
    /// 不能取 <c>vid_…&amp;pid_…</c>：同型号的两台摄像机这一段一模一样，等于没加标识。
    /// 取不到就返回空串，由 <see cref="DisplayNames"/> 决定怎么办。
    /// </summary>
    public static string ShortId(string symbolicLink)
    {
        var parts = (symbolicLink ?? "").Trim().Split('#');
        if (parts.Length < 3) return "";

        // 实例段形如 7&2b0f5c9&0&0000：第 2 个字段是实例哈希，前面的 7 只是总线号、后面的 0&0000 是填充。
        var bits = parts[2].Split('&', StringSplitOptions.RemoveEmptyEntries);
        var hash = bits.Length >= 2 ? bits[1].Trim() : "";
        return hash.Length is > 0 and <= 12 ? hash : "";
    }

    /// <summary>短标识也分不出来时的兜底：路径末尾几位。难看，但至少两行不会显示成一个样。</summary>
    private static string PathTail(string symbolicLink)
    {
        var link = (symbolicLink ?? "").Trim();
        if (link.Length == 0) return "";
        return link.Length <= 12 ? link : "…" + link[^12..];
    }

    /// <summary>
    /// 列表里每一行显示的名字。同名才加短标识，名字本来就唯一的那些行保持干净。
    /// </summary>
    /// <remarks>
    /// 短标识对不上（同型号又认不出实例段）时用路径兜底：名字一样的两行必须长得不一样，
    /// 否则设置里根本不知道自己勾的是哪一台——这正是当初加这个功能的理由。
    /// </remarks>
    public static List<string> DisplayNames(IReadOnlyList<CameraDevice> devices)
    {
        var duplicated = devices
            .GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var names = new List<string>(devices.Count);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var device in devices)
        {
            if (!duplicated.Contains(device.Name))
            {
                names.Add(device.Name);
                continue;
            }

            var label = $"（{ShortId(device.SymbolicLink)}）";
            if (label.Length <= 2 || !used.Add(device.Name + label))
            {
                label = $"（{PathTail(device.SymbolicLink)}）";
                used.Add(device.Name + label);
            }

            names.Add(device.Name + label);
        }

        return names;
    }
}
