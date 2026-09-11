using ClassIsland.Core.Icons;

namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// CI 矢量图标（Lucide 图标字体）字形转换。
///
/// 原理：ClassIsland 的 <see cref="LucideIconKind"/> 枚举值就是字形码位（如 Sun = 0xE17C），
/// 所以 ((char)(int)kind) 即 CI 的 {ci:LI ...} 内部取到的那一个字符。把该字符交给
/// AppBase.LucideIconsFontFamily 渲染即为矢量图标（需动态选图标时用，写死的图标直接在
/// XAML 用 {ci:LI 图标名} 即可）。
/// </summary>
public static class IconGlyph
{
    /// <summary>枚举 → 图标字形字符。</summary>
    public static string Of(LucideIconKind kind) => ((char)(int)kind).ToString();

    /// <summary>
    /// 图标名（<see cref="LucideIconKind"/> 的成员名，如 "Sun"）→ 图标字形字符。
    /// 名字不认识时返回兜底图标（默认警告三角），保证界面上不会出现空白。
    /// </summary>
    public static string Of(string? name, LucideIconKind fallback = LucideIconKind.TriangleAlert)
        => !string.IsNullOrEmpty(name) && Enum.TryParse<LucideIconKind>(name, out var kind)
            ? Of(kind)
            : Of(fallback);
}
