namespace EveningSelfStudyClock.ViewModels;

/// <summary>
/// 音量档位 → 索引/颜色的纯计算类（无 Avalonia 依赖，供单元测试源文件编译）。
/// 档位边界与 <see cref="NoiseDetection.NoiseLevelDisplay.ProgressOf"/> 的 20/40/60/80 一一对应：
/// 0-安静 / 1-良好 / 2-一般 / 3-吵闹 / 4-嘈杂。
/// 进度由进度段算出、档位由进度反推，两者必须恒等于检测器的分级（有单元测试锁死）。
/// </summary>
public static class LevelSlotCalculator
{
    /// <summary>由 0–100 进度反推档位索引。</summary>
    public static int SlotFromProgress(double progress) => progress switch
    {
        < 20 => 0,
        < 40 => 1,
        < 60 => 2,
        < 80 => 3,
        _ => 4
    };

    /// <summary>五档色（ARGB，按吵闹程度：绿→黄绿→黄→橙→红），用于轨道填充（实心色块，两套主题共用）。</summary>
    public static readonly uint[] SlotColors =
        { 0xFF4CAF50, 0xFFAED581, 0xFFFFDE02, 0xFFFF9933, 0xFFFF5555 };

    /// <summary>
    /// 「一般」未达到时的档位文字色（两套主题共用）：与达到时的亮黄同色相、压暗一档，
    /// 让达到一般与未达到一般一眼能分开。
    /// </summary>
    public const uint NormalUnreachedColor = 0xFFB58900;

    /// <summary>五档文字色（**未达到**该档时，黑暗主题）：沿用轨道填充色，只有「一般」换成压暗琥珀。</summary>
    public static readonly uint[] SlotTextColorsDark =
        { 0xFF4CAF50, 0xFFAED581, NormalUnreachedColor, 0xFFFF9933, 0xFFFF5555 };

    /// <summary>
    /// 五档文字色（**未达到**该档时，明亮主题）：原色里的黄绿/黄在白底上几乎看不见，同色相压暗一档。
    /// </summary>
    public static readonly uint[] SlotTextColorsLight =
        { 0xFF2E7D32, 0xFF9E9D24, NormalUnreachedColor, 0xFFC2610A, 0xFFC62828 };

    /// <summary>五档文字色（未达到）：按主题取一版。</summary>
    public static uint[] SlotTextColors(bool light) => light ? SlotTextColorsLight : SlotTextColorsDark;

    /// <summary>
    /// 当前档位（**已达到**）的强调色。只有「一般」单独给一支亮黄，让达到一般与未达到一般
    /// 一眼能分开（未达到是压暗的琥珀，达到是亮黄）；其余四档与未达到时同色，
    /// 靠透明度与字号区分就够了。
    /// </summary>
    public static uint ReachedColorOf(int slot, bool light)
        => slot == 2 ? SlotColors[2] : SlotTextColors(light)[slot];
}
