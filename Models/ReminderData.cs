namespace EveningSelfStudyClock.Models;

/// <summary>左上角提醒面板的显示数据（由 CI 组件配置 + CI 天气缓存组装）。</summary>
public class ReminderData
{
    /// <summary>倒计时标题（CI 倒计时组件的 CountDownName，如「高考」）。</summary>
    public string? CountdownTitle { get; set; }

    /// <summary>倒计时剩余文本（如「还有 30 天」）。</summary>
    public string? CountdownRemaining { get; set; }

    /// <summary>文本框内容（CI 文本组件的 TextContent）。</summary>
    public string? TextContent { get; set; }

    /// <summary>天气 emoji 图标。</summary>
    public string? WeatherIcon { get; set; }

    /// <summary>天气摘要文本（如「26° 小雨」）。</summary>
    public string? WeatherText { get; set; }

    /// <summary>天气预警列表。</summary>
    public List<AlertInfo> Alerts { get; } = new();

    public bool HasCountdown => !string.IsNullOrWhiteSpace(CountdownTitle) || !string.IsNullOrWhiteSpace(CountdownRemaining);
    public bool HasText => !string.IsNullOrWhiteSpace(TextContent);
    public bool HasWeather => !string.IsNullOrWhiteSpace(WeatherText);
    public bool HasAlerts => Alerts.Count > 0;
}

/// <summary>单条天气预警。</summary>
public class AlertInfo
{
    /// <summary>预警标题（如「暴雨黄色预警」）。</summary>
    public required string Title { get; init; }

    /// <summary>预警等级（如「黄色」）。</summary>
    public string? Level { get; init; }

    /// <summary>预警类型（如「暴雨」）。</summary>
    public string? Type { get; init; }

    /// <summary>预警详情全文（如「…省自然资源厅、省气象局联合发布…应加强对…防范…」）。</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// 条目图标（LucideIconKind 成员名）。不填 = 预警三角（天气预警用）；
    /// 摄像头占用那条填自己的图标（见 <see cref="CameraAlertBuilder.AlertIcon"/>）。
    /// </summary>
    public string? IconName { get; init; }
}
