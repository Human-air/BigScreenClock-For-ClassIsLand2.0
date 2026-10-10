namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// Stack Blur —— 用三角形权重 + 滑动窗口逼近高斯模糊，每像素 O(1)，一遍就够（不用多遍叠加）。
/// 移植自 Mario Klingemann 的 Stack Blur 的 C 版实现（<c>blur_ARGB_8888</c>），
/// 来源 https://github.com/781015928/BlurLib 的 <c>Library/src/main/jni/stackblur.c</c>。
///
/// 本项目用它把用户选的背景图烘成一张模糊底图给「磨砂玻璃」档当底：
/// 不能挂 Avalonia 的 <c>BlurEffect</c>，因为那是合成器效果（ClassIsland 有可能是软件渲染后端），
/// 而且每个面板挂一张全屏大小的位图反而更重。CPU 路径与渲染后端无关，产出一张共享底图，
/// 绘制时每块玻璃只是一次普通的贴图。
///
/// 像素格式为 32 位 BGRA（走小端时就是 <c>0xAARRGGBB</c>），与 Avalonia 的 <c>Bgra8888</c> 一致；
/// alpha 通道原样保留。
/// </summary>
public static unsafe class StackBlur
{
    /// <summary>
    /// 就地模糊。<paramref name="pixels"/> 是行优先、连续排布的 BGRA 像素。
    /// </summary>
    /// <param name="radius">模糊半径（像素）。小于 1 直接返回。</param>
    public static void Apply(uint[] pixels, int width, int height, int radius)
    {
        if (radius < 1 || width < 2 || height < 2) return;
        if (pixels.Length < width * height) return;

        fixed (uint* pix = pixels)
            Blur(pix, width, height, radius);
    }

    private static void Blur(uint* pix, int width, int height, int radius)
    {
        var wm = width - 1;
        var hm = height - 1;
        var wh = width * height;

        // 正方形的权重和：div 个像素、三角形权重，总和正好是 divsum
        var div = radius + radius + 1;
        var divsum = (div + 1) >> 1;
        divsum *= divsum;

        // 查表代替除法：dv[加权和] = 加权和 / divsum
        var dv = new short[256 * divsum];
        for (var i = 0; i < 256 * divsum; i++)
            dv[i] = (short)(i / divsum);

        // 横竖两遍的中间结果（每通道 0~255）
        var r = new short[wh];
        var g = new short[wh];
        var b = new short[wh];

        // 环形队列：最多同时装着 div 个像素的三个通道
        var stack = new int[div * 3];

        // 纵向那遍用到的行偏移查表（每列算一次）
        var vmin = new int[Math.Max(width, height)];

        var r1 = radius + 1;
        var yi = 0;   // 当前像素在线性缓冲里的下标
        var yw = 0;   // 当前行行首的下标

        // ===== 横向 =====
        for (var y = 0; y < height; y++)
        {
            int rsum = 0, gsum = 0, bsum = 0;
            int routsum = 0, goutsum = 0, boutsum = 0;
            int rinsum = 0, ginsum = 0, binsum = 0;

            for (var i = -radius; i <= radius; i++)
            {
                // 越界按边缘像素钳制，否则画面边缘会渗出黑边
                var p = pix[yi + Math.Min(wm, Math.Max(i, 0))];
                var sir = (i + radius) * 3;
                stack[sir] = (int)((p & 0xff0000) >> 16);   // R
                stack[sir + 1] = (int)((p & 0x00ff00) >> 8); // G
                stack[sir + 2] = (int)(p & 0x0000ff);        // B

                var rbs = r1 - Math.Abs(i);
                rsum += stack[sir] * rbs;
                gsum += stack[sir + 1] * rbs;
                bsum += stack[sir + 2] * rbs;

                if (i > 0)
                {
                    rinsum += stack[sir];
                    ginsum += stack[sir + 1];
                    binsum += stack[sir + 2];
                }
                else
                {
                    routsum += stack[sir];
                    goutsum += stack[sir + 1];
                    boutsum += stack[sir + 2];
                }
            }

            var stackpointer = radius;

            for (var x = 0; x < width; x++)
            {
                r[yi] = dv[rsum];
                g[yi] = dv[gsum];
                b[yi] = dv[bsum];

                rsum -= routsum;
                gsum -= goutsum;
                bsum -= boutsum;

                var stackstart = stackpointer - radius + div;
                var sir = (stackstart % div) * 3;

                routsum -= stack[sir];
                goutsum -= stack[sir + 1];
                boutsum -= stack[sir + 2];

                if (y == 0)
                    vmin[x] = Math.Min(x + radius + 1, wm);

                var p = pix[yw + vmin[x]];
                stack[sir] = (int)((p & 0xff0000) >> 16);
                stack[sir + 1] = (int)((p & 0x00ff00) >> 8);
                stack[sir + 2] = (int)(p & 0x0000ff);

                rinsum += stack[sir];
                ginsum += stack[sir + 1];
                binsum += stack[sir + 2];

                rsum += rinsum;
                gsum += ginsum;
                bsum += binsum;

                stackpointer = (stackpointer + 1) % div;
                sir = stackpointer * 3;

                routsum += stack[sir];
                goutsum += stack[sir + 1];
                boutsum += stack[sir + 2];

                rinsum -= stack[sir];
                ginsum -= stack[sir + 1];
                binsum -= stack[sir + 2];

                yi++;
            }

            yw += width;
        }

        // ===== 纵向（读上面存下的 r/g/b，直接写回 pix）=====
        for (var x = 0; x < width; x++)
        {
            int rsum = 0, gsum = 0, bsum = 0;
            int routsum = 0, goutsum = 0, boutsum = 0;
            int rinsum = 0, ginsum = 0, binsum = 0;

            var yp = -radius * width;

            for (var i = -radius; i <= radius; i++)
            {
                var yi2 = Math.Max(0, yp) + x;
                var sir = (i + radius) * 3;

                stack[sir] = r[yi2];
                stack[sir + 1] = g[yi2];
                stack[sir + 2] = b[yi2];

                var rbs = r1 - Math.Abs(i);
                rsum += r[yi2] * rbs;
                gsum += g[yi2] * rbs;
                bsum += b[yi2] * rbs;

                if (i > 0)
                {
                    rinsum += stack[sir];
                    ginsum += stack[sir + 1];
                    binsum += stack[sir + 2];
                }
                else
                {
                    routsum += stack[sir];
                    goutsum += stack[sir + 1];
                    boutsum += stack[sir + 2];
                }

                if (i < hm) yp += width;
            }

            yi = x;
            var stackpointer = radius;

            for (var y = 0; y < height; y++)
            {
                // 保留原 alpha（模糊只作用于颜色），其余位换成模糊结果
                var red = (uint)dv[rsum];
                var green = (uint)dv[gsum];
                var blue = (uint)dv[bsum];
                pix[yi] = (pix[yi] & 0xff000000u) | (red << 16) | (green << 8) | blue;

                rsum -= routsum;
                gsum -= goutsum;
                bsum -= boutsum;

                var stackstart = stackpointer - radius + div;
                var sir = (stackstart % div) * 3;

                routsum -= stack[sir];
                goutsum -= stack[sir + 1];
                boutsum -= stack[sir + 2];

                if (x == 0)
                    vmin[y] = Math.Min(y + r1, hm) * width;

                var p = x + vmin[y];
                stack[sir] = r[p];
                stack[sir + 1] = g[p];
                stack[sir + 2] = b[p];

                rinsum += stack[sir];
                ginsum += stack[sir + 1];
                binsum += stack[sir + 2];

                rsum += rinsum;
                gsum += ginsum;
                bsum += binsum;

                stackpointer = (stackpointer + 1) % div;
                sir = stackpointer * 3;

                routsum += stack[sir];
                goutsum += stack[sir + 1];
                boutsum += stack[sir + 2];

                rinsum -= stack[sir];
                ginsum -= stack[sir + 1];
                binsum -= stack[sir + 2];

                yi += width;
            }
        }
    }
}
