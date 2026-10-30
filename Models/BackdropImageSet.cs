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

    public Bitmap? Sharp { get; private set; }
    public WriteableBitmap? Blur { get; private set; }
    public uint[]? SharpPixels { get; private set; }
    public PixelSize SharpPixelsSize { get; private set; }
    public uint[]? BlurPixels { get; private set; }

    /// <summary>图没读出来（文件不在 / 格式不支持）时为真：等同于「没有背景图」。</summary>
    public bool IsEmpty => Sharp is null;

    /// <summary>
    /// 同步烘一套。纯 CPU 的活，可以扔在后台线程上；任何异常都吞掉并返回空的一套
    /// （图读不了不该把整个大屏拖垮）。
    /// </summary>
    public static BackdropImageSet Build(string path, PixelSize pixels, int stretch, double blurRadius, bool wantBlur)
    {
        var set = new BackdropImageSet
        {
            Path = path,
            Stretch = stretch,
            BlurRadius = blurRadius,
            WantBlur = wantBlur,
            Pixel = pixels,
        };

        try
        {
            if (path.Length > 0 && File.Exists(path))
            {
                // 解码尺寸要按**原图**多大来定：解码器给多宽就解多宽，小图会被放大着解，
                // 白白多占一倍半内存还多插一遍值。原图尺寸只读文件头，不解码。
                var native = ImageHeader.ReadSize(path) ?? pixels;
                var decode = BackgroundImageMap.DecodeSize(native, pixels, stretch);

                using var stream = File.OpenRead(path);
                set.Sharp = Bitmap.DecodeToWidth(stream, decode.Width, BitmapInterpolationMode.HighQuality);
                // 折射要在 CPU 上逐像素采样，顺手把像素倒成连续数组（位图每次读都得先 Lock，太贵）
                set.SharpPixels = ReadPixels(set.Sharp);
                set.SharpPixelsSize = set.Sharp.PixelSize;
                if (wantBlur)
                {
                    set.Blur = BakeBlur(set.Sharp, BlurRadiusInImagePixels(blurRadius, set.Sharp.PixelSize, pixels, stretch));
                    if (set.Blur is not null) set.BlurPixels = ReadPixels(set.Blur);
                }
            }
        }
        catch
        {
            set.Dispose();
        }

        return set;
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
        if (path != Path || stretch != Stretch || wantBlur != WantBlur) return false;

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

    public void Dispose()
    {
        Sharp?.Dispose();
        Sharp = null;
        Blur?.Dispose();
        Blur = null;
        SharpPixels = null;
        BlurPixels = null;
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
    /// 烘一张模糊底图：把清晰图的像素整张拷出来、做一遍 Stack Blur。尺寸与清晰图一模一样。
    ///
    /// 为什么是「模糊整张图」而不是「按窗口尺寸烘一张模糊的全屏画面」：
    /// 全屏画面得靠 RenderTargetBitmap 渲，那是渲染后端相关的（换后端、在后台线程上渲都可能出岔子），
    /// 一旦抛异常整块背景图就没了。模糊整张图是纯 CPU 的，不碰渲染器。
    /// 至于「玻璃里透着模糊、玻璃外是清晰」两者对得上，靠的是绘制时共用同一个
    /// <see cref="BackgroundImageMap.ComputeDestRect"/>，与底图尺寸无关。
    /// </summary>
    private static WriteableBitmap? BakeBlur(Bitmap sharp, double radius)
    {
        var pixels = sharp.PixelSize;
        if (pixels.Width < 2 || pixels.Height < 2) return null;

        var blur = new WriteableBitmap(pixels, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var framebuffer = blur.Lock();
        sharp.CopyPixels(framebuffer, AlphaFormat.Premul);
        BlurFramebuffer(framebuffer, pixels.Width, pixels.Height, (int)Math.Round(radius));
        return blur;
    }

    /// <summary>
    /// 把一张位图的像素倒进连续数组。位图每行可能带对齐 padding，必须逐行拷，
    /// 不能把首地址直接当成 width×height 的连续内存。
    /// </summary>
    private static uint[]? ReadPixels(Bitmap source)
    {
        var size = source.PixelSize;
        if (size.Width < 1 || size.Height < 1) return null;

        // CopyPixels 得先有个锁住的缓冲当落脚点，借一张临时位图用一下
        var staging = new WriteableBitmap(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        try
        {
            using var frame = staging.Lock();
            source.CopyPixels(frame, AlphaFormat.Premul);
            return CopyOut(frame, size);
        }
        finally { staging.Dispose(); }
    }

    private static unsafe uint[] CopyOut(ILockedFramebuffer frame, PixelSize size)
    {
        var stride = frame.RowBytes;
        var origin = (byte*)frame.Address;
        var rowBytes = (long)size.Width * 4;
        var pixels = new uint[size.Width * size.Height];

        fixed (uint* dst = pixels)
        {
            for (var y = 0; y < size.Height; y++)
                Buffer.MemoryCopy(origin + (long)y * stride, dst + (long)y * size.Width, rowBytes, rowBytes);
        }
        return pixels;
    }

    /// <summary>把锁住的缓冲倒进连续数组、模糊、再倒回去（缓冲的每行可能带 padding，不能直接当连续内存用）。</summary>
    private static unsafe void BlurFramebuffer(ILockedFramebuffer framebuffer, int width, int height, int radius)
    {
        if (radius < 1) return;

        var stride = framebuffer.RowBytes;
        var origin = (byte*)framebuffer.Address;
        var rowBytes = (long)width * 4;
        var pixels = new uint[width * height];

        fixed (uint* buffer = pixels)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(origin + (long)y * stride, buffer + (long)y * width, rowBytes, rowBytes);

            StackBlur.Apply(pixels, width, height, radius);

            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(buffer + (long)y * width, origin + (long)y * stride, rowBytes, rowBytes);
        }
    }
}
