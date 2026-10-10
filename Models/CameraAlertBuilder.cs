namespace EveningSelfStudyClock.Models;

/// <summary>
/// 摄像头占用 → 预警条目。纯函数（数据源是 <c>CameraActivityService</c>，这里不碰 COM），
/// 产出的条目直接并进天气预警列表，显示方式（面板那一行 + 悬停/点击的详情弹幕）与天气预警完全一致。
/// </summary>
public static class CameraAlertBuilder
{
    /// <summary>提醒标题（面板那一行显示的就是它）。</summary>
    public const string AlertTitle = "摄像头使用中";

    /// <summary>提醒等级：借预警等级配色里的橙色——比天气预警显眼，又不像气象红色预警那样紧张。</summary>
    public const string AlertLevel = "橙色";

    /// <summary>提醒图标（LucideIconKind 成员名）。</summary>
    public const string AlertIcon = "Webcam";

    /// <summary>
    /// 按「设置里勾选的设备」+「当前占用状态」拼提醒，一个被占用的设备一条。
    /// </summary>
    /// <param name="monitoredDevices">设置里勾选的设备（<see cref="MonitoredCamera.ToSettingValue"/> 存的那种行）。空 = 不提醒。</param>
    /// <param name="occupancy">当前占用快照。</param>
    /// <param name="titleFontSize">面板上这一条的字号（设置里可调）。</param>
    /// <remarks>
    /// 认设备见 <see cref="MonitoredCamera.Matches"/>（优先按接口路径，认不出才退回按名字）。
    /// 结果按设备名排序，保证同一状态下每次顺序一致（顺序变了预警列表会反复重建，
    /// 把正在看的详情弹幕打断）；同名设备只出一条，免得同一个名字在面板上叠两行。
    /// </remarks>
    public static List<AlertInfo> Build(IEnumerable<string> monitoredDevices, IReadOnlyList<CameraActivity> occupancy,
                                        double titleFontSize = AlertInfo.DefaultFontSize)
    {
        var result = new List<AlertInfo>();
        var wanted = monitoredDevices
            .Select(MonitoredCamera.Parse)
            .Where(m => m.Name.Length > 0 || m.SymbolicLink.Length > 0)
            .ToList();

        if (wanted.Count == 0 || occupancy.Count == 0) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var activity in occupancy.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!wanted.Any(m => m.Matches(activity.Name, activity.SymbolicLink))) continue;

            var device = activity.Name.Length > 0 ? activity.Name : activity.SymbolicLink;
            if (device.Length == 0 || !seen.Add(device)) continue;

            var who = string.IsNullOrWhiteSpace(activity.Process) ? "某个程序" : activity.Process.Trim();
            result.Add(new AlertInfo
            {
                Title = AlertTitle,
                Detail = $"{who} 正在使用 {device}",
                Level = AlertLevel,
                IconName = AlertIcon,
                FontSize = titleFontSize,
            });
        }

        return result;
    }
}
