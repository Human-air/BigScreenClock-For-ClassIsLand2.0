using System.Globalization;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 一条已经算好的地震提醒（两行文字 + 等级色所需的本地烈度）。
/// 数据来自地震预警插件（denglihong.EarthquakeWarning）的实时状态，见 <see cref="Services.EarthquakeReader"/>。
/// </summary>
/// <param name="Line1">第一行：横波还有 X 秒到达</param>
/// <param name="Line2">第二行：震中 + 震级</param>
/// <param name="Intensity">本地烈度（决定等级色，分档见 <see cref="SemanticColors.EarthquakeHex"/>）</param>
/// <param name="SecondsLeft">距横波到达还有几秒</param>
public record EarthquakeAlert(string Line1, string Line2, double Intensity, int SecondsLeft)
{
    /// <summary>第一行文案。组装与逐秒递减共用，格式只此一处。</summary>
    public static string CountdownLine(int secondsLeft) => $"横波还有 {secondsLeft} 秒到达";

    /// <summary>倒计时数字变了、其余（震中/震级/等级色）不动。</summary>
    public EarthquakeAlert WithSecondsLeft(int secondsLeft)
        => this with { Line1 = CountdownLine(secondsLeft), SecondsLeft = secondsLeft };
}

/// <summary>
/// 地震提醒的组装规则（纯函数，便于单测）。显示与否跟地震预警插件自己保持一致：
/// 同一套门槛、同样在横波到达后就不再显示。
/// </summary>
public static class EarthquakeAlertBuilder
{
    /// <summary>地震预警插件支持的震时字符串格式（见其 EarthquakeTime.Parse）。</summary>
    private static readonly string[] ShockTimeFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm:ss.fff",
        "yyyy-MM-dd HH时mm分ss秒",
        "yyyy-MM-dd HH时mm时ss",
    };

    /// <summary>
    /// 组装提醒；不该显示时返回 null。
    /// </summary>
    /// <param name="placeName">震中（如「云南昆明市盘龙区」）</param>
    /// <param name="magnitude">震级</param>
    /// <param name="shockTime">发震时刻（地震插件那套格式）</param>
    /// <param name="intensity">本地烈度</param>
    /// <param name="threshold">预警门槛（地震插件里的设置项，只有烈度高于它才预警）</param>
    /// <param name="countdownSeconds">该震中到本地的横波到达秒数（由地震插件自己的算法给出）</param>
    /// <param name="now">当前时间</param>
    public static EarthquakeAlert? Build(string? placeName, double magnitude, string? shockTime,
                                         double intensity, double threshold, double countdownSeconds,
                                         DateTime now)
    {
        if (string.IsNullOrWhiteSpace(placeName)) return null;
        if (!TryParseShockTime(shockTime, out var shock)) return null;
        if (intensity <= threshold) return null;   // 跟地震预警插件同一个门槛

        // 到达时刻 - 现在 = 剩余秒数；取整方式与 CI 的 TimeSpanToTotalSecondsConverter 一致
        // （不足 0.5 秒显示 0），免得跟旁边地震插件自己的提示对不上号。
        var secondsLeft = (shock.AddSeconds(countdownSeconds) - now).TotalSeconds;
        if (secondsLeft < 0.5) return null;        // 横波已到达：插件此时也收了

        var shown = (int)Math.Round(secondsLeft);
        return new EarthquakeAlert(
            EarthquakeAlert.CountdownLine(shown),
            $"{placeName} 发生 {magnitude:0.#} 级地震",
            intensity,
            shown);
    }

    public static bool TryParseShockTime(string? value, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        return DateTime.TryParseExact(value, ShockTimeFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal, out result);
    }
}
