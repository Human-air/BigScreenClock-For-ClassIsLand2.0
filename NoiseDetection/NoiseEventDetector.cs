using System;

namespace EveningSelfStudyClock.NoiseDetection;

/// <summary>
/// 平滑后的噪音等级。Noisy 同时覆盖显示上的「吵闹」和「嘈杂」。
/// </summary>
public enum NoiseLevel
{
    Quiet = 0,
    Good = 1,
    Normal = 2,
    Noisy = 3
}

/// <summary>
/// 单块（约 100ms）检测结果。
/// </summary>
public sealed class SampleResult
{
    public NoiseLevel Level;
    public bool LevelChanged;
    public double SmoothedRms;
    public bool EventFired;
    public NoiseLevel? EventLevel;

    /// <summary>
    /// 当前段的实时归属（仅已起算且段未结束时非空），供 UI 显示「正在记录：一般/吵闹」。
    /// </summary>
    public NoiseLevel? SegmentLevel;
}

/// <summary>
/// V2.1 噪音事件检测器（纯算法，无任何外部依赖，便于单元测试）。
/// 职责：EMA 平滑 + 迟滞分级 + 「段」状态机。
/// 输入：每块原始 RMS + 真实块时长；输出：显示等级 + 段结束事件 + 实时段归属。
///
/// 「一段算一次、短回落合并」规则（取代旧"连续持续 + 冷却"方案）：
/// - 进入可计等级（一般/吵闹）后开段，段内累计「有效时长」= 真正处于一般/吵闹的时间，
///   一般↔吵闹来回波动只影响实时归属，不重置累计；
/// - 回落到不可计等级（安静/良好）开始回落计时，回落 ≤ FallWindowSeconds 不断段、有效时长
///   不清零（说话之间的短暂停顿并进同一段）；回落 > FallWindowSeconds 段才结束；
/// - 段结束结算一次：有效时长 ≥ SustainSeconds（起算底线）→ 按段内占比归属产事件
///   （吵闹占比 ≥ NoisyRatioThreshold 记吵闹，否则记一般）；有效时长不足的短段（喷嚏/咳嗽）丢弃；
/// - 每段至多一次事件（在段结束时），不会中途改记；
/// - SegmentLevel 实时反映当前段起算后的临时归属（供「正在记录」提示，随占比变化切换）。
/// </summary>
public sealed class NoiseEventDetector
{
    public double QuietThreshold { get; set; } = 0.005;
    public double GoodThreshold { get; set; } = 0.015;
    public double NormalThreshold { get; set; } = 0.04;
    public double NoisyThreshold { get; set; } = 0.08;
    public double SmoothAlpha { get; set; } = 0.2;
    public double HysteresisFactor { get; set; } = 0.8;

    /// <summary>
    /// 起算底线（秒）：段内「有效时长」（处于一般/吵闹的累计时间）达到该值，
    /// 段结束才结算一次；不足的短段（喷嚏、咳嗽）丢弃。默认 1 秒。
    /// </summary>
    public double SustainSeconds { get; set; } = 1.0;

    /// <summary>
    /// 回落窗口（秒）：掉到不可计等级后的容错时间。回落 ≤ 该值不断段、与前面并成一段；
    /// 超过该值段才结束、结算一次。默认 5 秒（可调 0~15）。
    /// </summary>
    public double FallWindowSeconds { get; set; } = 5.0;

    /// <summary>
    /// 段内「吵闹」有效时长占比达到该比例，本段即归属「吵闹」；否则归属「一般」。默认 0.5。
    /// </summary>
    public double NoisyRatioThreshold { get; set; } = 0.5;

    private double _smooth;
    private bool _initialized;
    private NoiseLevel _level = NoiseLevel.Quiet;
    private double _time;

    // 段状态
    private bool _segmentActive;           // 当前是否有段
    private double _segmentNormalSeconds;  // 段内「一般」有效时长（秒）
    private double _segmentNoisySeconds;   // 段内「吵闹」有效时长（秒）
    private double _fallSeconds;           // 当前连续回落时长（秒）
    private NoiseLevel? _segmentLevel;     // 实时归属（起算后非空）

    public NoiseLevel Level => _level;
    public double SmoothedRms => _smooth;

    /// <summary>当前是否处于段（含回落中）。</summary>
    public bool IsSegmentActive => _segmentActive;

    /// <summary>当前段起算后的实时归属（一般/吵闹）；未起算或无段为 null。</summary>
    public NoiseLevel? SegmentLevel => _segmentActive ? _segmentLevel : null;

    public void Reset()
    {
        _initialized = false;
        _smooth = 0;
        _level = NoiseLevel.Quiet;
        _time = 0;
        _segmentActive = false;
        _segmentNormalSeconds = 0;
        _segmentNoisySeconds = 0;
        _fallSeconds = 0;
        _segmentLevel = null;
    }

    /// <summary>
    /// 处理一块音频。dtSeconds 为这块的真实时长（秒），由调用方按实际音频块长传入；
    /// 默认 0.1 对应 100ms 采样块，纯算法测试用。
    /// </summary>
    public SampleResult Process(double rawRms, double dtSeconds = 0.1)
    {
        if (dtSeconds <= 0) dtSeconds = 0.1;
        _time += dtSeconds;

        if (!_initialized)
        {
            _smooth = rawRms;
            _initialized = true;
        }
        else
        {
            // 平滑系数按块长折算：SmoothAlpha 是 100ms 块下的取值，块长变化时保持等效时间常数不变
            // （否则把音频块改小会让平滑变弱、读数变毛刺）。
            var alpha = SmoothAlpha >= 1 ? 1 : 1 - Math.Pow(1 - SmoothAlpha, dtSeconds / 0.1);
            _smooth = _smooth * (1 - alpha) + rawRms * alpha;
        }

        var prev = _level;
        _level = Classify(_smooth, _level);

        NoiseLevel? fired = null;

        if (IsCountable(_level))
        {
            // 回到可计等级：开段（或继续段），回落计时清零
            if (!_segmentActive)
            {
                _segmentActive = true;
                _segmentNormalSeconds = 0;
                _segmentNoisySeconds = 0;
                _segmentLevel = null;
            }
            _fallSeconds = 0;

            // 累计有效时长（一般/吵闹分别计）
            if (_level == NoiseLevel.Noisy) _segmentNoisySeconds += dtSeconds;
            else _segmentNormalSeconds += dtSeconds;

            // 实时归属：起算后按当前段内占比临时判定（供「正在记录」提示切换）
            var total = _segmentNormalSeconds + _segmentNoisySeconds;
            if (total >= SustainSeconds)
            {
                var ratio = total > 0 ? _segmentNoisySeconds / total : 0;
                _segmentLevel = ratio >= NoisyRatioThreshold ? NoiseLevel.Noisy : NoiseLevel.Normal;
            }
        }
        else if (_segmentActive)
        {
            // 掉到不可计等级：进入回落计时，超过回落窗口段才结束
            _fallSeconds += dtSeconds;
            if (_fallSeconds > FallWindowSeconds)
            {
                var total = _segmentNormalSeconds + _segmentNoisySeconds;
                if (total >= SustainSeconds)
                {
                    var ratio = total > 0 ? _segmentNoisySeconds / total : 0;
                    fired = ratio >= NoisyRatioThreshold ? NoiseLevel.Noisy : NoiseLevel.Normal;
                }
                _segmentActive = false;
                _segmentNormalSeconds = 0;
                _segmentNoisySeconds = 0;
                _fallSeconds = 0;
                _segmentLevel = null;
            }
        }

        return new SampleResult
        {
            Level = _level,
            LevelChanged = prev != _level,
            SmoothedRms = _smooth,
            EventFired = fired.HasValue,
            EventLevel = fired,
            SegmentLevel = _segmentActive ? _segmentLevel : null
        };
    }

    private static bool IsCountable(NoiseLevel level)
        => level == NoiseLevel.Normal || level == NoiseLevel.Noisy;

    /// <summary>
    /// 迟滞分级（Schmitt 触发）：升级用名义阈值，降级用名义阈值 × HysteresisFactor。
    /// </summary>
    private NoiseLevel Classify(double rms, NoiseLevel current)
    {
        double goodDown = GoodThreshold * HysteresisFactor;
        double normalDown = NormalThreshold * HysteresisFactor;
        double noisyDown = NoisyThreshold * HysteresisFactor;

        switch (current)
        {
            case NoiseLevel.Quiet:
                if (rms >= NoisyThreshold) return NoiseLevel.Noisy;
                if (rms >= NormalThreshold) return NoiseLevel.Normal;
                if (rms >= GoodThreshold) return NoiseLevel.Good;
                return NoiseLevel.Quiet;
            case NoiseLevel.Good:
                if (rms >= NoisyThreshold) return NoiseLevel.Noisy;
                if (rms >= NormalThreshold) return NoiseLevel.Normal;
                if (rms < goodDown) return NoiseLevel.Quiet;
                return NoiseLevel.Good;
            case NoiseLevel.Normal:
                if (rms >= NoisyThreshold) return NoiseLevel.Noisy;
                if (rms < normalDown)
                    return rms < goodDown ? NoiseLevel.Quiet : NoiseLevel.Good;
                return NoiseLevel.Normal;
            case NoiseLevel.Noisy:
                if (rms < noisyDown)
                    return rms >= NormalThreshold ? NoiseLevel.Normal
                         : rms >= GoodThreshold ? NoiseLevel.Good
                         : NoiseLevel.Quiet;
                return NoiseLevel.Noisy;
            default:
                return current;
        }
    }
}
