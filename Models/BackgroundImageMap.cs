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
