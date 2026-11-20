using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Helpers;

internal enum GlassFallbackReason { SlowRendering, RenderingFailed, UiStall }

/// <summary>先降低折射分辨率，再根据当前档位的新压力逐级降档。</summary>
internal sealed class AdaptiveGlassQuality
{
    internal const double StallMilliseconds = 1000;
    internal const double ReducedScale = 0.65;
    private const int RequiredSignals = 3;
    private int _slowBakes;
    private int _failedBakes;
    private int _stalls;
    private TimeSpan _firstStall;
    private TimeSpan? _lastBake;

    public GlassMode Mode { get; private set; }
    public bool Reduced { get; private set; }

    public void Reset(GlassMode mode)
    {
        Mode = mode;
        Reduced = false;
        ClearSignals();
    }

    public void ClearSignals()
    {
        _slowBakes = 0;
        _failedBakes = 0;
        _stalls = 0;
        _lastBake = null;
    }

    public GlassFallbackReason? ObserveBake(TimeSpan now, double milliseconds, bool failed)
    {
        if (_lastBake is { } previous && now - previous > TimeSpan.FromSeconds(20))
        {
            _slowBakes = 0;
            _failedBakes = 0;
        }
        _lastBake = now;
        _failedBakes = failed ? _failedBakes + 1 : 0;
        _slowBakes = !failed && milliseconds >= 850 ? _slowBakes + 1 : 0;

        if (_failedBakes >= RequiredSignals) return GlassFallbackReason.RenderingFailed;
        if (!Reduced && _slowBakes >= RequiredSignals) return GlassFallbackReason.SlowRendering;
        return null;
    }

    public bool ObserveStall(TimeSpan now)
    {
        if (_stalls == 0 || now - _firstStall > TimeSpan.FromSeconds(20))
        {
            _firstStall = now;
            _stalls = 0;
        }
        return ++_stalls >= RequiredSignals;
    }

    public void Lower()
    {
        if (Mode == GlassMode.Liquid && !Reduced)
            Reduced = true;
        else
        {
            Mode = Mode == GlassMode.Liquid ? GlassMode.Frosted : GlassMode.Translucent;
            Reduced = false;
        }
        ClearSignals();
    }
}
