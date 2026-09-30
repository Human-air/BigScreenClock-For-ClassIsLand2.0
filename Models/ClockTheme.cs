namespace EveningSelfStudyClock.Models;

/// <summary>大屏时钟的主题档位（设置项）。</summary>
public enum ClockThemeMode
{
    /// <summary>跟随 ClassIsland（CI 深色则深色、CI 浅色则浅色）</summary>
    Follow = 0,

    /// <summary>固定明亮（白底黑字）</summary>
    Light = 1,

    /// <summary>固定黑暗</summary>
    Dark = 2,
}

/// <summary>
/// 一套主题的配色（HEX）。前一批是界面「底色 / 文字」类颜色，随主题整体翻转、不作为设置项；
/// 最后 5 项对应设置页里的 5 个颜色选择器，切主题时被写成该主题的默认值，之后仍可手动微调。
/// </summary>
public sealed class ClockThemePalette
{
    /// <summary>这套调色板是浅底（明亮）还是深底（黑暗）。语义色（五档/预警/计数）据此选深浅版本。</summary>
    public required bool IsLight { get; init; }

    /// <summary>提醒主文字 + 图标</summary>
    public required string TextPrimary { get; init; }

    /// <summary>文本块 / 弹幕</summary>
    public required string TextBody { get; init; }

    /// <summary>各副标题（颜文字、日期副标题）</summary>
    public required string TextSecondary { get; init; }

    /// <summary>右上角日期</summary>
    public required string TextMuted { get; init; }

    /// <summary>按钮前景 / 免责声明</summary>
    public required string TextFaint { get; init; }

    /// <summary>倒计时文字</summary>
    public required string Countdown { get; init; }

    /// <summary>音量条分段刻度</summary>
    public required string Divider { get; init; }

    /// <summary>底部两个按钮的前景（配合按钮自身 0.5 的不透明度，要比正文更实一些）</summary>
    public required string ButtonForeground { get; init; }

    /// <summary>按钮底色</summary>
    public required string ButtonBackground { get; init; }

    /// <summary>按钮悬停底色</summary>
    public required string ButtonHover { get; init; }

    /// <summary>记录规则浮层底色</summary>
    public required string PopupBackground { get; init; }

    /// <summary>记录规则浮层边框</summary>
    public required string PopupBorder { get; init; }

    /// <summary>记录规则浮层文字</summary>
    public required string PopupText { get; init; }

    /// <summary>音量条轨道 / 课程进度带底色</summary>
    public required string VolumeTrack { get; init; }

    /// <summary>设置页预览里音量块与进度带的容器底色（只用于预览）</summary>
    public required string PanelBackground { get; init; }

    // ===== 以下 6 项对应设置页里的 6 个颜色选择器 =====

    /// <summary>背景色默认值</summary>
    public required string BackgroundColor { get; init; }

    /// <summary>时钟字体色默认值</summary>
    public required string FontColor { get; init; }

    /// <summary>课程信息色默认值</summary>
    public required string CourseInfoColor { get; init; }

    /// <summary>音量条标题色默认值</summary>
    public required string NoiseTitleColor { get; init; }

    /// <summary>进度条色默认值</summary>
    public required string ProgressColor { get; init; }

    /// <summary>降水提醒色默认值（有降雨提醒、或当前天气是雨雪时用）</summary>
    public required string RainColor { get; init; }
}

/// <summary>
/// 主题解析 + 两套调色板。纯计算、不依赖 Avalonia，便于单测。
/// 色值语义色（音量五档绿→红、预警等级色、计数黄/红）由数据决定，两套主题共用，不在这里。
/// </summary>
public static class ClockTheme
{
    /// <summary>未套用过任何主题（新装配置的初值）。</summary>
    public const int VariantUnknown = -1;

    /// <summary>已套用明亮调色板</summary>
    public const int VariantLight = 0;

    /// <summary>已套用黑暗调色板</summary>
    public const int VariantDark = 1;

    /// <summary>黑暗：与历史版本一致（黑底白字）。</summary>
    public static readonly ClockThemePalette Dark = new()
    {
        IsLight = false,
        TextPrimary = "#FFFFFF",
        TextBody = "#DDFFFFFF",
        TextSecondary = "#AAFFFFFF",
        TextMuted = "#66FFFFFF",
        TextFaint = "#40FFFFFF",
        Countdown = "#FFFFDD",
        Divider = "#66FFFFFF",
        // 按钮文字与图标：按钮自身还带 0.5 不透明度，那是唯一的淡化手段
        // （以前这里再叠一层 #40，黑底上叠完只有 20% 白，字基本看不见）
        ButtonForeground = "#FFFFFF",
        ButtonBackground = "#33FFFFFF",
        ButtonHover = "#66FFFFFF",
        PopupBackground = "#14141F",
        PopupBorder = "#44FFFFFF",
        PopupText = "#DDFFFFFF",
        VolumeTrack = "#B31C3047",
        PanelBackground = "#1A1A2E",
        BackgroundColor = "#000000",
        FontColor = "#FFFFFF",
        CourseInfoColor = "#88CCFF",
        NoiseTitleColor = "#BBFFFFFF",
        ProgressColor = "#4FC3F7",
        RainColor = "#7FD4FF",
    };

    /// <summary>明亮：白底黑字，半透明色一并换成黑色系。</summary>
    public static readonly ClockThemePalette Light = new()
    {
        IsLight = true,
        TextPrimary = "#000000",
        TextBody = "#DD000000",
        TextSecondary = "#99000000",
        TextMuted = "#66000000",
        TextFaint = "#40000000",
        Countdown = "#8A6D00",
        Divider = "#55000000",
        // 按钮文字与图标：同上，按钮自身带 0.5 不透明度，这里用满色
        ButtonForeground = "#000000",
        ButtonBackground = "#1F000000",
        ButtonHover = "#33000000",
        PopupBackground = "#FFFFFF",
        PopupBorder = "#22000000",
        PopupText = "#DD000000",
        VolumeTrack = "#B3D5DEE8",
        PanelBackground = "#E8EDF3",
        BackgroundColor = "#FFFFFF",
        FontColor = "#000000",
        CourseInfoColor = "#1565C0",
        NoiseTitleColor = "#99000000",
        ProgressColor = "#0288D1",
        RainColor = "#0277BD",
    };

    /// <summary>
    /// 解析实际生效的调色板变体：固定明亮/黑暗按设置走，跟随档按 CI 当前是不是深色。
    /// </summary>
    public static int ResolveVariant(ClockThemeMode mode, bool ciIsDark) => mode switch
    {
        ClockThemeMode.Light => VariantLight,
        ClockThemeMode.Dark => VariantDark,
        _ => ciIsDark ? VariantDark : VariantLight,
    };

    /// <summary>变体 → 调色板（未知变体按黑暗，与旧版外观一致）。</summary>
    public static ClockThemePalette PaletteOf(int variant)
        => variant == VariantLight ? Light : Dark;

    /// <summary>设置里的档位 + CI 当前亮暗 → 生效的调色板。</summary>
    public static ClockThemePalette Resolve(ClockThemeMode mode, bool ciIsDark)
        => PaletteOf(ResolveVariant(mode, ciIsDark));
}
