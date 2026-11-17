using System;

namespace EveningSelfStudyClock.NoiseDetection;

/// <summary>一次持续事件在计数层的处理结果。</summary>
public enum CountDecision
{
    None = 0,
    CountNormal = 1,
    CountNoisy = 2
}

/// <summary>
/// V2.1 计数规则（纯算法，无任何外部依赖，便于单元测试）。
/// 「一段算一次、吵闹证据锁定」由 NoiseEventDetector 在段结束结算时保证（每段至多一次事件），
/// 本类只应用 保护 / 课间 规则：课间、上课初期保护期内不计数，其余一律计数（已无冷却）。
/// </summary>
public sealed class NoiseCounter
{
    private bool _inBreak;

    public int NormalCount { get; private set; }
    public int NoisyCount { get; private set; }

    /// <summary>课间状态。课间内的任何事件都不计数。</summary>
    public void SetBreak(bool inBreak) => _inBreak = inBreak;

    /// <summary>新一节课开始：清空计数。</summary>
    public void StartNewClass()
    {
        NormalCount = 0;
        NoisyCount = 0;
    }

    /// <summary>
    /// 处理一次持续事件（检测器段结束结算产生）。
    /// isProtected = 上课初期保护期内（不计数）。
    /// </summary>
    public CountDecision OnEvent(NoiseLevel level, bool isProtected)
    {
        if (_inBreak || isProtected) return CountDecision.None;
        if (level == NoiseLevel.Noisy)
        {
            NoisyCount++;
            return CountDecision.CountNoisy;
        }
        if (level == NoiseLevel.Normal)
        {
            NormalCount++;
            return CountDecision.CountNormal;
        }
        return CountDecision.None;
    }
}
