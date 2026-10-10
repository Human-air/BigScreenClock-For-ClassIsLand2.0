using System;

namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 圆角矩形的有符号距离场（SDF）与它的梯度。纯函数，没有状态。
///
/// 距离场是「高光折射」玻璃的地基：折射的方向就是距离场的梯度方向——光在玻璃边缘
/// 一定是朝远离边界的法线方向被推弯的，用梯度算比手写「上边朝上、左边朝左」的分段
/// 判断要稳得多，圆角过渡处也不会突然拐折。
///
/// 约定与图形学常规一致：<b>内部为负、边界为零、外部为正</b>。像素中心落在一像素宽的
/// 过渡带里时用它做抗锯齿。
/// </summary>
public static class RoundedRectSdf
{
    /// <summary>
    /// 点 (<paramref name="x"/>, <paramref name="y"/>) 到圆角矩形边界的有符号距离。
    /// 坐标以矩形中心为原点，<paramref name="halfW"/> / <paramref name="halfH"/> 是半宽半高，
    /// <paramref name="radius"/> 是圆角半径。
    /// </summary>
    public static double RoundedRect(double x, double y, double halfW, double halfH, double radius)
    {
        // 半宽半高为 0 或负（控件还没排布）当作一个点，别让后面的除法出 NaN
        halfW = Math.Max(halfW, 0);
        halfH = Math.Max(halfH, 0);
        radius = Math.Clamp(radius, 0, Math.Min(halfW, halfH));

        // 把直角外侧的那块切掉：到「缩进 radius 的矩形」的距离，再减去 radius，
        // 就是圆角。这是圆角矩形 SDF 的标准写法。
        var qx = Math.Abs(x) - (halfW - radius);
        var qy = Math.Abs(y) - (halfH - radius);

        var outside = Math.Sqrt(Math.Max(qx, 0) * Math.Max(qx, 0) + Math.Max(qy, 0) * Math.Max(qy, 0)) - radius;
        var inside = Math.Min(Math.Max(qx, qy), 0);
        return outside + inside;
    }

    /// <summary>
    /// 距离场在该点的梯度（未归一化，方向朝外）。
    ///
    /// 直边段返回的是垂直于该边的轴向单位向量；圆角段返回的是从圆心指出去的方向。
    /// 刻意用 <paramref name="gradRadius"/> 而不是真实圆角半径：把「圆角」在梯度里放大，
    /// 可以让直边与圆角的交界处平滑过渡。用真实半径的话，那个交界点上的梯度方向会
    /// 突然从轴向跳到 45°，折射出来会看到一道折痕。
    /// </summary>
    public static (double X, double Y) Gradient(double x, double y, double halfW, double halfH, double gradRadius)
    {
        halfW = Math.Max(halfW, 0);
        halfH = Math.Max(halfH, 0);
        gradRadius = Math.Clamp(gradRadius, 0, Math.Min(halfW, halfH));

        var qx = Math.Abs(x) - (halfW - gradRadius);
        var qy = Math.Abs(y) - (halfH - gradRadius);

        if (qx >= 0 || qy >= 0)
        {
            // 圆角区（或矩形外）：朝远离圆心方向的 45°~轴向向量
            var vx = Math.Max(qx, 0);
            var vy = Math.Max(qy, 0);
            var len = Math.Sqrt(vx * vx + vy * vy);
            if (len < 1e-6) return (0, 0);   // 正好压在圆心的退化情形
            return (Math.Sign(x) * vx / len, Math.Sign(y) * vy / len);
        }

        // 直边区：取「离得最近的那条边」的法线。x 更靠边就走横向，否则纵向。
        // qx >= qy 说明横向离边更近。乘上象限符号，方向才是朝外的。
        var horizontal = qx >= qy ? 1 : 0;
        return (Math.Sign(x) * horizontal, Math.Sign(y) * (1 - horizontal));
    }

    /// <summary>
    /// 圆弧映射：把线性深度 0..1 压成「贴边陡、往里趋平」的曲线，正是玻璃边缘隆起的样子。
    /// x=1（贴边）时为 1，x=0（带的内沿）时为 0。
    ///
    /// 直接用线性深度会得到一条等宽的位移带，看着像贴在边上的一圈描边；圆弧映射才是透镜。
    /// </summary>
    public static double CircleMap(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return 1 - Math.Sqrt(1 - x * x);
    }

    /// <summary>标准 smoothstep：<paramref name="edge0"/> 处返回 0，<paramref name="edge1"/> 处返回 1。</summary>
    public static double SmoothStep(double edge0, double edge1, double x)
    {
        if (Math.Abs(edge1 - edge0) < 1e-9) return x < edge0 ? 0 : 1;
        var t = Math.Clamp((x - edge0) / (edge1 - edge0), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
