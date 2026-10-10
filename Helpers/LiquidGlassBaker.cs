using System;

namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 玻璃背后那块画面的采样器：给层内的物理像素坐标，返回「玻璃背后那一处」的颜色。
///
/// 之所以是个接口而不是直接给一张位图：玻璃背后可能是清晰背景图、可能是「完整显示」
/// 留白处的模糊填充、也可能什么图都没有只有底色，采样器把这三种情形一起收掉。
/// </summary>
public interface IBackdropSampler
{
    /// <summary>取该点的颜色（直线空间，0..1，未预乘）。</summary>
    ColorSample Sample(double x, double y);

    /// <summary>
    /// 取该点<b>模糊后</b>的颜色。玻璃里的磨砂层用它——背后那张花壁纸原样透过去，
    /// 压在上面的字就淹了（苹果那版液态玻璃栽的就是这个）。同一坐标给一份糊的，
    /// 磨砂浓度才有得调。没有模糊底图时实现方可以直接返回 <see cref="Sample"/>。
    /// </summary>
    ColorSample SampleBlurred(double x, double y);
}

/// <summary>一个像素的颜色。直线空间（非预乘），0..1。</summary>
public readonly struct ColorSample
{
    public ColorSample(double r, double g, double b, double a)
    {
        R = r; G = g; B = b; A = a;
    }

    public double R { get; }
    public double G { get; }
    public double B { get; }
    public double A { get; }
}

/// <summary>
/// 一块玻璃的光学参数。全部长度单位是<b>物理像素</b>（调用方已经乘过屏幕缩放倍率）。
/// </summary>
public readonly struct LiquidGlassStyle
{
    /// <summary>折射带的宽度：从边缘往里这么多像素内的画面会被推弯。</summary>
    public double RefractionHeight { get; init; }

    /// <summary>紧贴边缘处的最大位移量。越大边缘越「鼓」。</summary>
    public double RefractionAmount { get; init; }

    /// <summary>色散比例：红蓝两通道的位移相对绿通道各差这么多（0.05 = 差 5%）。</summary>
    public double Dispersion { get; init; }

    /// <summary>镜边高光的带宽。</summary>
    public double HighlightWidth { get; init; }

    /// <summary>高光沿周长的强弱指数：越大，只有正对光的那一小段亮。</summary>
    public double HighlightFalloff { get; init; }

    /// <summary>高光强度。</summary>
    public double HighlightStrength { get; init; }

    /// <summary>光源方向（弧度）。</summary>
    public double LightAngle { get; init; }

    /// <summary>斜面受光的提亮系数。</summary>
    public double BevelLight { get; init; }

    /// <summary>玻璃材料底色（黑暗主题=黑、明亮主题=白）的不透明度 0..1。</summary>
    public double TintAlpha { get; init; }

    /// <summary>材料色是不是白的。亮主题的玻璃是「白玻璃」。 </summary>
    public bool TintIsLight { get; init; }

    /// <summary>
    /// 磨砂浓度 0..1：玻璃里透出的画面在「清晰」与「模糊」之间取哪一档。
    /// 0 = 纯清玻璃（折射最明显，但背后花壁纸会把字淹掉）；1 = 全磨砂（最好读，透镜感最弱）。
    /// </summary>
    public double Frost { get; init; }

    /// <summary>
    /// 斜面上额外追加的散射 0..1。玻璃有厚度，边缘那道斜口本来是散射最重的地方；
    /// 中间留清、边上糊一层，透镜的形状才看得出来（不然整块糊匀了就只是磨砂）。
    /// </summary>
    public double RimFrost { get; init; }
}

/// <summary>
/// 把一块玻璃「算」出来：对玻璃范围内的每个像素，按圆角矩形的距离场把背后的画面
/// 沿法线方向推开一段，贴边的像素推得多、往里迅速收敛到零；红蓝两通道推的量略有差异，
/// 于是在边缘一两像素里透出一丝分色；最后压一层材料色，并在迎光的那段周长上加一道镜边高光。
///
/// 为什么在 CPU 上算：Avalonia 11 没有暴露自定义着色器（公开的 DrawingContext 只有
/// PushClip / PushOpacity 那几样，没有 PushEffect），跑不了像素着色器。好在玻璃只在
/// 「背景换了 / 组件挪了 / 换了主题」这种离散时刻才需要重算，不是每帧，CPU 完全吃得下——
/// 跟已经在用的 StackBlur 是同一套路子。
///
/// 这里不碰任何 Avalonia 类型：给它一个像素数组和一个采样器就能算，所以能被单测直接覆盖。
/// </summary>
public static class LiquidGlassBaker
{
    /// <summary>
    /// 把一块玻璃画进 <paramref name="layer"/>（BGRA8888 预乘，按层从左到右、从上到下排列）。
    ///
    /// 只写玻璃<b>盖住</b>的那些像素，玻璃矩形之外一个字节都不动——调用方先把整层清成透明，
    /// 于是多块玻璃依次画上去自然就叠在一起了。
    /// </summary>
    /// <param name="panelX">玻璃左上角在层里的物理像素坐标。</param>
    public static void RenderPanel(
        uint[] layer, int layerWidth, int layerHeight,
        double panelX, double panelY, double panelWidth, double panelHeight, double radius,
        in LiquidGlassStyle style, IBackdropSampler backdrop)
    {
        if (layer.Length < layerWidth * layerHeight) return;
        if (panelWidth < 2 || panelHeight < 2) return;

        var halfW = panelWidth * 0.5;
        var halfH = panelHeight * 0.5;
        var rr = Math.Clamp(radius, 0, Math.Min(halfW, halfH));

        // 折射带不能比玻璃本身还宽，否则整块都在被推弯、看不出「中间是平的」
        var bandHeight = Math.Clamp(style.RefractionHeight, 0, Math.Min(halfW, halfH) * 0.9);
        var amount = Math.Max(0, style.RefractionAmount);
        var dispK = Math.Clamp(style.Dispersion, 0, 0.4);

        // 梯度用放大了的圆角半径：直边与圆角交界处才不会拐折出一道痕
        var gradRadius = Math.Min(rr * 1.5, Math.Min(halfW, halfH));

        var lightX = Math.Cos(style.LightAngle);
        var lightY = Math.Sin(style.LightAngle);

        var tint = style.TintIsLight ? 1.0 : 0.0;
        var tintAlpha = Math.Clamp(style.TintAlpha, 0, 1);

        var strokeHalf = Math.Max(style.HighlightWidth, 0) * 0.5;
        var sigma = Math.Max(strokeHalf * 0.5, 0.35);
        // 3 抽头的高斯核权重（±σ 与 0），带宽只有一两个像素时足够
        var w0 = 1.0;
        var w1 = Math.Exp(-0.5);
        var sideWeight = w0 + 2 * w1;

        // 只扫玻璃的外接矩形，且裁到层内
        var x0 = Math.Max(0, (int)Math.Floor(panelX));
        var y0 = Math.Max(0, (int)Math.Floor(panelY));
        var x1 = Math.Min(layerWidth - 1, (int)Math.Ceiling(panelX + panelWidth));
        var y1 = Math.Min(layerHeight - 1, (int)Math.Ceiling(panelY + panelHeight));
        if (x1 < x0 || y1 < y0) return;

        for (var y = y0; y <= y1; y++)
        {
            var localY = y + 0.5 - panelY;      // 相对玻璃左上角
            var cy = localY - halfH;            // 相对玻璃中心
            var rowBase = y * layerWidth;

            for (var x = x0; x <= x1; x++)
            {
                var localX = x + 0.5 - panelX;
                var cx = localX - halfW;

                var sd = RoundedRectSdf.RoundedRect(cx, cy, halfW, halfH, rr);
                if (sd > 0.5) continue;         // 圆角外，一个字节都不动

                // 一像素宽的过渡带做抗锯齿
                var coverage = 1 - RoundedRectSdf.SmoothStep(-0.5, 0.5, sd);
                if (coverage <= 0) continue;

                var (gradX, gradY) = RoundedRectSdf.Gradient(cx, cy, halfW, halfH, gradRadius);
                var gradLen = Math.Sqrt(gradX * gradX + gradY * gradY);
                var nx = 0.0;
                var ny = 0.0;
                if (gradLen > 1e-6)
                {
                    nx = gradX / gradLen;
                    ny = gradY / gradLen;
                }

                var depth = -sd;                // 玻璃内为正：越大越靠里
                var inBand = bandHeight > 0.5 && depth < bandHeight;
                var push = 0.0;
                var band = 0.0;
                if (inBand)
                {
                    band = 1 - depth / bandHeight;                      // 贴边 1、带的内沿 0
                    push = RoundedRectSdf.CircleMap(band) * amount;
                }

                // 层内坐标 → 被推弯后的采样坐标。
                // 采样点先按折射推弯、再在这个位置上取清晰或模糊的一档：
                // 透镜的形状体现在「取哪个位置」，磨砂体现在「取的那一档有多糊」，互不干扰。
                var sx = localX + panelX;
                var sy = localY + panelY;
                var tx = sx + nx * push;
                var ty = sy + ny * push;

                // 磨砂浓度：整块按用户的设置，斜面上再加一档。中间清、边上糊，玻璃才像有厚度。
                var frost = style.Frost;
                if (inBand && style.RimFrost > 0.001) frost += (1 - frost) * band * style.RimFrost;
                frost = Math.Clamp(frost, 0, 1);

                double r, g, b, a;
                if (push > 0.01 && dispK > 0.001)
                {
                    // 色散：红推得远一点、蓝推得近一点，贴边一两像素里就有彩边
                    var sr = backdrop.Sample(sx + nx * push * (1 + dispK), sy + ny * push * (1 + dispK));
                    var sg = backdrop.Sample(tx, ty);
                    var sb = backdrop.Sample(sx + nx * push * (1 - dispK), sy + ny * push * (1 - dispK));
                    r = sr.R; g = sg.G; b = sb.B; a = sg.A;
                }
                else if (push > 0.01)
                {
                    var s = backdrop.Sample(tx, ty);
                    r = s.R; g = s.G; b = s.B; a = s.A;
                }
                else
                {
                    var s = backdrop.Sample(sx, sy);
                    r = s.R; g = s.G; b = s.B; a = s.A;
                }

                if (frost > 0.001)
                {
                    // 模糊那一路按同一个坐标取，所以折射位移照样带着它一起偏
                    var blurred = backdrop.SampleBlurred(tx, ty);
                    r += (blurred.R - r) * frost;
                    g += (blurred.G - g) * frost;
                    b += (blurred.B - b) * frost;
                    a += (blurred.A - a) * frost;
                }

                // 材料色：上端厚、往下渐薄（跟另两档玻璃的渐变一致，互相对得上）
                if (tintAlpha > 0.001)
                {
                    var k = tintAlpha * (1.0 - 0.33 * Math.Clamp(localY / panelHeight, 0, 1));
                    r += (tint - r) * k;
                    g += (tint - g) * k;
                    b += (tint - b) * k;
                }

                // 斜面受光：迎光那一侧的隆起被照亮，玻璃立刻有了厚度
                if (inBand && style.BevelLight > 0.001)
                {
                    var lit = Math.Max(0, nx * lightX + ny * lightY);
                    var k = 1 + style.BevelLight * band * lit;
                    r *= k; g *= k; b *= k;
                }

                // 镜边高光：贴着周长的一道细亮线，正对光的那段最亮、背光那段几乎不见。
                // 强度沿周长连续变化是「真玻璃」和「一圈白描边」的分界线。
                if (strokeHalf > 0.4 && style.HighlightStrength > 0.001)
                {
                    var mask = 0.0;
                    for (var i = -1; i <= 1; i++)
                    {
                        var offset = i * sigma;
                        var sampleSd = sd - offset;
                        var hard = Math.Abs(sampleSd) < strokeHalf ? 1.0 : 0.0;
                        mask += hard * (i == 0 ? w0 : w1);
                    }
                    mask = mask / sideWeight * 0.5;   // 只留边界内侧的一半

                    if (mask > 0.001)
                    {
                        var lit = Math.Pow(Math.Abs(nx * lightX + ny * lightY), Math.Max(style.HighlightFalloff, 0.1));
                        var s = mask * lit * style.HighlightStrength;
                        r += s; g += s; b += s;
                    }
                }

                // 装进预乘 BGRA
                var alpha = coverage * Math.Clamp(a, 0, 1);
                if (alpha <= 0) continue;

                layer[rowBase + x] = Pack(r * alpha, g * alpha, b * alpha, alpha);
            }
        }
    }

    private static uint Pack(double r, double g, double b, double a)
    {
        var rb = (uint)Math.Clamp(Math.Round(r * 255), 0, 255);
        var gb = (uint)Math.Clamp(Math.Round(g * 255), 0, 255);
        var bb = (uint)Math.Clamp(Math.Round(b * 255), 0, 255);
        var ab = (uint)Math.Clamp(Math.Round(a * 255), 0, 255);
        return bb | (gb << 8) | (rb << 16) | (ab << 24);   // 小端：字节序是 B G R A
    }
}
