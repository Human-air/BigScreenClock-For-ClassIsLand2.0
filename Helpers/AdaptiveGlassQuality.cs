using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Helpers;

internal enum GlassFallbackReason { BufferBudget, SlowRendering, RenderingFailed, UiStall }

/// <summary>会话内单向降档，避免全屏/半屏切换时重复申请大缓冲。</summary>
internal sealed class AdaptiveGlassQuality
{
    internal const long BufferBudget = 44L * 1024 * 1024;
    internal const double StallMilliseconds = 750;
    private const int RequiredSignals = 3;
    private int _slowBakes;
    private int _stalls;
    private TimeSpan _firstStall;

    public GlassMode Mode { get; private set; } = GlassMode.Liquid;

    public void Reset()
    {
        Mode = GlassMode.Liquid;
        ClearSignals();
    }

    public void ClearSignals()
    {
        _slowBakes = 0;
        _stalls = 0;
    }

    public bool ObserveBake(double milliseconds, bool failed)
    {
        _slowBakes = failed || milliseconds >= 500 ? _slowBakes + 1 : 0;
        return _slowBakes >= RequiredSignals;
    }

    public bool ObserveStall(TimeSpan now)
    {
        if (_stalls == 0 || now - _firstStall > TimeSpan.FromSeconds(30))
        {
            _firstStall = now;
            _stalls = 0;
        }
        return ++_stalls >= RequiredSignals;
    }

    public void Lower()
    {
        Mode = Mode == GlassMode.Liquid ? GlassMode.Frosted : GlassMode.Translucent;
        ClearSignals();
    }
}
