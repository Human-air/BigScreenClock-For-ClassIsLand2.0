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
    /// <param name="monitoredDevices">设置里勾选的设备名。空 = 不提醒。</param>
    /// <param name="occupancy">设备名 → 正在使用它的程序名。</param>
    /// <remarks>
    /// 设备名比较忽略大小写与首尾空白；结果按设备名排序，保证同一状态下每次顺序一致
    /// （顺序变了预警列表会反复重建，把正在看的详情弹幕打断）。
    /// </remarks>
    public static List<AlertInfo> Build(IEnumerable<string> monitoredDevices, IReadOnlyDictionary<string, string> occupancy)
    {
        var result = new List<AlertInfo>();
        var wanted = new HashSet<string>(
            monitoredDevices.Select(n => n.Trim()).Where(n => n.Length > 0),
            StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0 || occupancy.Count == 0) return result;

        foreach (var (device, process) in occupancy.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!wanted.Contains(device)) continue;

            var who = string.IsNullOrWhiteSpace(process) ? "某个程序" : process.Trim();
            result.Add(new AlertInfo
            {
                Title = AlertTitle,
                Detail = $"{who} 正在使用 {device}",
                Level = AlertLevel,
                IconName = AlertIcon,
            });
        }

        return result;
    }
}
