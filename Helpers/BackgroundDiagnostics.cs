using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace EveningSelfStudyClock.Helpers;

/// <summary>有界诊断日志，采集方不等待磁盘。只记录插件的显示、耗时和内存数据。</summary>
public static class BackgroundDiagnostics
{
    private const int LogLimit = 512 * 1024;
    private const int QueueLimit = 128;
    private const string CurrentFile = "ClockDiagnostics.log";
    private const string PreviousFile = "ClockDiagnostics.previous.log";
    private static readonly object QueueGate = new();
    private static readonly object FileGate = new();
    private static readonly Queue<string> Pending = new();
    private static bool _writerScheduled;

    public static string? DestinationFolder { get; set; }

    public static void Write(string operation, string detail, Exception? error = null)
    {
        if (string.IsNullOrEmpty(DestinationFolder)) return;
        // 异常正文可能带本地路径，诊断只记录异常类型。
        var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {operation} {detail}";
        if (error is not null) line += $" error={error.GetType().Name}";
        lock (QueueGate)
        {
            if (Pending.Count == QueueLimit) Pending.Dequeue();
            Pending.Enqueue(line);
            if (_writerScheduled) return;
            _writerScheduled = true;
        }
        _ = Task.Run(Drain);
    }

    private static void Drain()
    {
        while (true)
        {
            lock (FileGate) FlushPending();
            lock (QueueGate)
            {
                if (Pending.Count > 0) continue;
                _writerScheduled = false;
                return;
            }
        }
    }

    private static void FlushPending()
    {
        string[] lines;
        lock (QueueGate)
        {
            if (Pending.Count == 0) return;
            lines = Pending.ToArray();
            Pending.Clear();
        }
        try
        {
            if (DestinationFolder is not { Length: > 0 } folder) return;
            using var process = Process.GetCurrentProcess();
            var memory = $"privateMiB={process.PrivateMemorySize64 / 1048576.0:F1} managedMiB={GC.GetTotalMemory(false) / 1048576.0:F1}";
            var batch = string.Concat(lines.Select(line => $"{line} {memory}{Environment.NewLine}"));
            Directory.CreateDirectory(folder);
            var file = Path.Combine(folder, CurrentFile);
            if (File.Exists(file) && new FileInfo(file).Length + Encoding.UTF8.GetByteCount(batch) > LogLimit)
                File.Move(file, Path.Combine(folder, PreviousFile), overwrite: true);
            File.AppendAllText(file, batch);
        }
        catch { /* 磁盘诊断失败不能影响显示。 */ }
    }

    internal static byte[] CreateArchive(string summary)
    {
        using var output = new MemoryStream();
        using (var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var writer = new StreamWriter(zip.CreateEntry("summary.txt").Open(), Encoding.UTF8))
                writer.Write(summary);

            lock (FileGate)
            {
                FlushPending();
                if (DestinationFolder is { Length: > 0 } folder)
                {
                    foreach (var name in new[] { CurrentFile, PreviousFile })
                    {
                        var path = Path.Combine(folder, name);
                        if (File.Exists(path)) zip.CreateEntryFromFile(path, name);
                    }
                }
            }
        }
        return output.ToArray();
    }
}
