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

    /// <summary>五档色（ARGB，按吵闹程度：绿→黄绿→黄→橙→红），与 XAML 档位文字/轨道填充共用。</summary>
    public static readonly uint[] SlotColors =
        { 0xFF4CAF50, 0xFFAED581, 0xFFFFDD44, 0xFFFF9933, 0xFFFF5555 };
}
