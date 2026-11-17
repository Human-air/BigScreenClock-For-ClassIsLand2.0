using System.Diagnostics;

namespace EveningSelfStudyClock.Helpers;

/// <summary>有界的背景资源诊断；只记录重建/释放/错误，不记录逐帧事件。</summary>
public static class BackgroundDiagnostics
{
    public static string? DestinationFolder { get; set; }
    private static readonly object Gate = new();
    public static void Write(string operation, string detail, Exception? error = null)
    {
        var folder = DestinationFolder;
        if (string.IsNullOrEmpty(folder)) return;
        try
        {
            using var process = Process.GetCurrentProcess();
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {operation} privateMiB={process.PrivateMemorySize64 / 1048576.0:F1} managedMiB={GC.GetTotalMemory(false) / 1048576.0:F1} {detail}";
            if (error is not null) line += $" error={error.GetType().Name}: {error.Message}";
            lock (Gate)
            {
                Directory.CreateDirectory(folder);
                var file = Path.Combine(folder, "BackgroundMemory.log");
                if (File.Exists(file) && new FileInfo(file).Length > 512 * 1024)
                    File.Move(file, Path.Combine(folder, "BackgroundMemory.previous.log"), true);
                File.AppendAllText(file, line + Environment.NewLine);
            }
        }
        catch { /* 诊断失败不能破坏显示 */ }
    }
}
