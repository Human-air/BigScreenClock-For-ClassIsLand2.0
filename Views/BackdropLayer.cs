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
    //
    // 下面这块画布与这张位图都是**复用**的，不每次重烘都新建：它们各有整屏那么大（1920×1080 就是 8 MB），
    // 而重烘其实每秒都在发生（时钟秒数一跳，它的玻璃矩形就变一次），每烘一次扔掉十几 MB 的话，
    // 几分钟就能把进程的常驻内存顶到 CI 内存看门狗的上限（1.40 GiB）——那条「闪退」就是这么来的。
    private uint[]? _sheetCanvas;
    private WriteableBitmap? _sheet;
    private bool _sheetDirty = true;
    private bool _sheetBusy;
    // 手上那张折射图被扔掉了（拖动一开始就扔）、新的还没烘好。这段时间这一档整块不画。
    // 见 Apply 里拖动开始那一段。
    private bool _sheetDropped;
    private DispatcherTimer? _sheetTimer;
    private string _builtPath = "";  // 手上这张底图是按哪个输入烘出来的：换了输入才重烘
    private double _builtRadius = -1;
    private bool? _builtWantBlur;        // 上次烘的时候要不要模糊底图（换了要重来）
    private bool _needsRebuild;
    private bool _busy;
    private bool _releasePending;   // 离开视觉树时后台正好在烘图 → 等它收工再放位图
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

    /// <summary>
    /// 设置页正在拖「位置 / 字号」，玻璃整层收起不画。
    ///
    /// 拖动时每一帧都要跟着重排，而玻璃这一层每帧的代价是很实的：十二块圆角投影、一层整屏折射图、
    /// 一层整屏清晰图，三样叠起来 Skia 每帧都要出好几个整屏中间面。帧一多，这些面排着队释放不掉，
    /// 内存就顶着看门狗上限走（现象：一调位置大小就闪退）；而且拖动中间态的一堆整屏重烘画出来
    /// 也是一片拖影。索性拖动期间不画，松手（停 300 毫秒）再一次性画出来。
    /// </summary>
    private bool _adjusting;

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
    /// <param name="adjusting">
    /// 设置页正在拖「位置 / 字号」这类连着改布局的滑块。为真时玻璃整层收起、重烘也停掉，
    /// 见 <see cref="PluginSettings.AdjustingLayout"/>。
    /// </param>
    public void Apply(string? imagePath, int stretch, double dim, int glassMode, double glassOpacity,
                      double blurRadius, double refraction, double frost, bool adjusting = false)
    {
        imagePath ??= "";

        if (_adjusting != adjusting)
        {
            _adjusting = adjusting;

            // 拖动期间插值降到双线性：设置页那块预览是个 Viewbox，里面装着整屏那么大的一面，
            // 高质量缩放会让 Skia 每帧给整屏位图重算一次 mipmap（几十兆级的临时面，一秒钟几十个）。
            // 拖动中降档既省下这一大笔，画面又几乎看不出差别；松手立刻换回高质量。
            RenderOptions.SetBitmapInterpolationMode(this, _adjusting
                ? BitmapInterpolationMode.LowQuality
                : BitmapInterpolationMode.HighQuality);

            // 一开始拖就把手上这张折射图扔掉。它是上一版位置烘出来的，留着的话松手那一刻
            // 玻璃会先按旧位置原样画一下、等新图烘好再跳过去（「刚渲染出新玻璃的一瞬间老的还在」）。
            // 扔掉之后 _sheet 为 null，渲染那边直到新图出来之前整块不画 —— 拖的过程中本来就是
            // 整层收起的，接得上。顺带把这一整屏的位图（8 MB）在拖的时候腾出去。
            if (_adjusting && _sheet is not null)
            {
                _sheet.Dispose();
                _sheet = null;
                _sheetDropped = true;
            }

            // 收起来这一下：停掉在途的重烘，把已经排上的重烘请求作废；
            // 放出来这一下：按当前设置把整屏折射重烘一遍（拖动期间的每一次布局变化都不再烘了）
            MarkSheetDirty();
        }

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
        // 走防抖那条路：这里以前是直接标脏，于是「标脏 → 下一帧 Render 里立刻重烘」，
        // 拖滑块时等于每帧烘一张整屏图。攒 120ms 再算，中间态都白算。
        MarkSheetDirty();
        InvalidateVisual();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _releasePending = false;   // 又回到树上了：刚才那次「离开时放掉」取消
        _layoutUpdatedHandler ??= (_, _) => OnLayoutUpdated();
        LayoutUpdated += _layoutUpdatedHandler;
        RequestRebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_layoutUpdatedHandler is { } handler) LayoutUpdated -= handler;
        ReleaseBitmaps();
    }

    /// <summary>
    /// 离开视觉树时把底图与折射图全放掉、把重烘的定时器停掉。
    ///
    /// 不放的话：DispatcherTimer 会一直攥着这个背景层（连带它那几张整屏位图与像素快照，几十 MB），
    /// 设置页每开一次预览就漏一份 —— CI 的内存看门狗就是这么被顶爆的。
    /// 回到树上时 <see cref="OnAttachedToVisualTree"/> 会重新 RequestRebuild，按当时的设置重烘一遍。
    /// </summary>
    private void ReleaseBitmaps()
    {
        _sheetTimer?.Stop();

        // 后台正烘着图（它正读着这些像素/位图）就先让开，等它回来再放
        if (_busy || _sheetBusy)
        {
            _releasePending = true;
            return;
        }

        _releasePending = false;
        _sharp?.Dispose();
        _sharp = null;
        _blur?.Dispose();
        _blur = null;
        _sheet?.Dispose();
        _sheet = null;
        _sharpPixels = null;
        _blurPixels = null;
        _sheetCanvas = null;

        _builtPixel = default;
        _sheetPixel = default;
        _reqPixel = default;
        _builtPath = "";
        _builtRadius = -1;
        _builtWantBlur = null;

        _needsRebuild = true;
        _sheetDirty = true;
        _sheetDropped = false;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        if (finalSize.Width < 2 || finalSize.Height < 2) return result;

        // 这里显式传 finalSize 而不是走 Bounds —— 排布过程中 Bounds 还是上一轮的旧值，
        // 拿旧尺寸烘出来的底图会让玻璃里的画面整体错位。
        SyncPixelBudget(finalSize);
        TryStartRebuild(finalSize);

        return result;
    }

    /// <summary>
    /// 现在这个尺寸该按多少物理像素烘东西；发现手上那批不是这个尺寸，就安排重烘。
    ///
    /// 比的是**物理像素**而不是逻辑尺寸（窗口被拖到另一块缩放倍率不同的显示器上时，
    /// 逻辑尺寸可能没变但该解的像素变了；反过来设置页那块预览逻辑尺寸抖一个像素也不改画质）。
    /// 「正在烘的那一批」也算数：不算它的话，同一批会被反复作废重来，每来一遍就是上百兆垃圾。
    /// </summary>
    private void SyncPixelBudget(Size size)
    {
        if (size.Width < 2 || size.Height < 2) return;

        var want = WantedPixelSize(size);
        if ((_busy ? _reqPixel : _builtPixel) != want) _needsRebuild = true;

        if (_sheetPixel != want)
        {
            _sheetPixel = want;
            MarkSheetDirty();
        }
    }

    // 底下这几张图各自多大，一律按**物理像素**记（逻辑尺寸在设置页预览那种缩放的场合会骗人）
    private PixelSize _builtPixel;   // 手上这张底图是按多少物理像素解的
    private PixelSize _sheetPixel;   // 折射图是按多少物理像素烘的
    private PixelSize _reqPixel;     // 正在烘的那一批要多少物理像素

    /// <summary>这个逻辑尺寸该烘成多少物理像素。</summary>
    private PixelSize WantedPixelSize(Size size)
    {
        var scale = DeviceScale(size);
        return new PixelSize(Math.Max(64, (int)Math.Round(size.Width * scale)),
                             Math.Max(36, (int)Math.Round(size.Height * scale)));
    }

    /// <summary>
    /// 一个逻辑像素最后落在几个物理像素上 = 屏幕缩放 × 这一层在屏幕上被缩放的倍率。
    ///
    /// 大屏是满屏映到屏幕上，后一项是 1；设置页那块预览是 Viewbox 缩着放的，里面却装着
    /// 整屏那么大的一面 —— 不把这一层算进来，就是为一张几百像素宽的缩略图去解一张 2560 宽的底图、
    /// 再烘一张 2560 宽的折射图：白占两百来兆常驻，每换一次图还要造上百兆垃圾（CI 的内存看门狗就是这么被顶爆的）。
    /// </summary>
    private double DeviceScale(Size size)
    {
        var scale = RenderScale;

        if (size.Width < 2) return scale;

        try
        {
            if (TopLevel.GetTopLevel(this) is { } top && this.TransformToVisual(top) is { } m)
            {
                var visual = Math.Abs(m.M11);   // 这一路没有旋转，取横向缩放就够
                if (visual is >= 0.05 and <= 4) scale *= visual;
            }
        }
        catch
        {
            // 拿不到变换就只按屏幕缩放算
        }

        return scale;
    }

    /// <summary>每次布局跑完后比一比：玻璃所在的矩形动了就重画（Avalonia 是保留式渲染，别人动了不会叫醒这一层）。</summary>
    private void OnLayoutUpdated()
    {
        if (_needsRebuild) TryStartRebuild(Bounds.Size);
        if (_glassMode == (int)GlassMode.Off || _adjusting) return;

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

        // 画之前再核一次倍率：排布那一刻层可能刚挂上视觉树，外面那层缩放（设置页预览的 Viewbox）
        // 还没落定，照着排布时的倍率就会烘出一张整屏大的图白占内存。这里核出来不对就当场重来一遍，
        // 大屏上这一步永远是对上的，不会多做工。
        SyncPixelBudget(size);

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

        // 拖动位置/字号期间：玻璃整层让开，只留背景图（重活全在这一块里，拖起来也看不清）
        if (_glassMode == (int)GlassMode.Off || _adjusting) return;

        var frosted = _glassMode == (int)GlassMode.Frosted;
        var liquid = _glassMode == (int)GlassMode.Liquid;
        var rects = _rects.Length > 0 ? _rects : ComputeGlassRects(size);

        // 折射图被扔了、新的还没烘好（刚放手那 100 多毫秒）：这一档整块不画。
        // 只画描边不画内容的话，屏幕上是几个空心框，比什么都不画更奇怪。
        if (liquid && _sheetDropped) return;

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
        // 防抖计时器还在跑就等它：否则每次布局/渲染都从这里直接烘一遍，
        // 上面那个 120ms 防抖等于白搭（拖滑块时每帧一张整屏图）
        if (_sheetTimer is { IsEnabled: true }) return;
        // 拖动期间一次都不烘：这一张图要算整屏，拖动时每一下布局变化都重算一遍是白烧
        if (_adjusting) return;
        if (_glassMode != (int)GlassMode.Liquid) return;

        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;

        var rects = _rects.Length > 0 ? _rects : ComputeGlassRects(size);
        if (rects.Length == 0) return;

        // 用实际落屏的倍率，不是屏幕缩放：设置页那块预览缩着放，折射图按它实际画到的大小烘就行
        // （按整屏烘一张 2560×1600 的，屏幕上一共才几百像素宽，白烧 15MB + 每秒一次的整屏重算）
        var scale = DeviceScale(size);
        var layerW = Math.Max(2, (int)Math.Round(size.Width * scale));
        var layerH = Math.Max(2, (int)Math.Round(size.Height * scale));
        if ((long)layerW * layerH > 40_000_000)
        {
            // 大到离谱（多屏拼出的巨桌面）就不做折射了：别把它算成「一直在等新图」而整块不画
            _sheetDropped = false;
            return;
        }

        // 快照：算的这段时间里设置可能又变，变了就让结果作废重来（_sheetDirty 会再置上）
        var sharpPixels = _sharpPixels;
        var sharpPixelsSize = _sharpPixelsSize;
        var blurPixels = _blurPixels;
        var blurImageSize = _blur?.PixelSize ?? default;
        var stretch = _stretch;
        var style = BuildStyle(scale);
        var veilA = Math.Abs(_dim) < 0.5 ? 0.0 : Math.Min(1.0, Math.Abs(_dim) / 100.0);
        var veilColor = _dim < 0 ? Colors.Black : Colors.White;
        var themeBg = _themeBg;
        var radiusOf = rects;

        // 画布只增不减：尺寸没变就一直用同一块，重烘不再产生大对象
        if (_sheetCanvas is null || _sheetCanvas.Length != layerW * layerH)
            _sheetCanvas = new uint[layerW * layerH];
        var canvas = _sheetCanvas;

        _sheetDirty = false;
        _sheetBusy = true;

        Task.Run(() =>
        {
            var ok = false;
            try
            {
                // 画布是复用的，烘之前必须先擦干净：不擦的话上一次那几块玻璃的像素会留在原地，
                // 组件一挪位就成了「好几块玻璃叠在一起」的拖影。
                Array.Clear(canvas, 0, canvas.Length);

                var device = new Size(layerW, layerH);

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
                    // 留白处的填充与模糊那一路是同一张快照，别再拷一份（拷一次就是 8 MB 垃圾）
                    Fill = blurPixels,
                    FillSize = blurPixels is null ? default : blurImageSize,
                    FillDest = blurPixels is null
                        ? default
                        : BackgroundImageMap.ComputeDestRect(blurImageSize, device, (int)BackgroundImageStretch.FillCrop),
                    ThemeBackground = themeBg,
                    VeilAlpha = veilA,
                    VeilColor = veilColor,
                    LayerWidth = layerW,
                    LayerHeight = layerH,
                };

                foreach (var g in radiusOf)
                {
                    LiquidGlassBaker.RenderPanel(
                        canvas, layerW, layerH,
                        g.Rect.X * scale, g.Rect.Y * scale,
                        g.Rect.Width * scale, g.Rect.Height * scale,
                        g.Radius * scale, style, backdrop);
                }

                ok = true;
            }
            catch
            {
                // 算崩了就保持原来那张，别把大屏拖垮
            }

            Dispatcher.UIThread.Post(() =>
            {
                _sheetBusy = false;
                if (_releasePending) { ReleaseBitmaps(); return; }
                if (ok && !_sheetDirty)
                {
                    // 位图也在 UI 线程上复用：渲染器本来就在这个线程读它，锁与画不会打架
                    if (_sheet is not { } sheet || sheet.PixelSize.Width != layerW || sheet.PixelSize.Height != layerH)
                    {
                        _sheet?.Dispose();
                        _sheet = sheet = new WriteableBitmap(new PixelSize(layerW, layerH), new Vector(96, 96),
                                                             PixelFormat.Bgra8888, AlphaFormat.Premul);
                    }
                    using (var frame = sheet.Lock())
                        CopyInto(frame, canvas, layerW, layerH);
                    _sheetDropped = false;
                    InvalidateVisual();
                }
                else if (!ok)
                {
                    // 算崩了：退回「只画描边」的老样子，别让玻璃整块消失
                    _sheetDropped = false;
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

    /// <summary>当前设置下要不要一张模糊底图（磨砂与折射两档要，「完整显示」的补边也要）。</summary>
    private bool WantBlurNow()
        => _glassMode is (int)GlassMode.Frosted or (int)GlassMode.Liquid
           || _stretch == (int)BackgroundImageStretch.Fit;

    private void TryStartRebuild(Size size)
    {
        if (_busy || !_needsRebuild) return;
        if (size.Width < 2 || size.Height < 2) return;   // 还没排布，等 ArrangeOverride 再来

        _needsRebuild = false;
        _busy = true;

        var path = _imagePath;
        var radius = _blurRadius;
        var wantBlur = WantBlurNow();
        var stretch = _stretch;

        // 倍率得在 UI 线程上问（TopLevel 不能在后台线程碰）；按物理像素宽解码
        var pixels = WantedPixelSize(size);
        _reqPixel = pixels;

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
                    // 解码尺寸要按**原图**多大来定：解码器给多宽就解多宽，小图会被放大着解，
                    // 白白多占一倍半内存还多插一遍值。原图尺寸只读文件头，不解码。
                    var native = ImageHeader.ReadSize(path) ?? pixels;
                    var decode = BackgroundImageMap.DecodeSize(native, pixels, stretch);

                    using var stream = File.OpenRead(path);
                    sharp = Bitmap.DecodeToWidth(stream, decode.Width, BitmapInterpolationMode.HighQuality);
                    // 折射要在 CPU 上逐像素采样，顺手把像素倒成连续数组（位图每次读都得先 Lock，太贵）
                    sharpPixels = ReadPixels(sharp);
                    sharpPixelsSize = sharp.PixelSize;
                    if (wantBlur)
                    {
                        // 模糊半径说的是「屏幕上多少像素」，底图却是按自己的像素烘的。底图比这一层
                        // 落屏的像素少（小图不放大着解、设置页那块是缩着的预览），不换算磨砂在屏幕上
                        // 就会比设定值粗 —— 小图越小事越明显。
                        blur = BakeBlur(sharp, radius * decode.Width / Math.Max(1, pixels.Width));
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
                if (_releasePending)
                {
                    // 这期间离开了视觉树：结果扔掉，位图也一并放掉
                    sharp?.Dispose();
                    blur?.Dispose();
                    ReleaseBitmaps();
                    return;
                }
                // 期间设置又变了：但只有「烘图真正吃的那几项」变了才值得重来一遍。
                // 换一次壁纸，设置页会连着写好几项设置（图片路径、填充方式、遮罩……），
                // 其中只有一项能让这张图作废，后几次写的是同一张图 —— 不做这个判断，
                // 同一张图会被烘两遍，白造上百兆垃圾（换图那一秒内存窜一截就是这么来的）。
                var stale = _needsRebuild
                            && (_imagePath != path || _blurRadius != radius || _stretch != stretch
                                || WantBlurNow() != wantBlur || _reqPixel != pixels);

                if (stale)
                {
                    sharp?.Dispose();
                    blur?.Dispose();
                }
                else
                {
                    _needsRebuild = false;
                    _sharp?.Dispose();
                    _blur?.Dispose();
                    _sharp = sharp;
                    _blur = blur;
                    _sharpPixels = sharpPixels;
                    _sharpPixelsSize = sharpPixelsSize;
                    _blurPixels = blurPixels;
                    _builtPixel = pixels;
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
