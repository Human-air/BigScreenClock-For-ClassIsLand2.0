namespace EveningSelfStudyClock.Models;

/// <summary>
/// 一套主题下的 6 个可调颜色（设置页里那 6 个颜色选择器）。
/// 明亮、黑暗各存一套：切主题时先把当前这套存回它自己的槽位、再读另一套，
/// 所以来回切主题不会把你调过的颜色冲掉；某个主题第一次用到时，槽位里就是该主题的默认色。
/// （存取动作在 <see cref="EveningSelfStudyClock.Helpers.ClockThemeApplier"/> 里，
///   本类保持零依赖、可被单元测试直接编译。）
/// </summary>
public sealed class ThemeColorSet
{
    public string Background { get; set; } = "#000000";
    public string Font { get; set; } = "#FFFFFF";
    public string CourseInfo { get; set; } = "#88CCFF";
    public string NoiseTitle { get; set; } = "#BBFFFFFF";
    public string Progress { get; set; } = "#4FC3F7";
    public string Rain { get; set; } = "#7FD4FF";

    /// <summary>取某个调色板的 6 个颜色默认值。</summary>
    public static ThemeColorSet FromPalette(ClockThemePalette p) => new()
    {
        Background = p.BackgroundColor,
        Font = p.FontColor,
        CourseInfo = p.CourseInfoColor,
        NoiseTitle = p.NoiseTitleColor,
        Progress = p.ProgressColor,
        Rain = p.RainColor,
    };

    /// <summary>6 个色值（用于逐项校验/拷贝）。</summary>
    public IEnumerable<string> Values()
    {
        yield return Background;
        yield return Font;
        yield return CourseInfo;
        yield return NoiseTitle;
        yield return Progress;
        yield return Rain;
    }
}
