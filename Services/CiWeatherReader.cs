using System.Text.Json;
using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 从 CI 的 Settings.json 缓存读取天气信息（LastWeatherInfo）。
/// CI 主界面天气组件会把天气数据写入 Settings.json 的 LastWeatherInfo（含 current/alerts）。
/// 按用户要求：实时天气不用 CI 的（定位偏差导致不准），只取缓存里的温度/天气码做展示；
/// 天气预警是区级统一发布、与具体点位无关，可靠，可以跟随 CI 的缓存。
/// </summary>
public static class CiWeatherReader
{
    /// <summary>
    /// 中国天气网 weathercn 天气码 → 图标名（LucideIconKind 的成员名，由 VM 转成字形渲染成矢量图标）。
    /// 这里只存图标名不直接存字形，是为了让本类保持零 CI 依赖、可被单元测试直接编译。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> WeatherIcons = new Dictionary<string, string>
    {
        ["00"] = "Sun", ["01"] = "CloudSun", ["02"] = "Cloud", ["03"] = "CloudDrizzle",
        ["04"] = "CloudLightning", ["05"] = "CloudLightning", ["06"] = "CloudSnow", ["07"] = "CloudRain",
        ["08"] = "CloudRain", ["09"] = "CloudRain", ["10"] = "CloudRain", ["11"] = "CloudRain",
        ["12"] = "CloudRain", ["13"] = "Snowflake", ["14"] = "CloudSnow", ["15"] = "Snowflake",
        ["16"] = "Snowflake", ["17"] = "CloudRain", ["18"] = "CloudRain", ["19"] = "CloudFog",
        ["20"] = "CloudRain", ["21"] = "CloudRain", ["22"] = "CloudFog", ["23"] = "CloudFog",
        ["24"] = "Wind", ["25"] = "CloudSnow", ["26"] = "Snowflake", ["27"] = "Droplets",
        ["28"] = "Droplets", ["29"] = "Droplets", ["30"] = "Flame", ["31"] = "Flame",
        ["32"] = "Wind", ["33"] = "Wind", ["34"] = "CloudSun", ["53"] = "Hourglass",
    };

    /// <summary>中国天气网 weathercn 天气码 → 中文天气描述（CI 的 GetWeatherTextByCode 对不同码体系会返回「未知」）。</summary>
    public static readonly IReadOnlyDictionary<string, string> WeatherTextCn = new Dictionary<string, string>
    {
        ["00"] = "晴", ["01"] = "多云", ["02"] = "阴", ["03"] = "阵雨",
        ["04"] = "雷阵雨", ["05"] = "雷阵雨伴有冰雹", ["06"] = "雨夹雪",
        ["07"] = "小雨", ["08"] = "中雨", ["09"] = "大雨", ["10"] = "暴雨",
        ["11"] = "大暴雨", ["12"] = "特大暴雨", ["13"] = "阵雪", ["14"] = "小雪",
        ["15"] = "中雪", ["16"] = "大雪", ["17"] = "暴雪", ["18"] = "雾",
        ["19"] = "冻雨", ["20"] = "沙尘暴", ["21"] = "小到中雨", ["22"] = "中到大雨",
        ["23"] = "大到暴雨", ["24"] = "暴雨到大暴雨", ["25"] = "大暴雨到特大暴雨",
        ["26"] = "小雪到中雪", ["27"] = "中雪到大雪", ["28"] = "大雪到暴雪",
        ["29"] = "浮尘", ["30"] = "扬沙", ["31"] = "强沙尘暴", ["32"] = "霾",
        ["33"] = "晴间多云", ["34"] = "多云间阴",
    };

    /// <summary>weathercn 天气码 → 中文描述；未收录返回 null。码自动补前导零（"2" → "02"）。</summary>
    public static string? GetWeatherDescription(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var padded = code.PadLeft(2, '0');
        return WeatherTextCn.TryGetValue(padded, out var text) ? text : null;
    }

    /// <summary>
    /// 从 CI Settings.json 读取天气缓存，返回 (天气码, 温度文本, 预警列表)。
    /// 文件缺失/字段缺失时返回空值，不抛异常。
    /// </summary>
    /// <remarks>
    /// CI 写入的 LastWeatherInfo 键名是小写（current.weather / temperature / alerts[].title 等），
    /// 为兼容大小写不一致，统一用大小写不敏感查找；天气码补前导零（"2" → "02"）匹配 weathercn 两位码。
    /// </remarks>
    public static (string? WeatherCode, string? Temperature, List<AlertInfo> Alerts) ReadLastWeather(string settingsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (!TryGetProp(root, "LastWeatherInfo", out var lw)) return (null, null, new());
            if (!TryGetProp(lw, "current", out var current)) return (null, null, new());

            var code = TryGetProp(current, "weather", out var w) ? w.GetString() : null;
            if (!string.IsNullOrEmpty(code)) code = code.PadLeft(2, '0'); // "2" → "02"

            var temp = TryGetProp(current, "temperature", out var t)
                && TryGetProp(t, "value", out var tv)
                ? tv.GetString()
                : null;

            var alerts = new List<AlertInfo>();
            if (TryGetProp(lw, "alerts", out var alertsEl) && alertsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in alertsEl.EnumerateArray())
                {
                    var title = GetStr(a, "title");
                    if (string.IsNullOrWhiteSpace(title)) continue;
                    alerts.Add(new AlertInfo
                    {
                        Title = title,
                        Level = GetStr(a, "level"),
                        Type = GetStr(a, "type"),
                        Detail = GetStr(a, "detail"),
                    });
                }
            }
            return (code, temp, alerts);
        }
        catch
        {
            return (null, null, new());
        }
    }

    /// <summary>weathercn 天气码中「降水」类码（雨/雪/冻雨，雾/霾/沙尘不算）。用于未来降雨提醒。</summary>
    public static readonly IReadOnlySet<string> RainCodes = new HashSet<string>
    {
        "03", "04", "05", "06", "07", "08", "09", "10", "11", "12",
        "13", "14", "15", "16", "17", "19",
        "21", "22", "23", "24", "25", "26", "27", "28",
    };

    /// <summary>
    /// 从 CI Settings.json 缓存读取「未来降雨提醒」：返回主标题文本（如「20小时内当前地区有降雨」），
    /// 未来无降雨返回 null。副标题「记得带伞哦」由界面固定显示。
    /// </summary>
    /// <remarks>
    /// CI 的 LastWeatherInfo 里两段数据：
    /// ① minutely.precipitation.value —— 未来 2 小时逐分钟降水（0=无 1=有），最精确，优先；
    /// ② forecastHourly.weather.value —— 未来逐小时 weathercn 天气码，覆盖更长窗口，次选。
    /// 文件缺失/字段缺失返回 null，不抛异常。
    /// </remarks>
    public static string? ReadRainReminder(string settingsPath)
    {
        var (minutely, hourly) = ReadRainArrays(settingsPath);
        return ComputeRainReminder(minutely, hourly);
    }

    /// <summary>到最近降雨的小时数（0=正在下、1=未来1小时内…）；未来无雨返回 null。用于「带伞提醒」是否在 6 小时内。</summary>
    public static int? ReadRainHours(string settingsPath)
    {
        var (minutely, hourly) = ReadRainArrays(settingsPath);
        return ComputeRainHours(minutely, hourly);
    }

    /// <summary>读取 CI Settings.json 中的分钟级/小时级降水数据（读取失败返回 (null, null)）。</summary>
    private static (int[]? Minutely, int[]? Hourly) ReadRainArrays(string settingsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
            var root = doc.RootElement;
            if (!TryGetProp(root, "LastWeatherInfo", out var lw)) return (null, null);

            int[]? minutely = null;
            if (TryGetProp(lw, "minutely", out var minEl)
                && TryGetProp(minEl, "precipitation", out var precEl)
                && TryGetProp(precEl, "value", out var mv) && mv.ValueKind == JsonValueKind.Array)
                minutely = ReadIntArray(mv);

            int[]? hourly = null;
            if (TryGetProp(lw, "forecastHourly", out var fhEl)
                && TryGetProp(fhEl, "weather", out var whEl)
                && TryGetProp(whEl, "value", out var hv) && hv.ValueKind == JsonValueKind.Array)
                hourly = ReadIntArray(hv);

            return (minutely, hourly);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>纯计算：由分钟级降水数组 + 逐小时天气码数组推算降雨提醒主标题；无雨返回 null。</summary>
    /// <remarks>
    /// ① 分钟级：找第一个「有降水」的位置 i（分钟），折算「X小时内」（i=0 表示正在下）；
    /// ② 小时级：找第一个降水类天气码的位置 h（小时），h=0 表示当前小时在降雨。
    /// </remarks>
    public static string? ComputeRainReminder(IReadOnlyList<int>? minutely, IReadOnlyList<int>? hourlyWeather)
    {
        if (minutely is { Count: > 0 })
        {
            for (var i = 0; i < minutely.Count; i++)
            {
                if (minutely[i] <= 0) continue;
                return i == 0 ? "当前正在降雨" : $"当前地区未来{i / 60 + 1}小时内有降雨";
            }
        }

        if (hourlyWeather is { Count: > 0 })
        {
            for (var h = 0; h < hourlyWeather.Count; h++)
            {
                if (!RainCodes.Contains(hourlyWeather[h].ToString("D2"))) continue;
                return h == 0 ? "当前正在降雨" : $"当前地区未来{h}小时内有降雨";
            }
        }

        return null;
    }

    /// <summary>纯计算：到最近降雨的小时数（0=正在下、1=未来1小时内…）；无雨返回 null。</summary>
    /// <remarks>
    /// ① 分钟级：第一个有降水位置 i（分钟）→ i/60 小时（分钟级数据最多覆盖 2 小时，命中必然 ≤2 小时）；
    /// ② 小时级：第一个降水天气码位置 h（小时）。
    /// </remarks>
    public static int? ComputeRainHours(IReadOnlyList<int>? minutely, IReadOnlyList<int>? hourlyWeather)
    {
        if (minutely is { Count: > 0 })
        {
            for (var i = 0; i < minutely.Count; i++)
                if (minutely[i] > 0) return i / 60;
        }

        if (hourlyWeather is { Count: > 0 })
        {
            for (var h = 0; h < hourlyWeather.Count; h++)
                if (RainCodes.Contains(hourlyWeather[h].ToString("D2"))) return h;
        }

        return null;
    }

    private static int[]? ReadIntArray(JsonElement el)
    {
        var list = new List<int>();
        foreach (var v in el.EnumerateArray())
            if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                list.Add(n);
        return list.ToArray();
    }

    private static string? GetStr(JsonElement el, string name)
        => TryGetProp(el, name, out var p) ? p.GetString() : null;

    /// <summary>大小写不敏感地查找 JSON 属性（CI 写入的键名大小写不固定）。</summary>
    private static bool TryGetProp(JsonElement el, string name, out JsonElement value)
    {
        if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out value)) return true;
        if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in el.EnumerateObject())
            {
                if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = p.Value;
                    return true;
                }
            }
        }
        value = default;
        return false;
    }
}
