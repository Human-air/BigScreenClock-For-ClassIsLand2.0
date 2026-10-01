namespace EveningSelfStudyClock.Models;

/// <summary>
/// 数据语义色（吵闹计数、预警等级）在两套主题下的取值。
/// 这些颜色本身表达数据（黄=一般、红=吵闹、预警按官方等级着色），语义不随主题变，
/// 只是浅底上原色太亮读不清，故按底色深浅换一版明度。
/// </summary>
public static class SemanticColors
{
    /// <summary>
    /// 「一般」计数与「正在记录：一般」提示的颜色：与音量条「一般」档**达到时**的档位文字同色
    /// （<see cref="EveningSelfStudyClock.ViewModels.LevelSlotCalculator.ReachedColorOf"/>，即填充色第 3 项），
    /// 两套主题同一支亮黄；未达到一般时档位文字是压暗的琥珀，两者不会混淆。
    /// 注意它本身很亮，#FFDE02 当文字放白底上对比度不足，改前先看真机。
    /// </summary>
    public static string CountNormalHex(bool light) => "#FFDE02";

    /// <summary>「吵闹」计数与「正在记录：吵闹」提示的红色。</summary>
    public static string CountNoisyHex(bool light) => light ? "#C62828" : "#FF5555";

    /// <summary>
    /// 地震提醒的等级色：按**本地烈度**分档，分档区间取自地震预警插件的 IntensityToColorConverter
    /// （≤2 蓝 / ≤4 黄 / ≤6 橙 / &gt;6 红）；浅底上换成同色相的压暗版（与预警等级色同一套处理）。
    /// </summary>
    public static string EarthquakeHex(double intensity, bool light) => (intensity, light) switch
    {
        (<= 2, false) => "#55AAFF",
        (<= 2, true) => "#1976D2",
        (<= 4, false) => "#FFDD44",
        (<= 4, true) => "#B58900",
        (<= 6, false) => "#FF9933",
        (<= 6, true) => "#E06C00",
        (_, false) => "#FF5555",
        (_, true) => "#D32F2F",
    };

    /// <summary>预警等级色（蓝/黄/橙/红）；等级未知返回 null（调用方用主题正文色兜底）。</summary>
    public static string? AlertHex(string? level, bool light) => (level, light) switch
    {
        ("蓝色", false) => "#55AAFF",
        ("蓝色", true) => "#1976D2",
        ("黄色", false) => "#FFDD44",
        ("黄色", true) => "#B58900",
        ("橙色", false) => "#FF9933",
        ("橙色", true) => "#E06C00",
        ("红色", false) => "#FF5555",
        ("红色", true) => "#D32F2F",
        _ => null,
    };
}
