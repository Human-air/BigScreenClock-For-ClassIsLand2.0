namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 给采样器整体挪一个位移。
///
/// 玻璃改成「一块一张小位图」之后，烘一块时用的画布不再是整层，而是这块自己的外接框。
/// 于是 <see cref="LiquidGlassBaker.RenderPanel"/> 拿到的坐标是「相对这块左上角」的，
/// 而背后那张背景图是按**层**坐标铺的 —— 中间差的就是这（<c>Px, Py</c>）。
///
/// 关键在于：偏移必须是**整数设备像素**，玻璃左上角落在这块缓冲里的小数部分则由
/// <c>panelX = 矩形X × 倍率 − Px</c> 交给烘焙那边去处理。两处都算对了，折射出来的图案
/// 才和整层一张烘出来的**逐像素**一致（这块专门由 LiquidGlassTests 里的等价性用例钉着）。
///
/// 钳制一概不在这里做：贴边折射会把采样坐标推出块外，那本来就该按整层的边界去钳
/// （<see cref="IBackdropSampler"/> 的实现方负责），在这里钳就成了「按块的边界拉长边缘像素」，
/// 玻璃边上会显出一道假的接缝。
/// </summary>
public sealed class OffsetSampler : IBackdropSampler
{
    private readonly IBackdropSampler _inner;
    private readonly double _dx;
    private readonly double _dy;

    public OffsetSampler(IBackdropSampler inner, double dx, double dy)
    {
        _inner = inner;
        _dx = dx;
        _dy = dy;
    }

    public ColorSample Sample(double x, double y) => _inner.Sample(x + _dx, y + _dy);

    public ColorSample SampleBlurred(double x, double y) => _inner.SampleBlurred(x + _dx, y + _dy);
}
