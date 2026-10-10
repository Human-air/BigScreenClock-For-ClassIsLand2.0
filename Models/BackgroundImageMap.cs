using Avalonia;

namespace EveningSelfStudyClock.Models;

/// <summary>
/// 背景图片的填充方式，取值与 <see cref="PluginSettings.BackgroundImageStretch"/> 的整数值一致。
/// </summary>
public enum BackgroundImageStretch
{
    /// <summary>铺满整屏、多余部分裁掉（相当于 CSS 的 cover / Avalonia 的 UniformToFill）。</summary>
    FillCrop = 0,

    /// <summary>整张图都看得见，多出来的两侧或上下留白（相当于 contain / Uniform）。</summary>
    Fit = 1,

    /// <summary>拉伸到整屏，长宽比不保（相当于 fill）。</summary>
    Stretch = 2,
}

/// <summary>
/// 背景图片在视口里的落位换算。**纯函数，无副作用**——清晰背景与玻璃底下那张模糊底图
/// 必须共用同一套映射，玻璃里透出的画面才能和玻璃外严丝合缝地对上。
/// 这也是这个文件被单测覆盖的原因：对不齐是「磨砂玻璃」这个卖点唯一的致命伤。
/// </summary>
public static class BackgroundImageMap
{
    /// <summary>
    /// 底图最多解到这一屏用得上的像素的多少倍。稍微留一点余量，是为了「比屏幕略宽/略高」的图
    /// 别在解码时就被裁掉边角上的锐度；再多就是白占内存了（超出的部分绘制时本来就被裁掉）。
    /// </summary>
    private const double PixelBudgetFactor = 1.25;

    /// <summary>
    /// 整张图应该落在视口坐标里的哪个矩形。结果可以超出视口（铺满裁切时就是超出的），
    /// 由绘制方自己裁剪。
    /// </summary>
    /// <param name="imageSize">图片的像素尺寸。</param>
    /// <param name="viewportSize">视口尺寸（清晰那遍是窗口尺寸，模糊那遍是底图尺寸）。</param>
    /// <param name="stretch">见 <see cref="BackgroundImageStretch"/>。</param>
    public static Rect ComputeDestRect(PixelSize imageSize, Size viewportSize, int stretch)
    {
        if (imageSize.Width <= 0 || imageSize.Height <= 0 ||
            viewportSize.Width <= 0 || viewportSize.Height <= 0)
            return default;

        var iw = (double)imageSize.Width;
        var ih = (double)imageSize.Height;
        var vw = viewportSize.Width;
        var vh = viewportSize.Height;

        var scaleX = vw / iw;
        var scaleY = vh / ih;

        var (sx, sy) = (BackgroundImageStretch)stretch switch
        {
            BackgroundImageStretch.Fit => Both(scaleX, scaleY, Math.Min),
            BackgroundImageStretch.Stretch => (scaleX, scaleY),
            _ => Both(scaleX, scaleY, Math.Max),   // 铺满裁切：取大的那个比例，另一维溢出被裁
        };

        var w = iw * sx;
        var h = ih * sy;
        return new Rect((vw - w) / 2, (vh - h) / 2, w, h);

        static (double, double) Both(double x, double y, Func<double, double, double> pick)
        {
            var s = pick(x, y);
            return (s, s);
        }
    }

    /// <summary>
    /// 视口里的某个矩形（比如某块玻璃所在的区域）→ <paramref name="imageSize"/> 这张图上的像素矩形。
    /// 「完整显示」留白处会算出图片外部的坐标，这是预期的——绘制方那边本来就有底色垫着，
    /// 采样时按边缘钳制即可。
    /// </summary>
    /// <param name="viewportRect">要换算的矩形，坐标相对 <paramref name="viewportSize"/>。</param>
    /// <param name="imageSize">图片的像素尺寸。</param>
    /// <param name="viewportSize">视口尺寸，必须与算 dest 矩形时用的同一个。</param>
    /// <param name="stretch">见 <see cref="BackgroundImageStretch"/>。</param>
    public static Rect MapToImage(Rect viewportRect, PixelSize imageSize, Size viewportSize, int stretch)
    {
        var dest = ComputeDestRect(imageSize, viewportSize, stretch);
        if (dest.Width <= 0 || dest.Height <= 0) return default;

        var kx = imageSize.Width / dest.Width;
        var ky = imageSize.Height / dest.Height;

        return new Rect(
            (viewportRect.X - dest.X) * kx,
            (viewportRect.Y - dest.Y) * ky,
            viewportRect.Width * kx,
            viewportRect.Height * ky);
    }

    /// <summary>
    /// 这张底图该解成多大的像素。两条规矩：
    ///
    /// 1. **绝不放大**。解码器不管原图多大，给多宽就解多宽 —— 一张 1600×1600 的壁纸给它 2560
    ///    就解出 2560×2560，多花一倍半内存，而且先在解码时插一遍值、绘制时又插一遍，比直接
    ///    拿原图放大还糊。原图比要的地方小，就按原样解，放大交给绘制那一步。
    /// 2. 极端长条（超宽全景 / 超长截图）按规矩 1 解出来还是可能几倍于这一屏用得上的像素，
    ///    再按像素预算压一档。宁可稍微糊一点，也不要把 CI 的内存看门狗顶爆。
    /// </summary>
    /// <param name="nativeSize">原图像素尺寸（见 <see cref="ImageHeader"/>）。</param>
    /// <param name="boxSize">这一层落屏要多少物理像素。</param>
    /// <param name="stretch">见 <see cref="BackgroundImageStretch"/>。</param>
    public static PixelSize DecodeSize(PixelSize nativeSize, PixelSize boxSize, int stretch)
    {
        if (nativeSize.Width <= 0 || nativeSize.Height <= 0) return boxSize;
        if (boxSize.Width <= 0 || boxSize.Height <= 0) return nativeSize;

        var sx = boxSize.Width / (double)nativeSize.Width;
        var sy = boxSize.Height / (double)nativeSize.Height;

        var needed = (BackgroundImageStretch)stretch switch
        {
            BackgroundImageStretch.Fit => Math.Min(sx, sy),       // 整张都看得见：按小的那维缩
            BackgroundImageStretch.Stretch => sx,                 // 拉伸档按宽度走，纵向本来就变形
            _ => Math.Max(sx, sy),                                // 铺满裁切：按大的那维缩，另一维溢出
        };

        var t = Math.Min(1.0, needed);                            // 只缩不放
        var w = Math.Max(1, (int)Math.Round(nativeSize.Width * t));
        var h = Math.Max(1, (int)Math.Round(nativeSize.Height * t));

        var budget = (long)(boxSize.Width * boxSize.Height * PixelBudgetFactor);
        if ((long)w * h > budget)
        {
            var k = Math.Sqrt((double)budget / ((long)w * h));
            w = Math.Max(1, (int)Math.Round(w * k));
            h = Math.Max(1, (int)Math.Round(h * k));
        }

        return new PixelSize(w, h);
    }

    /// <summary>
    /// 把视口坐标里的矩形按比例缩到另一套视口尺度上（模糊底图是窗口的若干分之一，用它换算）。
    /// </summary>
    public static Rect ScaleTo(Rect viewportRect, Size fromViewport, Size toViewport)
    {
        if (fromViewport.Width <= 0 || fromViewport.Height <= 0) return default;

        var kx = toViewport.Width / fromViewport.Width;
        var ky = toViewport.Height / fromViewport.Height;
        return new Rect(viewportRect.X * kx, viewportRect.Y * ky,
                        viewportRect.Width * kx, viewportRect.Height * ky);
    }
}
