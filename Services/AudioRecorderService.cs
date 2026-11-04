using System.Diagnostics;
using NAudio.Wave;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 麦克风录音服务：按指定时长采集现场音频并封装为 WAV（44100Hz / 16bit / 单声道）。
/// 与音量检测（DecibelMeterService）相互独立——自己打开设备实例，Windows 允许同一
/// 麦克风被多个 waveIn 实例共享采集；全流程后台线程执行，不阻塞 UI / 音频线程。
/// </summary>
public class AudioRecorderService
{
    /// <summary>并发闸门：同一时刻只允许一段录音，重复触发（如连续吵闹事件）直接跳过。</summary>
    private static int _active;

    /// <summary>当前是否正在录音（供设置页/调试显示）。</summary>
    public static bool IsRecording => Volatile.Read(ref _active) == 1;

    /// <summary>
    /// 录制指定秒数的现场音频，返回 WAV 字节流。失败返回 (null, 错误原因)。
    /// 实际时长 = max(设定秒数, 收满目标字节数)，并带 +3 秒兜底超时。
    /// </summary>
    public async Task<(byte[]? Wav, string? Error)> RecordAsync(int seconds)
    {
        seconds = Math.Clamp(seconds, 1, 120);
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
            return (null, "已有录音在进行，跳过本次");

        try
        {
            return await Task.Run(() => RecordCore(seconds)).ConfigureAwait(false);
        }
        finally
        {
            Interlocked.Exchange(ref _active, 0);
        }
    }

    private static (byte[]? Wav, string? Error) RecordCore(int seconds)
    {
        if (WaveInEvent.DeviceCount == 0)
            return (null, "未检测到麦克风");

        // 打开设备：优先默认 0，失败则逐个尝试其余设备
        WaveInEvent? waveIn = null;
        foreach (var idx in Enumerable.Range(0, WaveInEvent.DeviceCount))
        {
            try
            {
                var w = new WaveInEvent { DeviceNumber = idx, WaveFormat = new WaveFormat(44100, 16, 1) };
                w.StartRecording();
                waveIn = w;
                break;
            }
            catch
            {
                // 该设备占用/失败，试下一个
            }
        }

        if (waveIn == null)
            return (null, "无法打开麦克风（可能被其它程序独占）");

        try
        {
            var format = waveIn.WaveFormat;
            var targetBytes = (long)format.AverageBytesPerSecond * seconds;

            using var ms = new MemoryStream();
            byte[] result;
            using (var writer = new WaveFileWriter(ms, format))
            {
                var received = 0L;
                using var done = new ManualResetEventSlim(false);
                void OnDataAvailable(object? s, WaveInEventArgs e)
                {
                    try
                    {
                        writer.Write(e.Buffer, 0, e.BytesRecorded);
                        received += e.BytesRecorded;
                        if (received >= targetBytes) done.Set();
                    }
                    catch { }
                }

                waveIn.DataAvailable += OnDataAvailable;
                try
                {
                    // 等待录满；+3 秒兜底（设备掉数据时不至于永久挂起）
                    done.Wait(TimeSpan.FromSeconds(seconds + 3));
                }
                finally
                {
                    waveIn.DataAvailable -= OnDataAvailable;
                }

                writer.Flush();
                result = ms.ToArray(); // 必须在 writer/流仍打开时读取
            }

            return result.Length > 44
                ? (result, null)
                : (null, "录到空音频（设备无数据）");
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            try { waveIn.StopRecording(); } catch { }
            try { waveIn.Dispose(); } catch { }
        }
    }

    /// <summary>录音备份到本地（音乐\大屏时钟录音\yyyy-MM-dd\HHmmss.wav），与抓拍存储规则一致。失败返回 null。</summary>
    public static string? TrySaveLocal(byte[] wav, DateTime time)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
                "大屏时钟录音",
                time.ToString("yyyy-MM-dd"));
            Directory.CreateDirectory(root);
            var path = Path.Combine(root, $"{time:HHmmss}.wav");
            for (var i = 1; File.Exists(path); i++)
                path = Path.Combine(root, $"{time:HHmmss}-{i}.wav");
            File.WriteAllBytes(path, wav);
            return path;
        }
        catch
        {
            return null;
        }
    }
}
