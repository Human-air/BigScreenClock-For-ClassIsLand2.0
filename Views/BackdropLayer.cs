using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Views;

/// <summary>一块玻璃垫在哪：给某个控件的实测矩形，四周各外放一圈。</summary>
public sealed class GlassTarget
{
    /// <summary>玻璃垫在这个控件的实测矩形上（控件本身不进布局，玻璃是从底下一层画的）。</summary>
    public required Visual Visual { get; init; }

    /// <summary>左右各外放多少。相邻两块玻璃的间距 = 它们之间原本的间距 − 2×这个值。</summary>
    public double InsetX { get; init; } = 12;

    /// <summary>上下各外放多少。</summary>
    public double InsetY { get; init; } = 6;

    /// <summary>圆角。</summary>
    public double Radius { get; init; } = 12;
}

/// <summary>
/// 背景层：铺自定义背景图 + 明暗遮罩，并在各组件底下垫一块玻璃底。
///
/// 为什么是一整层而不是给每个组件套一个 Border：
/// 「磨砂」档要在玻璃里透出背后那块**模糊后**的画面。模糊底图是由清晰图整张烘出来的，
/// 玻璃只是按自己的位置从上面裁一块。裁的位置和底下那张清晰背景图必须用同一套映射算，
/// 一丁点不一致就会「玻璃里的画面和玻璃外的对不上」——那是这个效果最致命的破绽。
/// 让同一个 Render 里、用同一份 <see cref="BackgroundImageMap"/> 画清晰图和裁玻璃，
/// 对齐就是结构上成立的，不靠调参数凑。
///
/// 另一个好处：玻璃层完全不参与布局。组件的 Margin / 实测尺寸一个字节都没动，
/// 所以 <c>FullScreenClockWindow.ApplyBodyPositions()</c> 那套靠实测高度算位置的逻辑、
/// 以及往设置里写回百分比的那套限位，全都不受影响。
/// </summary>
public sealed class BackdropLayer : Control
{
    private string _imagePath = "";
    private int _stretch;
    private double _dim;
    private int _glassMode = (int)GlassMode.Off;
    private double _glassOpacity = 35;
    private double _blurRadius = 24;
    private double _refraction = 55;
    private double _frost = 45;
    private Color _themeBg = Colors.Black;

    private ClockBrushes _chrome = ClockBrushes.From(ClockTheme.Dark);
    private IBrush _glassFill = Brushes.Transparent;
    private IPen _glassPen = new Pen(Brushes.Transparent, 1);
    private BoxShadows _glassShadows;
    private IBrush _themeBgBrush = Brushes.Black;   // 留白处的底色
    private IBrush? _veilBrush;                     // 明暗遮罩（|dim| < 0.5 时为 null）

    private Bitmap? _sharp;          // 清晰背景图（按窗口的物理像素宽解码，不把 4K 原图整张解进内存）
    private WriteableBitmap? _blur;  // 同一张图的模糊副本（磨砂档用），尺寸与 _sharp 一模一样

    // 清晰图的像素快照。折射要用它在 CPU 上逐像素采样，而 Bitmap 每次读都得先 Lock 一遍，
    // 所以在烘图的时候顺手倒出来一份，之后重算玻璃就不用再碰那张位图了。
    private uint[]? _sharpPixels;
    private PixelSize _sharpPixelsSize;
    private uint[]? _blurPixels;     // 模糊底图的像素快照，磨砂那一档用

    // 「真•◯◯玻璃」那张整层的高光折射图。只在背景/组件位置/主题这些**离散**时刻重算。
    private WriteableBitmap? _sheet;
    private bool _sheetDirty = true;
    private bool _sheetBusy;
    private DispatcherTimer? _sheetTimer;
    private string _builtPath = "";  // 手上这张底图是按哪个输入烘出来的：换了输入才重烘
    private double _builtRadius = -1;
    private int _builtPixelWidth = -1;   // 烘的时候按多宽解的（屏幕缩放变了要重来）
    private bool? _builtWantBlur;        // 上次烘的时候要不要模糊底图（换了要重来）
    private bool _needsRebuild;
    private bool _busy;
    private GlassRect[] _rects = Array.Empty<GlassRect>();

    private EventHandler? _layoutUpdatedHandler;

    public BackdropLayer()
    {
        ClipToBounds = true;
        IsHitTestVisible = false;   // 纯背景层，别把按钮/悬停的鼠标事件吃掉
        // 图按 1:1 画到物理像素上，插值方式其实用不上；万一哪天真缩放了，
        // 也让它是高质量缩放而不是最近邻（默认值不保证）
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
        RebuildBrushes();
    }

    /// <summary>
    /// 屏幕缩放倍率。解码背景图必须按**物理像素**来：屏幕开 125% / 150% 时逻辑宽度只有
    /// 1536 而实际要画 1920 个物理像素，按逻辑宽度解码就是拿小图放大，整张背景发糊。
    /// </summary>
    private double RenderScale => TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;

    /// <summary>要垫玻璃的组件清单（窗口那边填）。</summary>
    public IReadOnlyList<GlassTarget> GlassTargets { get; set; } = Array.Empty<GlassTarget>();

    /// <summary>整层圆角（设置页预览那块圆角卡片用；大屏是全屏，保持 0）。</summary>
    public CornerRadius CornerRadius { get; set; }

    /// <summary>换主题：玻璃的底色与描边跟着亮/暗翻转（底色是画的时候现取的，不用重烘位图）。</summary>
    public void SetPalette(ClockBrushes chrome, Color themeBackground)
    {
        _chrome = chrome;
        _themeBg = themeBackground;
        RebuildBrushes();
        MarkSheetDirty();
        InvalidateVisual();
    }

    /// <summary>套用设置。只有「换图」和「磨砂半径」动到那张烘好的位图，其余都是画的时候现算的。</summary>
    public void Apply(string? imagePath, int stretch, double dim, int glassMode, double glassOpacity,
                      double blurRadius, double refraction, double frost)
    {
        imagePath ??= "";

        // 模糊底图三个用途：磨砂玻璃里透出的那片、「完整显示」两侧的虚化填充，
        // 以及高光折射里那层可调的磨砂（背后那张花壁纸原样透过去，压在上面的字就淹了）。
        // 三者都不用时（半透明 / 关闭），烘它纯属浪费。
        var wantBlur = glassMode is (int)GlassMode.Frosted or (int)GlassMode.Liquid
                       || stretch == (int)BackgroundImageStretch.Fit;

        // 只有「换图」「模糊半径变了」「要从烘变不烘（或反过来）」才需要重烘位图：
        // 明暗遮罩是画的时候现算的（没烘进底图里），拖那个滑块不该触发解码 + 模糊。
        var rebuild =
            imagePath != _builtPath ||
            wantBlur != (_builtWantBlur ?? false) ||
            (wantBlur && Math.Abs(blurRadius - _builtRadius) > 0.4);

        var restyle = glassMode != _glassMode
                      || Math.Abs(glassOpacity - _glassOpacity) > 0.01
                      || Math.Abs(refraction - _refraction) > 0.01
                      || Math.Abs(frost - _frost) > 0.01
                      || stretch != _stretch;

        _imagePath = imagePath;
        _stretch = stretch;
        _dim = dim;
        _glassMode = glassMode;
        _glassOpacity = glassOpacity;
        _blurRadius = blurRadius;
        _refraction = refraction;
        _frost = frost;

        RebuildBrushes();
        if (restyle) MarkSheetDirty();
        if (rebuild) RequestRebuild();
        InvalidateVisual();
    }

    /// <summary>组件被拖动/内容变化后，玻璃要跟着走。窗口那边在布局变化的钩子里调。</summary>
    public void InvalidateGlass()
    {
        _sheetDirty = true;
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _layoutUpdatedHandler ??= (_, _) => OnLayoutUpdated();
        LayoutUpdated += _layoutUpdatedHandler;
        RequestRebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_layoutUpdatedHandler is { } handler) LayoutUpdated -= handler;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        // 尺寸变了：清晰图要按新宽度重解，模糊底图要重烘。
        // 这里显式传 finalSize 而不是走 Bounds —— 排布过程中 Bounds 还是上一轮的旧值，
        // 拿旧尺寸烘出来的底图会让玻璃里的画面整体错位。
        // 顺带比一下物理像素宽：窗口被拖到另一块缩放倍率不同的显示器上时，逻辑尺寸可能没变，
        // 但要解码的像素宽变了（不解就是糊的）。
        if (_sharpSize != finalSize || _builtPixelWidth != PixelWidthFor(finalSize))
        {
            _needsRebuild = true;
            TryStartRebuild(finalSize);
        }
        if (_sheetSize != finalSize)
        {
            _sheetSize = finalSize;
            MarkSheetDirty();
        }
        return result;
    }

    private Size _sharpSize;
    private Size _sheetSize;

    /// <summary>这个逻辑尺寸对应要解码多少物理像素。</summary>
    private int PixelWidthFor(Size size) => Math.Max(64, (int)Math.Round(size.Width * RenderScale));

    /// <summary>每次布局跑完后比一比：玻璃所在的矩形动了就重画（Avalonia 是保留式渲染，别人动了不会叫醒这一层）。</summary>
    private void OnLayoutUpdated()
    {
        if (_needsRebuild) TryStartRebuild(Bounds.Size);
        if (_glassMode == (int)GlassMode.Off) return;

        var rects = ComputeGlassRects(Bounds.Size);
        if (!SameRects(rects, _rects))
        {
            _rects = rects;
            MarkSheetDirty();       // 组件挪了/隐了，折射图上的那块也得跟着挪
            InvalidateVisual();
        }

        TryStartSheet();
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;

        if (_needsRebuild) TryStartRebuild(size);
        if (_sheetDirty) TryStartSheet();

        if (CornerRadius == default)
        {
            RenderCore(context, size);
            return;
        }

        using (context.PushClip(new RoundedRect(new Rect(size), CornerRadius)))
            RenderCore(context, size);
    }

    private void RenderCore(DrawingContext context, Size size)
    {
        // 兜底底色：图还没解出来、或读不出来时，整屏就是它
        if (_sharp is not null)
            context.FillRectangle(_themeBgBrush, new Rect(size));

        if (_sharp is { } image)
        {
            // 「完整显示」上下/左右会空出一块。空着一条纯色不好看，改成把同一张图放大到铺满、
            // 再糊掉垫在底下 —— 壁纸播放器里那种两侧虚化填充，接缝自然。
            if (_stretch == (int)BackgroundImageStretch.Fit && _blur is { } fill)
            {
                var cover = BackgroundImageMap.ComputeDestRect(
                    fill.PixelSize, size, (int)BackgroundImageStretch.FillCrop);
                if (cover.Width > 1 && cover.Height > 1)
                    context.DrawImage(fill, new Rect(ToSize(fill.PixelSize)), cover);
            }

            var dest = BackgroundImageMap.ComputeDestRect(image.PixelSize, size, _stretch);
            if (dest.Width > 1 && dest.Height > 1)
                context.DrawImage(image, new Rect(ToSize(image.PixelSize)), dest);
        }

        // 明暗遮罩：整层压暗/提亮，保证背景图再花字也读得清
        var veil = _veilBrush;
        if (veil is not null)
            context.FillRectangle(veil, new Rect(size));

        if (_glassMode == (int)GlassMode.Off) return;

        var frosted = _glassMode == (int)GlassMode.Frosted;
        var liquid = _glassMode == (int)GlassMode.Liquid;
        var rects = _rects.Length > 0 ? _rects : ComputeGlassRects(size);

        // 「真•高光折射」：整层折射图一次性画上去（每块玻璃的像素已经算好在那儿了）。
        // 底下那张清晰图不用先擦掉——折射图在玻璃范围内是不透明的，正好把它盖住。
        if (liquid && _sheet is { } sheet)
            context.DrawImage(sheet, new Rect(ToSize(sheet.PixelSize)), new Rect(size));

        foreach (var g in rects)
        {
            var rr = new RoundedRect(g.Rect, g.Radius);

            // 磨砂档：玻璃里透出的就是「整张模糊背景按同一套映射铺开、被这块裁掉」的那片，
            // 与玻璃外那张清晰图走的是同一个 ComputeDestRect —— 里外必然严丝合缝。
            if (frosted && _blur is { } blur)
                DrawBlurredBackdrop(context, blur, g.Rect, rr, size, veil);

            // 底色渐变 + 1px 亮边 + 外投影/内亮边（后者是「玻璃厚度感」的来源）。
            // 折射档的底色已经烘进折射图里了，这里只补描边和投影，别再叠一层渐变把折射糊掉。
            if (liquid)
                context.DrawRectangle(null, _glassPen, rr, _glassShadows);
            else
                context.DrawRectangle(_glassFill, _glassPen, rr, _glassShadows);
        }
    }

    /// <summary>
    /// 往一块玻璃里画它背后的那片模糊背景。
    ///
    /// 三层的顺序必须与玻璃外一致（底色 → 图 → 遮罩），否则玻璃里外会差一档亮度：
    /// 上面那张模糊图会把整层遮罩盖掉，所以遮罩要在玻璃里重画一遍。
    /// 只画与图片相交的那部分——「完整显示」档下玻璃盖到留白处时，那片就该是纯底色。
    /// </summary>
    private void DrawBlurredBackdrop(DrawingContext context, WriteableBitmap blur, Rect glass, RoundedRect clip,
                                     Size size, IBrush? veil)
    {
        using (context.PushClip(clip))
        {
            context.FillRectangle(_themeBgBrush, glass);

            var src = BackgroundImageMap.MapToImage(glass, blur.PixelSize, size, _stretch);
            var visible = src.Intersect(new Rect(ToSize(blur.PixelSize)));
            if (visible.Width > 0.5 && visible.Height > 0.5 && src.Width > 0 && src.Height > 0)
            {
                var kx = glass.Width / src.Width;
                var ky = glass.Height / src.Height;
                var dest = new Rect(glass.X + (visible.X - src.X) * kx,
                                    glass.Y + (visible.Y - src.Y) * ky,
                                    visible.Width * kx, visible.Height * ky);
                context.DrawImage(blur, visible, dest);
            }

            if (veil is not null) context.FillRectangle(veil, glass);
        }
    }

    /// <summary>玻璃的落位（层内坐标）：控件的实测矩形四周各外放一圈。</summary>
    private GlassRect[] ComputeGlassRects(Size layerSize)
    {
        var targets = GlassTargets;
        if (targets.Count == 0) return Array.Empty<GlassRect>();

        var list = new List<GlassRect>(targets.Count);
        foreach (var t in targets)
        {
            var v = t.Visual;
            if (!v.IsEffectivelyVisible) continue;

            var b = v.Bounds;
            if (b.Width <= 0.5 || b.Height <= 0.5) continue;

            var p = v.TranslatePoint(default, this);
            if (p is not { } origin) continue;

            var rect = new Rect(origin.X - t.InsetX, origin.Y - t.InsetY,
                                b.Width + t.InsetX * 2, b.Height + t.InsetY * 2);

            // 贴边的块别把玻璃画到层外去（投影本来就允许溢出，这里只管主体）
            rect = rect.Intersect(new Rect(layerSize));
            if (rect.Width <= 1 || rect.Height <= 1) continue;

            list.Add(new GlassRect(rect, t.Radius));
        }
        return list.ToArray();
    }

    private static bool SameRects(GlassRect[] a, GlassRect[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i].Rect.X - b[i].Rect.X) > 0.5 ||
                Math.Abs(a[i].Rect.Y - b[i].Rect.Y) > 0.5 ||
                Math.Abs(a[i].Rect.Width - b[i].Rect.Width) > 0.5 ||
                Math.Abs(a[i].Rect.Height - b[i].Rect.Height) > 0.5 ||
                Math.Abs(a[i].Radius - b[i].Radius) > 0.01)
                return false;
        }
        return true;
    }

    private readonly record struct GlassRect(Rect Rect, double Radius);

    private static Size ToSize(PixelSize size) => new(size.Width, size.Height);

    // ===== 画刷 =====

    private void RebuildBrushes()
    {
        var tint = _chrome.IsLight ? Colors.White : Colors.Black;
        var alpha = Math.Clamp(_glassOpacity, 0, 100) / 100.0;
        byte A(double k) => (byte)Math.Round(alpha * k * 255);

        // 竖向渐变：上端最实、往下渐淡，照 LFG 那套液态玻璃的层次
        var fill = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(A(1.00), tint.R, tint.G, tint.B), 0),
                new GradientStop(Color.FromArgb(A(0.75), tint.R, tint.G, tint.B), 0.55),
                new GradientStop(Color.FromArgb(A(0.67), tint.R, tint.G, tint.B), 1),
            },
        };

        // 1px 亮边：上端亮、中间最淡、下端稍回一点，看着才像一块有厚度的玻璃
        var edge = _chrome.IsLight ? Colors.Black : Colors.White;
        var pen = new Pen(new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x40, edge.R, edge.G, edge.B), 0),
                new GradientStop(Color.FromArgb(0x10, edge.R, edge.G, edge.B), 0.5),
                new GradientStop(Color.FromArgb(0x25, edge.R, edge.G, edge.B), 1),
            },
        }, 1);

        _glassFill = fill;
        _glassPen = pen;
        // 外投影把玻璃从背景里托起来，内亮边做出「玻璃边缘有一线光」的厚度感
        _glassShadows = BoxShadows.Parse(_chrome.IsLight
            ? "0 2 16 0 #28000000, 0 0 1 0 #1F000000 inset"
            : "0 2 16 0 #30000000, 0 0 1 0 #18FFFFFF inset");

        // 留白处（「完整显示」档的上下/左右空档）垫的底色
        _themeBgBrush = new SolidColorBrush(_themeBg);

        // 明暗遮罩：整层压暗（负）或提亮（正）。拖动时每帧都会重建，一次分配无所谓。
        _veilBrush = Math.Abs(_dim) < 0.5
            ? null
            : new SolidColorBrush(_dim < 0
                ? Color.FromArgb((byte)Math.Round(Math.Abs(_dim) / 100.0 * 255), 0, 0, 0)
                : Color.FromArgb((byte)Math.Round(Math.Abs(_dim) / 100.0 * 255), 255, 255, 255));
    }

    // ===== 「真•高光折射」那张图 =====

    /// <summary>
    /// 标脏。组件挪位、换主题、换背景、拖滑块都会调到这里，所以挂了个防抖：
    /// 拖时钟位置滑块时一帧一次的概率很高，攒 120 毫秒再算，免得白算十几遍中间态。
    /// </summary>
    private void MarkSheetDirty()
    {
        _sheetDirty = true;
        if (Bounds.Width < 2) return;

        _sheetTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        if (!_sheetTimer.IsEnabled)
        {
            _sheetTimer.Tick -= OnSheetTimerTick;
            _sheetTimer.Tick += OnSheetTimerTick;
        }
        _sheetTimer.Stop();
        _sheetTimer.Start();
    }

    private void OnSheetTimerTick(object? sender, EventArgs e)
    {
        _sheetTimer?.Stop();
        TryStartSheet();
    }

    /// <summary>
    /// 按当前设置算一遍全部玻璃的高光折射，烘成整层一张图。
    ///
    /// 这张图不由渲染器参与：纯 CPU 逐像素算，跟烘模糊底图是同一套路，所以后台线程上跑也没问题。
    /// 只有「背景换了 / 组件挪了 / 换主题 / 拖了参数」这些**离散**时刻才重算，不是每帧，
    /// 教室里那些弱机也扛得住。
    /// </summary>
    private void TryStartSheet()
    {
        if (_sheetBusy || !_sheetDirty) return;
        if (_glassMode != (int)GlassMode.Liquid) return;

        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;

        var rects = _rects.Length > 0 ? _rects : ComputeGlassRects(size);
        if (rects.Length == 0) return;

        var scale = RenderScale;
        var layerW = Math.Max(2, (int)Math.Round(size.Width * scale));
        var layerH = Math.Max(2, (int)Math.Round(size.Height * scale));
        if ((long)layerW * layerH > 40_000_000) return;   // 大到离谱（多屏拼出的巨桌面）就不做了

        // 快照：算的这段时间里设置可能又变，变了就让结果作废重来（_sheetDirty 会再置上）
        var sharpPixels = _sharpPixels;
        var sharpPixelsSize = _sharpPixelsSize;
        var blurPixels = _blurPixels;
        var blurImageSize = _blur?.PixelSize ?? default;
        var blur = _blur;
        var stretch = _stretch;
        var style = BuildStyle(scale);
        var veilA = Math.Abs(_dim) < 0.5 ? 0.0 : Math.Min(1.0, Math.Abs(_dim) / 100.0);
        var veilColor = _dim < 0 ? Colors.Black : Colors.White;
        var themeBg = _themeBg;
        var radiusOf = rects;

        _sheetDirty = false;
        _sheetBusy = true;

        Task.Run(() =>
        {
            WriteableBitmap? sheet = null;
            try
            {
                var device = new Size(layerW, layerH);
                var layer = new uint[layerW * layerH];

                // 玻璃背后那块画面：清晰图（「完整显示」档铺不满时，留白处退回模糊放大填充）＋整层明暗遮罩。
                // 折射采样必须走跟外面那张清晰图**同一套** ComputeDestRect，里外才能严丝合缝。
                var backdrop = new LayerBackdrop
                {
                    Sharp = sharpPixels,
                    SharpSize = sharpPixelsSize,
                    SharpDest = sharpPixels is null
                        ? default
                        : BackgroundImageMap.ComputeDestRect(sharpPixelsSize, device, stretch),
                    Blur = blurPixels,
                    BlurSize = blurImageSize,
                    BlurDest = blurPixels is null
                        ? default
                        : BackgroundImageMap.ComputeDestRect(blurImageSize, device, stretch),
                    Fill = blur is null ? null : ReadPixels(blur),
                    FillSize = blur?.PixelSize ?? default,
                    FillDest = blur is null
                        ? default
                        : BackgroundImageMap.ComputeDestRect(blur.PixelSize, device, (int)BackgroundImageStretch.FillCrop),
                    ThemeBackground = themeBg,
                    VeilAlpha = veilA,
                    VeilColor = veilColor,
                    LayerWidth = layerW,
                    LayerHeight = layerH,
                };

                foreach (var g in radiusOf)
                {
                    LiquidGlassBaker.RenderPanel(
                        layer, layerW, layerH,
                        g.Rect.X * scale, g.Rect.Y * scale,
                        g.Rect.Width * scale, g.Rect.Height * scale,
                        g.Radius * scale, style, backdrop);
                }

                sheet = new WriteableBitmap(new PixelSize(layerW, layerH), new Vector(96, 96),
                                            PixelFormat.Bgra8888, AlphaFormat.Premul);
                using (var frame = sheet.Lock())
                    CopyInto(frame, layer, layerW, layerH);
            }
            catch
            {
                sheet?.Dispose();
                sheet = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _sheetBusy = false;
                if (_sheetDirty)
                {
                    sheet?.Dispose();       // 期间又变了，这批作废，按最新的重来
                }
                else
                {
                    _sheet?.Dispose();
                    _sheet = sheet;
                    InvalidateVisual();
                }
                TryStartSheet();
            });
        });
    }

    /// <summary>折射那层的参数（长度一律物理像素）。参数起点照液态玻璃那套配方来。</summary>
    private LiquidGlassStyle BuildStyle(double scale)
    {
        // 用户那条「折射强度」滑块 0~100 映射到位移量：50 是个「看得清但不晕」的中档
        var k = Math.Clamp(_refraction, 0, 100) / 100.0;

        return new LiquidGlassStyle
        {
            RefractionHeight = 22 * scale,
            RefractionAmount = 48 * k * scale,
            Dispersion = 0.04,
            HighlightWidth = 2.0 * scale,
            HighlightFalloff = 5,
            HighlightStrength = 0.5,
            LightAngle = 128 * Math.PI / 180,
            BevelLight = 0.35,
            TintAlpha = Math.Clamp(_glassOpacity, 0, 100) / 100.0 * 0.8,
            TintIsLight = _chrome.IsLight,
            // 磨砂浓度：背后那张花壁纸原样透过玻璃、压在字底下就会把字淹掉，
            // 这条滑块就是给可读性兜底的（苹果那版栽的也是这个坑）
            Frost = Math.Clamp(_frost, 0, 100) / 100.0,
            RimFrost = 0.35,
        };
    }

    /// <summary>
    /// 玻璃背后那块画面的采样器。把「清晰图 / 留白处的模糊填充 / 纯底色 / 明暗遮罩」四件事一起收掉，
    /// 烘焙那边只管问「这个坐标上背后是什么颜色」。
    /// </summary>
    private sealed class LayerBackdrop : IBackdropSampler
    {
        public uint[]? Sharp;
        public PixelSize SharpSize;
        public Rect SharpDest;

        /// <summary>同一张图的模糊副本，落位与 <see cref="Sharp"/> 完全一致。</summary>
        public uint[]? Blur;
        public PixelSize BlurSize;
        public Rect BlurDest;

        public uint[]? Fill;
        public PixelSize FillSize;
        public Rect FillDest;

        public Color ThemeBackground;
        public double VeilAlpha;
        public Color VeilColor;

        public int LayerWidth;
        public int LayerHeight;

        public ColorSample Sample(double x, double y)
            => Fetch(Sharp, SharpSize, SharpDest, x, y);

        public ColorSample SampleBlurred(double x, double y)
            => Fetch(Blur, BlurSize, BlurDest, x, y);

        private ColorSample Fetch(uint[]? pixels, PixelSize size, Rect dest, double x, double y)
        {
            // 贴边折射会把采样坐标推出层外，钳回来（等效于把边缘像素拉长，不会带出黑边）
            x = x < 0 ? 0 : x >= LayerWidth ? LayerWidth - 1 : x;
            y = y < 0 ? 0 : y >= LayerHeight ? LayerHeight - 1 : y;

            var (r, g, b) = SampleBitmap(pixels, size, dest, x, y);
            if (r < 0)
            {
                (r, g, b) = SampleBitmap(Fill, FillSize, FillDest, x, y);
                if (r < 0)
                {
                    r = ThemeBackground.R / 255.0;
                    g = ThemeBackground.G / 255.0;
                    b = ThemeBackground.B / 255.0;
                }
            }

            if (VeilAlpha > 0.001)
            {
                var vr = VeilColor.R / 255.0;
                var vg = VeilColor.G / 255.0;
                var vb = VeilColor.B / 255.0;
                r += (vr - r) * VeilAlpha;
                g += (vg - g) * VeilAlpha;
                b += (vb - b) * VeilAlpha;
            }

            return new ColorSample(r, g, b, 1);
        }
    }

    /// <summary>双线性采样。落在图外返回 (-1,-1,-1) 表示没取到。</summary>
    private static (double R, double G, double B) SampleBitmap(uint[]? pixels, PixelSize size, Rect dest,
                                                              double x, double y)
    {
        if (pixels is null || size.Width < 1 || size.Height < 1) return (-1, -1, -1);
        if (dest.Width <= 0 || dest.Height <= 0) return (-1, -1, -1);
        if (x < dest.X || x >= dest.Right || y < dest.Y || y >= dest.Bottom) return (-1, -1, -1);

        var fx = (x - dest.X) * (size.Width / dest.Width) - 0.5;
        var fy = (y - dest.Y) * (size.Height / dest.Height) - 0.5;

        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        var tx = fx - x0;
        var ty = fy - y0;

        var xa = Math.Clamp(x0, 0, size.Width - 1);
        var xb = Math.Clamp(x0 + 1, 0, size.Width - 1);
        var ya = Math.Clamp(y0, 0, size.Height - 1);
        var yb = Math.Clamp(y0 + 1, 0, size.Height - 1);

        var p00 = pixels[ya * size.Width + xa];
        var p10 = pixels[ya * size.Width + xb];
        var p01 = pixels[yb * size.Width + xa];
        var p11 = pixels[yb * size.Width + xb];

        var w00 = (1 - tx) * (1 - ty);
        var w10 = tx * (1 - ty);
        var w01 = (1 - tx) * ty;
        var w11 = tx * ty;

        return (Red(p00) * w00 + Red(p10) * w10 + Red(p01) * w01 + Red(p11) * w11,
                Green(p00) * w00 + Green(p10) * w10 + Green(p01) * w01 + Green(p11) * w11,
                Blue(p00) * w00 + Blue(p10) * w10 + Blue(p01) * w01 + Blue(p11) * w11);
    }

    // 位图里存的是预乘过的值，取出来要除回去，否则半透明像素会整体偏暗
    private static double Red(uint p) => Unpremultiply((p >> 16) & 0xFF, p >> 24);
    private static double Green(uint p) => Unpremultiply((p >> 8) & 0xFF, p >> 24);
    private static double Blue(uint p) => Unpremultiply(p & 0xFF, p >> 24);

    private static double Unpremultiply(uint channel, uint alpha)
        => alpha == 0 ? 0 : Math.Min(1.0, channel / (double)alpha);

    // ===== 位图：解码 + 烘模糊底图 =====

    private void RequestRebuild()
    {
        _needsRebuild = true;
        if (Bounds.Width > 1) TryStartRebuild(Bounds.Size);
    }

    private void TryStartRebuild(Size size)
    {
        if (_busy || !_needsRebuild) return;
        if (size.Width < 2 || size.Height < 2) return;   // 还没排布，等 ArrangeOverride 再来

        _needsRebuild = false;
        _busy = true;

        var path = _imagePath;
        var radius = _blurRadius;
        var wantBlur = _glassMode is (int)GlassMode.Frosted or (int)GlassMode.Liquid
                       || _stretch == (int)BackgroundImageStretch.Fit;

        // 倍率得在 UI 线程上问（TopLevel 不能在后台线程碰）；按物理像素宽解码
        var pixelWidth = PixelWidthFor(size);

        Task.Run(() =>
        {
            Bitmap? sharp = null;
            WriteableBitmap? blur = null;
            uint[]? sharpPixels = null;
            uint[]? blurPixels = null;
            var sharpPixelsSize = default(PixelSize);
            try
            {
                if (path.Length > 0 && File.Exists(path))
                {
                    using var stream = File.OpenRead(path);
                    sharp = Bitmap.DecodeToWidth(stream, pixelWidth, BitmapInterpolationMode.HighQuality);
                    // 折射要在 CPU 上逐像素采样，顺手把像素倒成连续数组（位图每次读都得先 Lock，太贵）
                    sharpPixels = ReadPixels(sharp);
                    sharpPixelsSize = sharp.PixelSize;
                    if (wantBlur)
                    {
                        blur = BakeBlur(sharp, radius);
                        if (blur is not null) blurPixels = ReadPixels(blur);
                    }
                }
            }
            catch
            {
                // 图片读不了/格式不支持：当作没有背景图，别把整个大屏拖垮
                sharp?.Dispose();
                sharp = null;
                blur?.Dispose();
                blur = null;
                sharpPixels = null;
                blurPixels = null;
            }

            Dispatcher.UIThread.Post(() =>
            {
                _busy = false;
                if (_needsRebuild)
                {
                    // 期间设置又变了：这批结果直接扔掉，按最新的再来一遍
                    sharp?.Dispose();
                    blur?.Dispose();
                }
                else
                {
                    _sharp?.Dispose();
                    _blur?.Dispose();
                    _sharp = sharp;
                    _blur = blur;
                    _sharpPixels = sharpPixels;
                    _sharpPixelsSize = sharpPixelsSize;
                    _blurPixels = blurPixels;
                    _sharpSize = size;
                    _builtPixelWidth = pixelWidth;
                    _builtWantBlur = wantBlur;
                    _builtPath = path;
                    _builtRadius = radius;
                    MarkSheetDirty();
                    InvalidateVisual();
                }
                TryStartRebuild(Bounds.Size);
            });
        });
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

    private static unsafe void CopyInto(ILockedFramebuffer frame, uint[] pixels, int width, int height)
    {
        var stride = frame.RowBytes;
        var origin = (byte*)frame.Address;
        var rowBytes = (long)width * 4;

        fixed (uint* src = pixels)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(src + (long)y * width, origin + (long)y * stride, rowBytes, rowBytes);
        }
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
