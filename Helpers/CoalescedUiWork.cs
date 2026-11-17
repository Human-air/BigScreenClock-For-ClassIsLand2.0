namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 合并后台定时器的 UI 请求：排队或执行期间最多保留一次工作，防止 UI 忙时积压。
/// 每个运行会话使用独立实例；停止时释放回调引用，已排队的旧工作随即失效。
/// </summary>
public sealed class CoalescedUiWork(Action<Action> schedule, Action callback) : IDisposable
{
    private Action? _callback = callback;
    private int _pending;
    private int _disposed;

    public void Request()
    {
        if (Volatile.Read(ref _disposed) != 0
            || Interlocked.CompareExchange(ref _pending, 1, 0) != 0) return;
        try { schedule(Run); }
        catch
        {
            Volatile.Write(ref _pending, 0);
            throw;
        }
    }

    private void Run()
    {
        try
        {
            if (Volatile.Read(ref _disposed) == 0)
                Volatile.Read(ref _callback)?.Invoke();
        }
        finally { Volatile.Write(ref _pending, 0); }
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _disposed, 1);
        Interlocked.Exchange(ref _callback, null);
    }
}
