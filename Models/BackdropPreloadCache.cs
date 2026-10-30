using Avalonia;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 预烘好的底图暂存处：<see cref="Services.BackdropPreloadService"/> 往里放，
/// <see cref="Views.BackdropLayer"/> 开屏时来取。只有一份（「即将要用的那一张」），
/// 取走即从缓存里摘掉，谁拿走谁负责释放。
/// </summary>
public static class BackdropPreloadCache
{
    private static readonly object Gate = new();
    private static BackdropImageSet? _ready;
    private static DateTime _readyAt;
    private static bool _building;

    /// <summary>占住「正在烘」的名额；已经有人在烘就返回 false（别烘第二份）。</summary>
    public static bool TryBeginBuild()
    {
        lock (Gate)
        {
            if (_building) return false;
            _building = true;
            return true;
        }
    }

    /// <summary>这次没烘成（图读不了 / 参数已经变了）：只把名额还回去，手上的留着。</summary>
    public static void EndBuild()
    {
        lock (Gate) _building = false;
    }

    /// <summary>放一份收工；原来那份（如果有）释放掉。</summary>
    public static void Publish(BackdropImageSet set)
    {
        BackdropImageSet? old;
        lock (Gate)
        {
            old = _ready;
            _ready = set;
            _readyAt = DateTime.UtcNow;
            _building = false;
        }
        if (!ReferenceEquals(old, set)) old?.Dispose();
    }

    /// <summary>
    /// 手上这份已经放了这么久还没人来取（课取消了 / 一直没开屏）：该放掉了。
    /// 不放的话它会攥着一两百兆占一整天。
    /// </summary>
    public static bool IsStale(TimeSpan maxAge)
    {
        lock (Gate) return _ready is not null && DateTime.UtcNow - _readyAt > maxAge;
    }

    /// <summary>手上已经有一份能用的了（同一张图 / 同一套参数 / 尺寸对得上）。</summary>
    public static bool IsReadyFor(string path, PixelSize pixels, int stretch, double blurRadius, bool wantBlur)
    {
        lock (Gate)
            return _ready is { } ready && ready.Matches(path, pixels, stretch, blurRadius, wantBlur);
    }

    /// <summary>取一份能用的；取到即从缓存里摘掉（之后归调用方管）。参数对不上就留着不动。</summary>
    public static BackdropImageSet? TryTake(string path, PixelSize pixels, int stretch, double blurRadius, bool wantBlur)
    {
        lock (Gate)
        {
            if (_ready is { } ready && ready.Matches(path, pixels, stretch, blurRadius, wantBlur))
            {
                _ready = null;
                return ready;
            }
            return null;
        }
    }

    /// <summary>
    /// 调用方自己烘好了一份同样参数的：缓存里那份就多余了，放掉（不然后台一直攥着上百兆）。
    /// </summary>
    public static void ReleaseIfMatches(string path, PixelSize pixels, int stretch, double blurRadius, bool wantBlur)
    {
        BackdropImageSet? old = null;
        lock (Gate)
        {
            if (_ready is { } ready && ready.Matches(path, pixels, stretch, blurRadius, wantBlur))
            {
                old = _ready;
                _ready = null;
            }
        }
        old?.Dispose();
    }

    /// <summary>设置变了（换图 / 换填充方式 / 改模糊半径 / 改玻璃档）：手上那份作废。</summary>
    public static void Clear()
    {
        BackdropImageSet? old;
        lock (Gate)
        {
            old = _ready;
            _ready = null;
        }
        old?.Dispose();
    }
}
