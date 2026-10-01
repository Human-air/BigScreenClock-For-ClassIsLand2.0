using System.Collections;
using System.Reflection;
using ClassIsland.Core.Abstractions.Services;
using EveningSelfStudyClock.Models;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 从「地震预警」插件（denglihong.EarthquakeWarning）的实时状态里读地震数据。
///
/// 该插件是个 CI 提醒提供方（NotificationProvider），实时状态都存在它的设置对象
/// <c>EarthquakeNotificationSettings</c> 里（震中、震级、震源深度、发震时刻、本地经纬度、预警门槛），
/// 插件的轮询线程每秒往里写。插件实例由 CI 的提醒主机服务持有，这里顺着
/// INotificationHostService.NotificationProviders[].ProviderInstance 反射拿到它。
///
/// 全部走反射、不引用对方的程序集：对方没装 / 没启用 / 改了内部结构时，这里读不到就安静地不显示，
/// 不影响大屏时钟其它功能。
/// </summary>
/// <summary>一次读取的结果，见 <see cref="EarthquakeReader.Read"/>。</summary>
/// <param name="Alert">这场地震的提醒；够不上提醒门槛（本地烈度不超阈值、横波已到）时为 null。</param>
/// <param name="EventKey">对方当前盯的那场地震的标识；读不到对方状态时为 null。</param>
public readonly record struct EarthquakeReading(EarthquakeAlert? Alert, string? EventKey);

public sealed class EarthquakeReader
{
    /// <summary>地震预警插件的提醒提供方 GUID（其 EarthquakeNotificationProvider 上的 NotificationProviderInfo）。</summary>
    private const string ProviderGuid = "B27C0AF3-C917-44DE-A61D-8010C3F3FB92";

    /// <summary>按类型名兜底找（万一以后换了 GUID）。</summary>
    private const string ProviderTypeName = "EarthquakeNotificationProvider";

    /// <summary>对方算距离/烈度/横波到达秒数的计算器（public static，与其设置类型同程序集）。</summary>
    private const string CalculatorTypeName = "EarthquakeWarning.Calculators.HuaniaEarthQuakeCalculator";

    private readonly IServiceProvider _serviceProvider;

    public EarthquakeReader(IServiceProvider serviceProvider) => _serviceProvider = serviceProvider;

    /// <summary>
    /// 「地震预警」插件装了没（能不能在 CI 的提醒主机里找到它的提醒提供方）。
    /// 设置页拿它决定地震提醒那一项是否可用。
    /// </summary>
    public bool IsAvailable()
    {
        try
        {
            return FindProviderSettings() is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 读一次：<see cref="EarthquakeReading.Alert"/> 是这场地震的提醒（够不上提醒门槛时为 null），
    /// <see cref="EarthquakeReading.EventKey"/> 是对方「现在盯的是哪一场」的标识。
    /// 调用方拿后者分辨「换了一场地震」和「这一拍没读到」（见 ViewModel 的地震提醒）。
    /// </summary>
    public EarthquakeReading Read(DateTime now)
    {
        try
        {
            var settings = FindProviderSettings();
            if (settings is null) return default;

            var info = GetProp(settings, "EarthquakeInfo");
            if (info is null) return default;

            var eventKey = EventKeyOf(info);
            var placeName = GetProp(info, "PlaceName") as string;
            var magnitude = GetDouble(info, "Magnitude") ?? 0;
            var shockTime = GetProp(info, "ShockTime") as string;
            var depth = GetDouble(info, "Depth");
            var quakeLat = GetDouble(info, "Latitude");
            var quakeLon = GetDouble(info, "Longitude");

            var myLat = GetDouble(settings, "Latitude") ?? 0;
            var myLon = GetDouble(settings, "Longitude") ?? 0;
            var threshold = GetDouble(settings, "Threshold") ?? 0;

            // 本地坐标没配（默认 0）时算出来的距离/烈度没有意义，直接不显示
            if (Math.Abs(myLat) < 0.01 && Math.Abs(myLon) < 0.01) return new EarthquakeReading(null, eventKey);
            if (quakeLat is null || quakeLon is null) return new EarthquakeReading(null, eventKey);

            var calculator = settings.GetType().Assembly.GetType(CalculatorTypeName);
            var distance = Calc(calculator, "GetDistance", myLat, myLon, quakeLat.Value, quakeLon.Value)
                           ?? HaversineKm(myLat, myLon, quakeLat.Value, quakeLon.Value);
            var intensity = Calc(calculator, "GetIntensity", magnitude, distance)
                            ?? FallbackIntensity(magnitude, distance);
            var countdown = Calc(calculator, "GetCountDownSeconds", depth ?? 10.0, distance)
                            ?? FallbackCountdownSeconds(depth ?? 10.0, distance);

            var alert = EarthquakeAlertBuilder.Build(placeName, magnitude, shockTime, intensity, threshold,
                countdown, now);
            return new EarthquakeReading(alert, eventKey);
        }
        catch
        {
            // 对方改了结构/程序集还没加载完等，一律当「没有地震」处理，不要去打扰大屏
            return default;
        }
    }

    /// <summary>
    /// 这场地震的标识：优先用对方的事件编号（E2Id / EventId），没有就退回「震中 + 发震时刻」。
    /// 同一场地震这几样都不会变，换了一场就会变——正是调用方要的。
    /// </summary>
    private static string? EventKeyOf(object info)
    {
        foreach (var name in new[] { "E2Id", "EventId" })
        {
            var value = GetProp(info, name)?.ToString();
            if (!string.IsNullOrWhiteSpace(value)) return value;
        }

        var place = GetProp(info, "PlaceName") as string;
        if (string.IsNullOrWhiteSpace(place)) return null;
        return $"{place}|{GetProp(info, "ShockTime")}";
    }

    /// <summary>从 CI 提醒主机服务里找到地震预警插件的设置对象。</summary>
    private object? FindProviderSettings()
    {
        var host = _serviceProvider.GetService<INotificationHostService>();
        if (host is null) return null;

        // NotificationProviders 是 internal 的，只能反射读
        var providersProp = host.GetType().GetProperty("NotificationProviders",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (providersProp?.GetValue(host) is not IEnumerable providers) return null;

        foreach (var registerInfo in providers)
        {
            var provider = GetProp(registerInfo, "ProviderInstance");
            if (provider is null) continue;

            var guid = GetProp(provider, "ProviderGuid");
            var isTarget = guid?.ToString()?.Equals(ProviderGuid, StringComparison.OrdinalIgnoreCase) == true
                           || provider.GetType().Name == ProviderTypeName;
            if (!isTarget) continue;

            // 基类 NotificationProviderBase<TSettings> 上的 Settings；拿不到就退内部字段
            return GetProp(provider, "Settings") ?? GetProp(provider, "SettingsInternal", isPublic: false);
        }

        return null;
    }

    /// <summary>调用对方计算器上的静态方法；找不到就返回 null（由调用方走兜底算法）。</summary>
    private static double? Calc(Type? calculator, string methodName, params double[] args)
    {
        var method = calculator?.GetMethod(methodName, BindingFlags.Public | BindingFlags.Static);
        if (method is null || method.GetParameters().Length != args.Length) return null;
        try
        {
            var result = method.Invoke(null, args.Cast<object>().ToArray());
            return result is null ? null : Convert.ToDouble(result);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>读属性值。默认只找公开属性；<paramref name="isPublic"/> 为 false 时也允许 internal。</summary>
    private static object? GetProp(object? target, string name, bool isPublic = true)
    {
        if (target is null) return null;
        var flags = BindingFlags.Instance | BindingFlags.Public
                    | (isPublic ? BindingFlags.Default : BindingFlags.NonPublic);
        return target.GetType().GetProperty(name, flags)?.GetValue(target);
    }

    private static double? GetDouble(object? target, string name)
    {
        var value = GetProp(target, name);
        return value is null ? null : Convert.ToDouble(value);
    }

    // ===== 兜底算法：万一对方计算器改名/取不到（正常情况下用不到）=====

    /// <summary>两点间大圆距离（km），与对方 GetDistance 同公式。</summary>
    private static double HaversineKm(double lat1, double lon1, double lat2, double lon2)
    {
        const double earthRadius = 6378.137;
        var radLat1 = Math.PI / 180 * lat1;
        var radLng1 = Math.PI / 180 * lon1;
        var radLat2 = Math.PI / 180 * lat2;
        var radLng2 = Math.PI / 180 * lon2;
        var a = radLat1 - radLat2;
        var b = radLng1 - radLng2;
        return 2 * Math.Asin(Math.Sqrt(Math.Pow(Math.Sin(a / 2), 2) +
                                       Math.Cos(radLat1) * Math.Cos(radLat2) * Math.Pow(Math.Sin(b / 2), 2)))
               * earthRadius;
    }

    /// <summary>本地烈度经验公式（与对方 GetIntensity 同公式）。</summary>
    private static double FallbackIntensity(double magnitude, double distance)
    {
        var value = magnitude * 1.363 + 2.941 - Math.Log(distance + 7.0) * 1.494;
        return value < 0 ? 0 : value;
    }

    /// <summary>横波到达秒数兜底：按横波速度 3.5 km/s 估，深度算进震源距离。</summary>
    private static double FallbackCountdownSeconds(double depth, double distance)
        => Math.Sqrt(distance * distance + depth * depth) / 3.5;
}
