using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EveningSelfStudyClock.Helpers;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 一张背景图「烘」出来的全部东西：清晰位图、模糊位图、两份像素快照。
///
/// 这段活（解码 + 整张模糊 + 把像素倒成 CPU 能直接采样的数组）以前只在大屏点亮那一刻当场干：
/// 4K 一张图要一次性吃掉几百兆临时内存，刚好撞上 CI 的内存看门狗（1.40 GiB）。
/// 现在它可以提前在后台做好（<see cref="Services.BackdropPreloadService"/>），开屏时直接取现成的；
/// 取不到就照旧当场烘，行为不变。
/// </summary>
public sealed class BackdropImageSet : IDisposable
{
    // Shared by the live window, preview and preloader: only one large decode/blur at a time.
    private static readonly SemaphoreSlim BuildGate = new(1, 1);

    /// <summary>光学采样最多约 1080p；清晰背景仍按实际显示尺寸解码。</summary>
    public const int OpticalPixelLimit = 1920 * 1080;

    /// <summary>构建失败的原因，供调用方写诊断日志；取消通过异常传播。</summary>
    public Exception? BuildError { get; private set; }

    /// <summary>是按哪张图烘的（绝对路径）。</summary>
    public string Path { get; private init; } = "";

    /// <summary>填充方式（<see cref="BackgroundImageStretch"/> 的整数值）——它决定落位与解码尺寸。</summary>
    public int Stretch { get; private init; }

    /// <summary>按屏幕上多少像素的模糊半径烘的（<see cref="PluginSettings.GlassBlurRadius"/> 原值，不是换算成图片像素后的）。</summary>
    public double BlurRadius { get; private init; }

    /// <summary>这一套里有没有模糊底图。</summary>
    public bool WantBlur { get; private init; }

    /// <summary>下这份订单时要的物理像素尺寸（烘出来的位图可能因只缩不放而小于它，看 <see cref="Sharp"/>）。</summary>
    public PixelSize Pixel { get; private init; }

    public PixelSize SourceSize { get; private set; }
    public long OpticalBytes => 4L * ((SharpPixels?.LongLength ?? 0) + (BlurPixels?.LongLength ?? 0) +
        (Blur is { } blur ? (long)blur.PixelSize.Width * blur.PixelSize.Height : 0));

    public Bitmap? Sharp { get; private set; }
    public WriteableBitmap? Blur { get; private set; }
    public uint[]? SharpPixels { get; private set; }
    public PixelSize SharpPixelsSize { get; private set; }
    public uint[]? BlurPixels { get; private set; }

    /// <summary>图没读出来（文件不在 / 格式不支持）时为真：等同于「没有背景图」。</summary>
    public bool IsEmpty => Sharp is null;

    /// <summary>
    /// 同步烘一套。后台执行，跨窗口串行。普通失败返回空结果并保留 BuildError，取消则抛出取消异常。
    /// </summary>
    public static BackdropImageSet Build(string path, PixelSize pixels, int stretch, double blurRadius, bool wantBlur,
        CancellationToken cancellationToken = default, bool captureSamples = true)
    {
        var set = new BackdropImageSet
        {
            Path = path,
            Stretch = stretch,
            BlurRadius = blurRadius,
            WantBlur = wantBlur,
            Pixel = pixels,
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (path.Length == 0) return set;
        if (!File.Exists(path))
        {
            set.BuildError = new FileNotFoundException("背景图片不存在。", path);
            return set;
        }

        // The gate bounds temporary buffers across independent BackdropLayer instances.
        BuildGate.Wait(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var native = ImageHeader.ReadSize(path) ?? pixels;
            set.SourceSize = native;
            var decode = BackgroundImageMap.DecodeSize(native, pixels, stretch);
            using var stream = File.OpenRead(path);
            set.Sharp = Bitmap.DecodeToWidth(stream, decode.Width, BitmapInterpolationMode.HighQuality);
            cancellationToken.ThrowIfCancellationRequested();

            if (!wantBlur && !captureSamples) return set;

            // Clear background rendering keeps the requested resolution. Refraction and blur
            // need smooth optical samples, not another full 4K copy for every retained buffer.
            var opticalSize = OpticalSize(set.Sharp.PixelSize);
            using var reduced = opticalSize == set.Sharp.PixelSize
                ? null
                : set.Sharp.CreateScaledBitmap(opticalSize, BitmapInterpolationMode.HighQuality);
            var optical = reduced ?? set.Sharp;
            cancellationToken.ThrowIfCancellationRequested();
            if (captureSamples)
            {
                set.SharpPixels = ReadPixels(optical, cancellationToken);
                set.SharpPixelsSize = optical.PixelSize;
            }
            if (wantBlur)
            {
                var blurred = BakeBlur(optical,
                    BlurRadiusInImagePixels(blurRadius, optical.PixelSize, pixels, stretch), cancellationToken);
                set.Blur = blurred.Bitmap;
                if (captureSamples) set.BlurPixels = blurred.Pixels;
            }
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            set.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            set.BuildError = ex;
            set.Dispose();
        }
        finally { BuildGate.Release(); }
        return set;
    }

    private static PixelSize OpticalSize(PixelSize source)
    {
        var count = (long)source.Width * source.Height;
        if (count <= OpticalPixelLimit) return source;
        var scale = Math.Sqrt(OpticalPixelLimit / (double)count);
        return new PixelSize(Math.Max(1, (int)Math.Floor(source.Width * scale)),
            Math.Max(1, (int)Math.Floor(source.Height * scale)));
    }

    /// <summary>
    /// 这一套能不能顶替「按这些参数现在要烘的那一份」。
    ///
    /// 尺寸允许有偏差：预烘时是按屏幕物理分辨率估的，真开窗时可能差一两个像素
    /// （缩放倍率换算的取整）。差一点点完全不影响观感 —— 画的时候按位图自己的尺寸落位，
    /// 采样也按快照自己的尺寸算，整套是自洽的，只是分辨率略微不同。差太多（换过屏幕 / 窗口尺寸
    /// 大改）就必须重来，否则整张图会明显发虚。
    /// </summary>
    public bool Matches(string path, PixelSize pixels, int stretch, double blurRadius, bool wantBlur)
    {
        if (IsEmpty || path != Path || stretch != Stretch || wantBlur != WantBlur) return false;

        // 半径只在「要模糊」时才算数（不要模糊时这份参数根本没用上），容差与 Apply 里的重烘判断一致
        if (wantBlur && Math.Abs(blurRadius - BlurRadius) > 0.4) return false;

        return WithinTolerance(Pixel, pixels);
    }

    private static bool WithinTolerance(PixelSize built, PixelSize wanted)
    {
        if (built.Width <= 0 || built.Height <= 0) return false;
        return Math.Abs(built.Width - wanted.Width) <= Math.Max(4, built.Width / 10)
               && Math.Abs(built.Height - wanted.Height) <= Math.Max(4, built.Height / 10);
    }

    public void ReleaseOptics(bool keepBlur)
    {
        SharpPixels = null;
        BlurPixels = null;
        SharpPixelsSize = default;
        if (keepBlur) return;
        var blur = Blur;
        Blur = null;
        blur?.Dispose();
    }

    public void Dispose()
    {
        var sharp = Sharp;
        Sharp = null;
        try { sharp?.Dispose(); }
        finally { ReleaseOptics(keepBlur: false); }
    }

    /// <summary>
    /// 把「屏幕上多少像素」的模糊半径换成底图自己的像素。
    ///
    /// 两个尺寸都得按**实际落位**算：底图铺在这一层上占
    /// <see cref="BackgroundImageMap.ComputeDestRect"/> 算出来的那块矩形，占多大就缩放多少倍。
    /// 以前的写法只用宽度比（图宽 / 层宽），遇上极端长条图会算出几十上百倍：比如一张
    /// 100000×10 的截图，铺满整屏时缩短边定比例、宽度要溢出十几屏，宽度比是几十；
    /// 而「完整显示」档下一条 1×2160 的竖图更是能到 46 倍。半径两百多，StackBlur 那张
    /// 查找表就上百兆，一张图足以把内存看门狗顶爆（于是 <see cref="StackBlur.MaxRadius"/> 再兜一道）。
    ///
    /// 正常图片（图与屏比例在 1 附近）算出来和旧写法一致。
    /// </summary>
    private static double BlurRadiusInImagePixels(double radius, PixelSize imageSize, PixelSize layerPixels, int stretch)
    {
        if (radius <= 0) return 0;

        var ratio = BackgroundImageMap.ImagePixelsPerViewportPixel(
            imageSize, new Size(layerPixels.Width, layerPixels.Height), stretch);

        // 硬上限见 StackBlur.MaxRadius：换算再怎么离谱，也不让它把查找表撑爆
        return Math.Clamp(radius * ratio, 0, StackBlur.MaxRadius);
    }

    /// <summary>
    /// 模糊低分辨率光学底图，最终像素数组直接作为折射快照使用，不再另拷一份。
    /// 模糊半径按光学图的实际像素换算，因此屏幕上的模糊尺度保持一致。
    /// </summary>
    private static (WriteableBitmap? Bitmap, uint[]? Pixels) BakeBlur(
        Bitmap source, double radius, CancellationToken cancellationToken)
    {
        var size = source.PixelSize;
        if (size.Width < 2 || size.Height < 2) return (null, null);
        var bitmap = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var framebuffer = bitmap.Lock();
            source.CopyPixels(framebuffer, AlphaFormat.Premul);
            var pixels = CopyOut(framebuffer, size, cancellationToken);
            StackBlur.Apply(pixels, size.Width, size.Height, (int)Math.Round(radius), cancellationToken);
            CopyInto(framebuffer, pixels, size, cancellationToken);
            return (bitmap, pixels);
        }
        catch
        {
            // Until the method returns, this bitmap is not owned by set.Blur.
            bitmap.Dispose();
            throw;
        }
    }

    private static uint[]? ReadPixels(Bitmap source, CancellationToken cancellationToken)
    {
        var size = source.PixelSize;
        if (size.Width < 1 || size.Height < 1) return null;
        using var staging = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = staging.Lock();
        source.CopyPixels(frame, AlphaFormat.Premul);
        return CopyOut(frame, size, cancellationToken);
    }

    private static unsafe uint[] CopyOut(ILockedFramebuffer frame, PixelSize size, CancellationToken cancellationToken)
    {
        var rowBytes = checked((long)size.Width * 4);
        var pixels = new uint[checked(size.Width * size.Height)];
        fixed (uint* dst = pixels)
        {
            for (var y = 0; y < size.Height; y++)
            {
                if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                Buffer.MemoryCopy((byte*)frame.Address + (long)y * frame.RowBytes,
                    dst + (long)y * size.Width, rowBytes, rowBytes);
            }
        }
        return pixels;
    }

    private static unsafe void CopyInto(ILockedFramebuffer frame, uint[] pixels, PixelSize size,
        CancellationToken cancellationToken)
    {
        var rowBytes = checked((long)size.Width * 4);
        fixed (uint* src = pixels)
        {
            for (var y = 0; y < size.Height; y++)
            {
                if ((y & 31) == 0) cancellationToken.ThrowIfCancellationRequested();
                Buffer.MemoryCopy(src + (long)y * size.Width,
                    (byte*)frame.Address + (long)y * frame.RowBytes, rowBytes, rowBytes);
            }
        }
    }
}
