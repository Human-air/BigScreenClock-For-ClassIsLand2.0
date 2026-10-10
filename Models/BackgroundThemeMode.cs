namespace EveningSelfStudyClock.Models;

/// <summary>
/// 开了背景图之后用的主题档位。**和 <see cref="ClockThemeMode"/> 是两套独立设置**：
/// 没有背景图时，「明亮 / 黑暗」指的是整屏的底色（白底黑字 / 黑底白字）；
/// 有了背景图，底色就是那张图了，能调的只剩垫在组件后面那块玻璃和字的颜色 ——
/// 此时再叫「黑暗」，在一张亮壁纸上反倒会调出一屏白字，名字和看到的东西对不上。
///
/// 所以这一套一律按**看到的样子**命名：暗底白字 / 亮底黑字。
/// </summary>
public enum BackgroundThemeMode
{
    /// <summary>跟着背景图走：图偏亮用暗底白字、图偏暗用亮底黑字（没图时退化成跟随 ClassIsland）。</summary>
    Auto = 0,

    /// <summary>暗底白字（垫深色玻璃，字用白色）。</summary>
    Dark = 1,

    /// <summary>亮底黑字（垫浅色玻璃，字用黑色）。</summary>
    Light = 2,
}

/// <summary>背景主题档位 → 真正描述配色的那套档位。</summary>
public static class BackgroundThemeModes
{
    /// <summary>映射到 <see cref="ClockThemeMode"/>；两套的整数值不一样，别直接转换。</summary>
    public static ClockThemeMode ToClockThemeMode(this BackgroundThemeMode mode) => mode switch
    {
        BackgroundThemeMode.Dark => ClockThemeMode.Dark,
        BackgroundThemeMode.Light => ClockThemeMode.Light,
        _ => ClockThemeMode.Auto,
    };
}
