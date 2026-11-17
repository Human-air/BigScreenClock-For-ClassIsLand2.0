using System;
using System.Collections.Generic;

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
/// V3 检测包：EMA 平滑 + 迟滞分级 + 最近时间窗锁定 + 段结束结算。
/// 连续吵闹 5 秒，或最近 10 秒内累计吵闹 6 秒后，本段锁定为吵闹。
/// 一般时间不稀释已取得的锁定；先一般后吵闹也可升级，不在起算时提前定格。
/// 起算底线、短回落合并、每段结束只结算一次保持不变；计数层没有冷却。
/// 瞬时 Level 独立反映当前音量，SegmentLevel 是本段最终归属的实时状态。
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

    // 本检测包先固定参数，便于收集同一规则下的真机反馈。
    public const double NoisyWindowSeconds = 10.0;
    public const double NoisyWindowRequiredSeconds = 6.0;
    public const double NoisyContinuousSeconds = 5.0;

    private double _smooth;
    private bool _initialized;
    private NoiseLevel _level = NoiseLevel.Quiet;
    private double _time;

    // 段状态
    private bool _segmentActive;           // 当前是否有段
    private double _effectiveSeconds;
    private double _continuousNoisySeconds;
    private bool _noisyLocked;
    // 仅保存最近 10 秒的吵闹音频块（生产采样间隔至少 100ms），不保存音频数据。
    // 锁定后立即清空，并停止采集证据，长期处于同一段也不会积累历史样本。
    private readonly Queue<(double Start, double End)> _recentNoisy = new();
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
        _effectiveSeconds = 0;
        _continuousNoisySeconds = 0;
        _noisyLocked = false;
        _recentNoisy.Clear();
        _fallSeconds = 0;
        _segmentLevel = null;
    }

    /// <summary>
    /// 处理一块音频。dtSeconds 为这块的真实时长（秒），由调用方按实际音频块长传入；
    /// 默认 0.1 对应 100ms 采样块，纯算法测试用。
    /// </summary>
    public SampleResult Process(double rawRms, double dtSeconds = 0.1)
    {
        // 无效采样不改变平滑器、分段或证据，避免 NaN 污染后续整节课。
        if (!double.IsFinite(rawRms) || rawRms < 0 || !double.IsFinite(dtSeconds) || dtSeconds <= 0)
            return new SampleResult { Level = _level, SmoothedRms = _smooth, SegmentLevel = SegmentLevel };
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

        var countable = IsCountable(_level);
        if (!_segmentActive && countable)
        {
            _segmentActive = true;
            _effectiveSeconds = 0;
            _continuousNoisySeconds = 0;
            _noisyLocked = false;
            _recentNoisy.Clear();
            _segmentLevel = null;
        }

        if (_segmentActive)
        {
            UpdateNoisyEvidence(dtSeconds);
            if (countable)
            {
                _fallSeconds = 0;
                _effectiveSeconds += dtSeconds;
                if (_effectiveSeconds + 1e-9 >= SustainSeconds)
                    _segmentLevel = _noisyLocked ? NoiseLevel.Noisy : NoiseLevel.Normal;
            }
            else
            {
                _fallSeconds += dtSeconds;
                if (_fallSeconds > FallWindowSeconds)
                {
                    if (_effectiveSeconds + 1e-9 >= SustainSeconds)
                        fired = _noisyLocked ? NoiseLevel.Noisy : NoiseLevel.Normal;
                    _segmentActive = false;
                    _effectiveSeconds = 0;
                    _continuousNoisySeconds = 0;
                    _noisyLocked = false;
                    _recentNoisy.Clear();
                    _fallSeconds = 0;
                    _segmentLevel = null;
                }
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

    private void UpdateNoisyEvidence(double dtSeconds)
    {
        if (_noisyLocked) return;
        var cutoff = _time - NoisyWindowSeconds;
        while (_recentNoisy.TryPeek(out var oldest) && oldest.End <= cutoff)
            _recentNoisy.Dequeue();

        if (_level == NoiseLevel.Noisy)
        {
            _continuousNoisySeconds += dtSeconds;
            _recentNoisy.Enqueue((_time - dtSeconds, _time));
        }
        else
            _continuousNoisySeconds = 0;

        // 首块可仅部分落在时间窗内；按真实时长裁剪，不按样本数估算秒数。
        double recentSeconds = 0;
        foreach (var block in _recentNoisy)
            recentSeconds += block.End - Math.Max(block.Start, cutoff);

        if (_continuousNoisySeconds + 1e-9 >= NoisyContinuousSeconds ||
            recentSeconds + 1e-9 >= NoisyWindowRequiredSeconds)
        {
            _noisyLocked = true;
            _recentNoisy.Clear();
        }
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
