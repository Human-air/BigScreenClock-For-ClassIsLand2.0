using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 把主题配色落到设置上（需要 PluginSettings，故与纯算法的 <see cref="ClockTheme"/> 分开放）。
/// </summary>
public static class ClockThemeApplier
{
    /// <summary>
    /// 按 ThemeMode + CI 当前亮暗解析出目标调色板，把设置里的 6 个颜色换成该主题那一套。
    /// 只在生效变体与上次套用的不同时才动：先把当前这套颜色存回它自己的主题槽位（用户调过的色不丢），
    /// 再读出新主题槽位里那套。某个主题第一次用到时槽位里就是该主题的默认色。
    /// </summary>
    /// <returns>true 表示本次换过颜色设置</returns>
    public static bool Apply(PluginSettings settings, bool ciIsDark)
    {
        var variant = ClockTheme.ResolveVariant((ClockThemeMode)settings.ThemeMode, ciIsDark);
        if (settings.AppliedPaletteVariant == variant) return false;

        Capture(settings, settings.AppliedPaletteVariant);   // 旧主题那套先存回自己的槽位
        Restore(settings, variant);                          // 再读出新主题那套
        settings.AppliedPaletteVariant = variant;
        return true;
    }

    /// <summary>
    /// 老配置（没有主题功能那版）升级上来时的迁移：它只有一套颜色，就是用户当时在用的那套
    /// （旧版默认就是黑底白字，用户改也是在这套上改）。
    /// 若直接跟随 CI，本机 CI 是浅色时会把外观整个换成明亮那套——用户会以为自己的外观设置没了。
    /// 故：把这套颜色收进黑暗槽、主题项置于「黑暗」，升级后外观与升级前完全一致；
    /// 想换主题在设置里自己选（主题项就在外观区第一行）。
    /// 只在「还没进过主题系统」的配置上跑一次，之后不再触发。
    /// </summary>
    public static void MigrateLegacySettings(PluginSettings settings)
    {
        if (settings.AppliedPaletteVariant != ClockTheme.VariantUnknown) return;

        Capture(settings, ClockTheme.VariantDark);
        settings.ThemeMode = (int)ClockThemeMode.Dark;
        settings.AppliedPaletteVariant = ClockTheme.VariantDark;
    }

    /// <summary>变体 → 它的颜色槽位（未知变体按黑暗：老配置里的颜色本来就是黑暗那套）。</summary>
    public static ThemeColorSet SetOf(PluginSettings settings, int variant)
        => variant == ClockTheme.VariantLight ? settings.LightColors : settings.DarkColors;

    /// <summary>把当前生效的 6 个颜色存进该变体的槽位。</summary>
    public static void Capture(PluginSettings settings, int variant)
    {
        var set = SetOf(settings, variant);
        set.Background = settings.BackgroundColor;
        set.Font = settings.FontColor;
        set.CourseInfo = settings.CourseInfoColor;
        set.NoiseTitle = settings.NoiseTitleColor;
        set.Progress = settings.ProgressColor;
        set.Rain = settings.RainColor;
    }

    /// <summary>把该变体槽位里的颜色读成当前生效颜色。</summary>
    public static void Restore(PluginSettings settings, int variant)
    {
        var set = SetOf(settings, variant);
        settings.BackgroundColor = set.Background;
        settings.FontColor = set.Font;
        settings.CourseInfoColor = set.CourseInfo;
        settings.NoiseTitleColor = set.NoiseTitle;
        settings.ProgressColor = set.Progress;
        settings.RainColor = set.Rain;
    }
}
