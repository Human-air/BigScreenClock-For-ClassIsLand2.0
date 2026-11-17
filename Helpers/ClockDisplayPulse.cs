namespace EveningSelfStudyClock.Helpers;

/// <summary>高频采样实际墙钟，每秒只发布一次文字；不会用递增计数伪造被卡顿错过的时间。</summary>
public sealed class ClockDisplayPulse(Action<DateTime> publish)
{
    private long _lastSecond = long.MinValue;
    public void Tick(DateTime now)
    {
        var second = now.Ticks / TimeSpan.TicksPerSecond;
        if (second == _lastSecond) return;
        _lastSecond = second;
        publish(now);
    }
}
