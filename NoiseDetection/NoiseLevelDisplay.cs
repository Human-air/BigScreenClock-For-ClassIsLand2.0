using System;

namespace EveningSelfStudyClock.NoiseDetection;

/// <summary>
/// 音量条的档位显示映射（纯算法，零外部依赖，供单元测试源文件编译）。
///
/// 关键约束：**显示档位必须与 <see cref="NoiseEventDetector"/> 的分级同源**。
/// 检测器的档次区间由「entry 阈值」划出（安静 &lt; 良好阈值 ≤ 良好 &lt; 一般阈值 ≤ 一般 &lt; 吵闹阈值 ≤ 吵闹），
/// 所以音量条的五档也必须落在同一组边界上；否则会出现「条还停在一般、记录却已按良好结算」
/// 这种显示与记录对不上的情况（旧版每档都往后串了一档，正是此毛病）。
/// </summary>
public static class NoiseLevelDisplay
{
    /// <summary>五档名（顺序即显示顺序：安静/良好/一般/吵闹/嘈杂）。</summary>
    public static readonly string[] SlotNames = { "安静", "良好", "一般", "吵闹", "嘈杂" };

    /// <summary>「吵闹」再细分为「嘈杂」的音量倍数（相对吵闹阈值）。</summary>
    public const double VeryNoisyFactor = 1.3;

    /// <summary>
    /// 档位索引 0-4。前四档直接由检测器的分级决定（检测器没有「嘈杂」，它按音量再细分出来）。
    /// </summary>
    public static int SlotOf(NoiseLevel level, double smooth, double noisyThreshold)
        => level switch
        {
            NoiseLevel.Quiet => 0,
            NoiseLevel.Good => 1,
            NoiseLevel.Normal => 2,
            _ => smooth >= noisyThreshold * VeryNoisyFactor ? 4 : 3
        };

    /// <summary>
    /// 检测器分级 + 平滑音量 → 轨道进度 0–100。
    /// 每一档只在**自己那段音量区间**内插值、绝不越到邻档，因此
    /// <c>LevelSlotCalculator.SlotFromProgress(ProgressOf(...)) == SlotOf(...)</c> 恒成立。
    /// </summary>
    /// <remarks>
    /// 各档区间与进度段一一对应（五档各占 20%，与轨道上的分段刻度 20/40/60/80 对齐，
    /// 这样「条走到哪一段、文字就显示哪一档」——旧版每档错后 20% 正是「条越进吵闹却显示一般」的根因）：
    /// 安静 [0, good) → 0-20；良好 [good, normal) → 20-40；一般 [normal, noisy) → 40-60；
    /// 吵闹 [noisy, 1.3×noisy) → 60-80；嘈杂 ≥1.3×noisy → 80-100。
    /// </remarks>
    public static double ProgressOf(NoiseLevel level, double smooth,
        double goodThreshold, double normalThreshold, double noisyThreshold)
    {
        var veryNoisy = noisyThreshold * VeryNoisyFactor;
        return level switch
        {
            NoiseLevel.Quiet => Band(smooth, 0, goodThreshold, 0, 20),
            NoiseLevel.Good => Band(smooth, goodThreshold, normalThreshold, 20, 40),
            NoiseLevel.Normal => Band(smooth, normalThreshold, noisyThreshold, 40, 60),
            _ => smooth >= veryNoisy
                ? Band(smooth, veryNoisy, veryNoisy * 1.6, 80, 100)
                : Band(smooth, noisyThreshold, veryNoisy, 60, 80)
        };
    }

    /// <summary>把 [lo, hi] 区间内的值线性映射到 [outLo, outHi]，超出区间夹在端点（保证进度不越档）。</summary>
    private static double Band(double value, double lo, double hi, double outLo, double outHi)
    {
        if (hi <= lo) return outLo;
        var k = Math.Clamp((value - lo) / (hi - lo), 0, 1);
        return outLo + k * (outHi - outLo);
    }
}
