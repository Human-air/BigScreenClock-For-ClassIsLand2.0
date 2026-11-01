using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using EveningSelfStudyClock.Models;
using OpenCvSharp;

namespace EveningSelfStudyClock.Services;

/// <summary>可抓拍的摄像头（按 OpenCV 的 DSHOW 索引识别；与远端占用检测用的 CameraDevice 是两套东西）。</summary>
public sealed record SnapshotCamera(int Index, string Label)
{
    /// <summary>下拉框直接显示设备描述。</summary>
    public override string ToString() => Label;
}

/// <summary>
/// 抓拍服务（基于 OpenCvSharp / DirectShow），供飞书推送附图用。
/// 插件以「插件程序集」的形式被主程序加载，原生 DLL 不在主程序目录里，
/// 因此需要自定义 DllImport 解析器，把 OpenCvSharpExtern 指向插件所在目录。
/// </summary>
public class SnapshotService
{
    private const string NativeLibName = "OpenCvSharpExtern";

    private readonly PluginSettings _settings;
    private static int _resolverRegistered;
    private readonly object _captureLock = new();

    public SnapshotService(PluginSettings settings)
    {
        _settings = settings;
    }

    /// <summary>注册原生库解析器（每个进程只需一次）。</summary>
    public static void EnsureNativeResolver()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) == 1) return;

        try
        {
            NativeLibrary.SetDllImportResolver(typeof(Mat).Assembly, (name, assembly, searchPath) =>
            {
                var dir = Path.GetDirectoryName(typeof(SnapshotService).Assembly.Location);
                if (string.IsNullOrEmpty(dir)) return IntPtr.Zero;

                var candidates = new[]
                {
                    Path.Combine(dir, name + ".dll"),
                    Path.Combine(dir, "runtimes", "win-x64", "native", name + ".dll"),
                    Path.Combine(dir, name),
                };

                foreach (var path in candidates)
                {
                    if (File.Exists(path))
                        return NativeLibrary.Load(path);
                }

                return IntPtr.Zero; // 交给系统默认查找
            });
        }
        catch (InvalidOperationException)
        {
            // 已有解析器（例如重复加载），忽略
        }
    }

    /// <summary>扫描可用摄像头（逐个索引试开，可能耗时 1~3 秒，需在后台线程调用）。</summary>
    public static IReadOnlyList<SnapshotCamera> Enumerate(int maxIndex = 6)
    {
        var result = new List<SnapshotCamera>();
        try
        {
            EnsureNativeResolver();
            for (var i = 0; i < maxIndex; i++)
            {
                try
                {
                    using var cap = new VideoCapture(i, VideoCaptureAPIs.DSHOW);
                    if (!cap.IsOpened()) continue;

                    var w = (int)cap.Get(VideoCaptureProperties.FrameWidth);
                    var h = (int)cap.Get(VideoCaptureProperties.FrameHeight);
                    var label = w > 0 && h > 0
                        ? $"摄像头 {i + 1} · {w}×{h}"
                        : $"摄像头 {i + 1}";
                    result.Add(new SnapshotCamera(i, label));
                }
                catch
                {
                    // 该索引不可用，继续
                }
            }
        }
        catch
        {
            // OpenCV 原生库缺失等，返回空列表
        }

        return result;
    }

    /// <summary>
    /// 抓拍一帧并编码为 JPEG。失败返回 null 并给出原因。
    /// 摄像头打开可能阻塞，内部放到后台线程并限时。
    /// </summary>
    public async Task<(byte[]? Jpeg, string? Error)> CaptureAsync(int timeoutSeconds = 9)
    {
        if (!_settings.EnableSnapshot)
            return (null, "抓拍未启用");

        var index = Math.Max(0, _settings.SnapshotCameraIndex);
        var width = Math.Max(320, _settings.SnapshotWidth);
        var height = Math.Max(240, _settings.SnapshotHeight);

        var work = Task.Run(() => CaptureCore(index, width, height));
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(timeoutSeconds)))
                                 .ConfigureAwait(false);

        if (finished != work)
            return (null, $"打开摄像头超时（{timeoutSeconds} 秒）。可能被其它程序占用（如会议软件 / 相机应用）。");

        return await work.ConfigureAwait(false);
    }

    private (byte[]? Jpeg, string? Error) CaptureCore(int index, int width, int height)
    {
        // 同一时刻只允许一个抓拍，避免摄像头被并发打开
        if (!Monitor.TryEnter(_captureLock))
            return (null, "上一次抓拍尚未结束");

        try
        {
            EnsureNativeResolver();

            using var cap = new VideoCapture(index, VideoCaptureAPIs.DSHOW);
            if (!cap.IsOpened())
                return (null, $"无法打开摄像头 {index + 1}，请在设置页换一个设备后重试");

            cap.Set(VideoCaptureProperties.FrameWidth, width);
            cap.Set(VideoCaptureProperties.FrameHeight, height);
            cap.Set(VideoCaptureProperties.BufferSize, 1);

            using var frame = new Mat();
            // 丢掉前几帧：刚打开时曝光/白平衡未稳定，前几帧常偏黑
            for (var i = 0; i < 6; i++)
            {
                if (!cap.Read(frame) || frame.Empty())
                    Thread.Sleep(120);
                else if (i < 5)
                    Thread.Sleep(60);
            }

            if (frame.Empty())
                return (null, "摄像头未返回画面（可能被占用或驱动异常）");

            Cv2.ImEncode(".jpg", frame, out var buffer,
                new ImageEncodingParam(ImwriteFlags.JpegQuality, 85));

            if (buffer is null || buffer.Length == 0)
                return (null, "画面编码失败");

            return (buffer, null);
        }
        catch (DllNotFoundException ex)
        {
            return (null, $"缺少 OpenCV 原生库：{ex.Message}");
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            Monitor.Exit(_captureLock);
        }
    }

    /// <summary>把抓拍结果保存到本地（图片\大屏时钟抓拍\yyyy-MM-dd\HHmmss.jpg），返回完整路径。</summary>
    public static string? TrySaveLocal(byte[] jpeg, DateTime time)
    {
        try
        {
            var root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                "大屏时钟抓拍",
                time.ToString("yyyy-MM-dd"));

            Directory.CreateDirectory(root);
            var file = Path.Combine(root, $"{time:HHmmss}.jpg");
            File.WriteAllBytes(file, jpeg);
            return file;
        }
        catch
        {
            return null;
        }
    }
}
