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

    // 「真•◯◯玻璃」的折射图：**一块一张小位图**，按控件块缓存、改到哪块才重烘哪块。
    //
    // 原来这里是整屏一张：任何一块矩形动了一点点都得整屏重清、整屏重算，而「正在记录」这种
    // 字数每秒都在变的块一动就整屏重来 —— 一分钟下来就是几十张整屏大小的垃圾（1920×1080 是 8 MB），
    // 这正是把进程顶到 CI 内存看门狗上限（1.40 GiB）、大屏「闪退」的元凶。
    // 改成按块以后：计数行那点字变来变去，只重烘它自己那一块（几十 KB）。
    private readonly List<GlassPanel> _panels = new();
    private bool _glassDirty = true;
    private bool _glassBusy;
    private int _contentVersion;     // 玻璃的光学参数 / 背景像素 / 明暗遮罩一变就 +1，手上那些块全作废
    private DispatcherTimer? _glassTimer;
    private string _builtPath = "";  // 手上这张底图是按哪个输入烘出来的：换了输入才重烘
    private double _builtRadius = -1;
    private bool? _builtWantBlur;        // 上次烘的时候要不要模糊底图（换了要重来）
    private bool _needsRebuild;
    private bool _busy;
    private bool _releasePending;   // 离开视觉树时后台正好在烘图 → 等它收工再放位图

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
    /// 设置页正**按着**「位置 / 字号」那根滑块（从按下到松开，跟手停多久无关）。
    ///
    /// 按着期间不重烘玻璃 —— 只是按住不动的话，玻璃该照旧显示，一个字节都不用重算。
    /// </summary>
    private bool _adjusting;

    /// <summary>
    /// 按着滑块期间，**矩形真的动过了**：这时才把玻璃收起来、改画一圈覆盖框。
    ///
    /// 分两档是为了对上「按下 → 拖动 → 松手」这道手感：只按住不拖，玻璃原样留着；
    /// 一拖动，旧玻璃立刻收走（它已经是按旧位置烘的，留着就是一片错位的拖影）；
    /// 松手前一直只有框，松手才开新一轮重烘 —— 而且只重烘矩形真变过的那几块。
    /// </summary>
    private bool _suppressed;

    /// <summary>换主题：玻璃的底色与描边跟着亮/暗翻转（底色是画的时候现取的，不用重烘位图）。</summary>
    public void SetPalette(ClockBrushes chrome, Color themeBackground)
    {
        // 只有「亮/暗翻转」与留白底色真的变了才整批作废：这两个是烘进折射图里的
        // （材料色 TintIsLight 与留白处的 ThemeBackground），其余几把刷子都是画的时候现取的。
        // 不加这个判断，设置页每刷新一次配色就把十二块全废掉、整屏重烘一遍。
        var changed = chrome.IsLight != _chrome.IsLight || themeBackground != _themeBg;

        _chrome = chrome;
        _themeBg = themeBackground;
        RebuildBrushes();

        if (changed)
        {
            _contentVersion++;
            MarkGlassDirty();
        }

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

            // 按着期间插值降到双线性：设置页那块预览是个 Viewbox，里面装着整屏那么大的一面，
            // 高质量缩放会让 Skia 每帧给整屏位图重算一次 mipmap（几十兆级的临时面，一秒钟几十个）。
            // 拖动中降档既省下这一大笔，画面又几乎看不出差别；松手立刻换回高质量。
            RenderOptions.SetBitmapInterpolationMode(this, _adjusting
                ? BitmapInterpolationMode.LowQuality
                : BitmapInterpolationMode.HighQuality);

            // 松手：解冻。覆盖框收起、换回玻璃，并且**只**重烘矩形真变过的那几块
            // （没动过的块位图还在手上，直接接着用）。
            if (!_adjusting) _suppressed = false;

            // 按下去这一下：停掉在途的重烘、把已经排上的请求作废（拖动期间不再烘）；
            // 放开来这一下：按当前设置把该重烘的块烘出来
            MarkGlassDirty();
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

        // 「背景明暗」也在里头：它是烘进折射图里的（采样时压的那层 veil 是算出来的），
        // 以前漏了这一项，拖明暗滑块时折射档玻璃里外会差一档亮度。
        var restyle = glassMode != _glassMode
                      || Math.Abs(glassOpacity - _glassOpacity) > 0.01
                      || Math.Abs(refraction - _refraction) > 0.01
                      || Math.Abs(frost - _frost) > 0.01
                      || Math.Abs(dim - _dim) > 0.01
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
        if (restyle)
        {
            _contentVersion++;   // 光学参数变了：手上那些块的像素全不作数了
            MarkGlassDirty();
        }
        if (rebuild) RequestRebuild();
        InvalidateVisual();
    }

    /// <summary>组件被拖动/内容变化后，玻璃要跟着走。窗口那边在布局变化的钩子里调。</summary>
    public void InvalidateGlass()
    {
        // 走防抖那条路：这里以前是直接标脏，于是「标脏 → 下一帧 Render 里立刻重烘」，
        // 拖滑块时等于每帧烘一张整屏图。攒 120ms 再算，中间态都白算。
        MarkGlassDirty();
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
        _glassTimer?.Stop();

        // 后台正烘着图（它正读着这些像素/位图）就先让开，等它回来再放
        if (_busy || _glassBusy)
        {
            _releasePending = true;
            return;
        }

        _releasePending = false;
        _sharp?.Dispose();
        _sharp = null;
        _blur?.Dispose();
        _blur = null;
        _sharpPixels = null;
        _blurPixels = null;

        foreach (var panel in _panels) panel.Dispose();
        _panels.Clear();

        _builtPixel = default;
        _sheetPixel = default;
        _reqPixel = default;
        _builtPath = "";
        _builtRadius = -1;
        _builtWantBlur = null;

        _needsRebuild = true;
        _glassDirty = true;
        _suppressed = false;
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
            MarkGlassDirty();
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
        if (_glassMode == (int)GlassMode.Off) return;

        if (SyncPanels())
        {
            // 按着滑块期间矩形真的动了 → 收起玻璃、改画覆盖框（只按住不拖的话走不到这儿）
            if (_adjusting) _suppressed = true;

            MarkGlassDirty();       // 哪些块的矩形变了，就在那儿等着重烘（SyncPanels 记好了）
            InvalidateVisual();
        }

        if (_adjusting) return;     // 按着期间一次都不烘
        TryStartBake();
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
        if (_panels.Count == 0) SyncPanels();   // 头一帧（布局钩子还没轮到）先按当前布局把块建出来
        if (_glassDirty) TryStartBake();

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

        // 按着滑块拖动中：玻璃整层让开，只画各块的覆盖范围框
        if (_suppressed)
        {
            DrawGlassOutlines(context);
            return;
        }

        var frosted = _glassMode == (int)GlassMode.Frosted;
        var liquid = _glassMode == (int)GlassMode.Liquid;

        // 「真•高光折射」：每块玻璃自己一张小图，先按序全部铺上（块的先后顺序就是原来整屏一张
        // 依次烘进去的顺序，两块重叠时后画的盖住先画的，与整屏那张的结果一致）。
        // 底下那张清晰图不用先擦掉——折射图在玻璃范围内是不透明的，正好把它盖住。
        if (liquid)
        {
            foreach (var p in _panels)
            {
                // 位置刚变过、新图还没烘好的块先不画（画旧位置就是一片错位的拖影），
                // 同时也就不给它画描边了 —— 几个空心框比什么都不画更奇怪。
                if (p.NeedsBake || p.Bitmap is not { } bitmap) continue;
                DrawPanel(context, p, bitmap);
            }
        }

        foreach (var p in _panels)
        {
            if (liquid && (p.NeedsBake || p.Bitmap is null)) continue;

            var rr = new RoundedRect(p.Rect, p.Radius);

            // 磨砂档：玻璃里透出的就是「整张模糊背景按同一套映射铺开、被这块裁掉」的那片，
            // 与玻璃外那张清晰图走的是同一个 ComputeDestRect —— 里外必然严丝合缝。
            if (frosted && _blur is { } blur)
                DrawBlurredBackdrop(context, blur, p.Rect, rr, size, veil);

            // 底色渐变 + 1px 亮边 + 外投影/内亮边（后者是「玻璃厚度感」的来源）。
            // 折射档的底色已经烘进折射图里了，这里只补描边和投影，别再叠一层渐变把折射糊掉。
            // 注意：描边必须等**所有**块的折射图都画完再画，否则相邻两块重叠时，
            // 后画那块会把先画那块的描边盖掉。
            if (liquid)
                context.DrawRectangle(null, _glassPen, rr, _glassShadows);
            else
                context.DrawRectangle(_glassFill, _glassPen, rr, _glassShadows);
        }
    }

    /// <summary>
    /// 把一块烘好的折射图落到位。位图是按物理像素烘的（宽高 = 这块的四舍五入外接框），
    /// 落位就走整数设备像素：平移 <c>px / scale</c> 逻辑单位，再按 1:1 画 —— 换算回设备像素正好是
    /// 整数 <c>px</c>，不会落在半像素上被 Skia 重新采样（那样整块会发虚）。
    /// </summary>
    private static void DrawPanel(DrawingContext context, GlassPanel panel, WriteableBitmap bitmap)
    {
        var scale = panel.BakedScale;
        if (scale <= 0) return;

        var w = bitmap.PixelSize.Width / scale;
        var h = bitmap.PixelSize.Height / scale;

        using (context.PushTransform(Matrix.CreateTranslation(panel.Px / scale, panel.Py / scale)))
            context.DrawImage(bitmap, new Rect(0, 0, w, h));
    }

    /// <summary>
    /// 拖动期间替玻璃的那圈覆盖框：把每块玻璃盖住的**范围**画出来，好让用户知道玻璃到底多大。
    /// 颜色跟主题走（亮底深框 / 暗底浅框），2px 实线。
    /// </summary>
    private void DrawGlassOutlines(DrawingContext context)
    {
        var pen = new Pen(new SolidColorBrush(_chrome.IsLight
            ? Color.FromArgb(0xB0, 0x00, 0x00, 0x00)
            : Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)), 2);

        foreach (var p in _panels)
            context.DrawRectangle(null, pen, new RoundedRect(p.Rect, p.Radius));
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

            list.Add(new GlassRect(t, rect, t.Radius));
        }
        return list.ToArray();
    }

    private readonly record struct GlassRect(GlassTarget Target, Rect Rect, double Radius);

    /// <summary>两个矩形算不算「同一个位置」：0.5 逻辑像素以内的抖动不算（布局每帧给的浮点值不保证逐位相同）。</summary>
    private static bool SameRect(Rect a, Rect b)
        => Math.Abs(a.X - b.X) <= 0.5 && Math.Abs(a.Y - b.Y) <= 0.5
           && Math.Abs(a.Width - b.Width) <= 0.5 && Math.Abs(a.Height - b.Height) <= 0.5;

    /// <summary>
    /// 把 <see cref="_panels"/> 跟当前布局对齐：新增的块建出来、挪过的块更新矩形、
    /// 消失的块连位图一起放掉，并重新算好每块「要不要重烘」。
    ///
    /// 只做记账，一个像素都不算，所以每轮布局都能直接跑。
    /// </summary>
    /// <returns>矩形或圆角真的变过（或有块增删）时为 true。</returns>
    private bool SyncPanels()
    {
        var size = Bounds.Size;
        var entries = ComputeGlassRects(size);
        var liquid = _glassMode == (int)GlassMode.Liquid;
        var scale = size.Width < 2 ? 0 : DeviceScale(size);
        var changed = false;

        var next = new List<GlassPanel>(entries.Length);
        foreach (var e in entries)
        {
            var panel = FindPanel(e.Target);
            if (panel is null)
            {
                panel = new GlassPanel { Target = e.Target };
                changed = true;
            }
            else if (!SameRect(panel.Rect, e.Rect) || Math.Abs(panel.Radius - e.Radius) > 0.01)
            {
                changed = true;
            }

            panel.Rect = e.Rect;
            panel.Radius = e.Radius;

            // 手上那张位图还算不算数：矩形/圆角/倍率/内容版本任一对不上就得重烘。
            // 比的是「烘的时候那份」（BakedRect 等）而不是上一次同步的值 —— 拖了一圈又拖回原位的话，
            // 位图其实还是对的，这里就自动认出来了，白烘一趟。
            panel.NeedsBake = liquid
                              && (panel.Bitmap is null
                                  || !SameRect(panel.Rect, panel.BakedRect)
                                  || Math.Abs(panel.Radius - panel.BakedRadius) > 0.01
                                  || panel.BakedVersion != _contentVersion
                                  || Math.Abs(panel.BakedScale - scale) > 1e-9);

            next.Add(panel);
        }

        // 退场（被隐藏 / 移出清单）的块：位图立刻放掉，别留着占地方
        foreach (var panel in _panels)
        {
            if (next.Contains(panel)) continue;
            panel.Dispose();
            changed = true;
        }

        // 顺序照 GlassTargets 来：折射图先画的被后画的盖住，这个次序得稳定
        _panels.Clear();
        _panels.AddRange(next);
        return changed;
    }

    private GlassPanel? FindPanel(GlassTarget target)
    {
        foreach (var panel in _panels)
            if (ReferenceEquals(panel.Target, target)) return panel;
        return null;
    }

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

    // ===== 「真•高光折射」的那些块 =====

    /// <summary>
    /// 标脏。组件挪位、换主题、换背景、拖滑块都会调到这里，所以挂了个防抖：
    /// 拖时钟位置滑块时一帧一次的概率很高，攒 120 毫秒再算，免得白算十几遍中间态。
    /// </summary>
    private void MarkGlassDirty()
    {
        _glassDirty = true;
        if (Bounds.Width < 2) return;

        _glassTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(120) };
        if (!_glassTimer.IsEnabled)
        {
            _glassTimer.Tick -= OnGlassTimerTick;
            _glassTimer.Tick += OnGlassTimerTick;
        }
        _glassTimer.Stop();
        _glassTimer.Start();
    }

    private void OnGlassTimerTick(object? sender, EventArgs e)
    {
        _glassTimer?.Stop();
        TryStartBake();
    }

    /// <summary>
    /// 把手上「该重烘」的那几块玻璃算出来（**只算这几块**）。
    ///
    /// 纯 CPU 逐像素算，跟烘模糊底图是同一套路，所以后台线程上跑也没问题。
    /// 教室里那些弱机也扛得住：安静的时候一块都不用重算，只有真动了的那块才烧那几十 KB。
    /// </summary>
    private void TryStartBake()
    {
        if (_glassBusy || !_glassDirty) return;
        // 防抖计时器还在跑就等它：否则每次布局/渲染都从这里直接烘一遍，上面那个 120ms 防抖等于白搭
        if (_glassTimer is { IsEnabled: true }) return;
        // 按着滑块期间一次都不烘（松手才开新一轮）
        if (_adjusting) return;
        if (_glassMode != (int)GlassMode.Liquid) return;

        var size = Bounds.Size;
        if (size.Width < 2 || size.Height < 2) return;

        // NeedsBake 是 SyncPanels() 里按 _contentVersion 算的，而 SyncPanels() 平时只在布局
        // （OnLayoutUpdated）和「一块都还没有」时才跑。可 _contentVersion 会因为一堆不动布局的
        // 东西变：玻璃档位、透明度、折射强度、雾面、背景明暗…… 不在这里补一次，那些改动
        // 就会撞上「一块都不该烘」→ 标脏被清掉 → 玻璃一直不重画（切回液态档时直接不出图）。
        SyncPanels();

        var pending = new List<GlassPanel>();
        foreach (var panel in _panels)
            if (panel.NeedsBake) pending.Add(panel);

        if (pending.Count == 0) { _glassDirty = false; return; }

        // 用实际落屏的倍率，不是屏幕缩放：设置页那块预览缩着放，折射图按它实际画到的大小烘就行
        // （按整屏烘一张 2560×1600 的，屏幕上一共才几百像素宽，白烧 15MB + 每秒一次的重算）
        var scale = DeviceScale(size);
        var layerW = Math.Max(2, (int)Math.Round(size.Width * scale));
        var layerH = Math.Max(2, (int)Math.Round(size.Height * scale));
        if ((long)layerW * layerH > 40_000_000)
        {
            // 大到离谱（多屏拼出的巨桌面）就不做折射了：别把它算成「一直在等新图」而整块不画
            _glassDirty = false;
            return;
        }

        // 这几块背后的画面是同一张，采样器建一份就够；每块再包一层偏移（见 OffsetSampler）
        var backdrop = BuildBackdrop(scale, layerW, layerH);
        var style = BuildStyle(scale);
        var version = _contentVersion;

        var jobs = new List<BakeJob>(pending.Count);
        foreach (var panel in pending)
        {
            // 这一块在层里的整数设备像素外接框。取整方向必须是「包住」：
            // px 往下取、右/下往上取，缓冲才装得下烘焙里那个小数偏移。
            var px = (int)Math.Floor(panel.Rect.X * scale);
            var py = (int)Math.Floor(panel.Rect.Y * scale);
            var pw = Math.Max(1, (int)Math.Ceiling(panel.Rect.Right * scale) - px);
            var ph = Math.Max(1, (int)Math.Ceiling(panel.Rect.Bottom * scale) - py);

            jobs.Add(new BakeJob
            {
                Panel = panel,
                Rect = panel.Rect,
                Radius = panel.Radius,
                Version = version,
                Scale = scale,
                Px = px,
                Py = py,
                Width = pw,
                Height = ph,
                // 玻璃左上角在这个小缓冲里的位置：**带小数**。传 0 的话玻璃形状会画在缓冲原点、
                // 与采样坐标差一个亚像素的小数，折射图案就和矢量描边错开了。
                PanelX = panel.Rect.X * scale - px,
                PanelY = panel.Rect.Y * scale - py,
                PanelWidth = panel.Rect.Width * scale,
                PanelHeight = panel.Rect.Height * scale,
                RadiusDevice = panel.Radius * scale,
                Canvas = panel.Canvas is { } c && c.Length == pw * ph ? c : null,
            });
        }

        _glassDirty = false;
        _glassBusy = true;

        Task.Run(() =>
        {
            foreach (var job in jobs)
            {
                try
                {
                    // 缓冲是复用的，烘之前必须先擦干净：不擦的话上一次那几块玻璃的像素会留在原地
                    var canvas = job.Canvas ?? new uint[job.Width * job.Height];
                    Array.Clear(canvas, 0, canvas.Length);

                    LiquidGlassBaker.RenderPanel(
                        canvas, job.Width, job.Height,
                        job.PanelX, job.PanelY,
                        job.PanelWidth, job.PanelHeight,
                        job.RadiusDevice, style,
                        new OffsetSampler(backdrop, job.Px, job.Py));

                    job.Result = canvas;
                }
                catch
                {
                    // 这一块算崩了就保持它原来那张，别把大屏拖垮
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _glassBusy = false;
                if (_releasePending) { ReleaseBitmaps(); return; }

                var stale = false;
                foreach (var job in jobs)
                {
                    var panel = job.Panel;

                    // 算的这段时间里它又动了 / 设置又变了：这一趟结果作废，稍后再来（下面按 stale 再标脏）。
                    // 注意这里比的是「烘的那份」对「现在这份」，所以只有真变了才算作废。
                    if (!_panels.Contains(panel)
                        || !SameRect(panel.Rect, job.Rect)
                        || Math.Abs(panel.Radius - job.Radius) > 0.01
                        || job.Version != _contentVersion
                        || Math.Abs(job.Scale - scale) > 1e-9)
                    {
                        stale = true;
                        continue;
                    }

                    if (job.Result is not { } result)
                    {
                        // 这一块算崩了：别再挂着 NeedsBake 一直等（那会变成每轮布局都白算一遍）
                        panel.NeedsBake = false;
                        continue;
                    }

                    // 位图也在 UI 线程上落：渲染器本来就在这个线程读它，锁与画不会打架。
                    // 尺寸没变就一直复用同一张，不重烘也不新建。
                    if (panel.Bitmap is not { } bitmap
                        || bitmap.PixelSize.Width != job.Width || bitmap.PixelSize.Height != job.Height)
                    {
                        panel.Bitmap?.Dispose();
                        panel.Bitmap = bitmap = new WriteableBitmap(new PixelSize(job.Width, job.Height),
                                                                    new Vector(96, 96), PixelFormat.Bgra8888,
                                                                    AlphaFormat.Premul);
                    }

                    using (var frame = bitmap.Lock())
                        CopyInto(frame, result, job.Width, job.Height);

                    panel.Canvas = result;
                    panel.Px = job.Px;
                    panel.Py = job.Py;
                    panel.BakedRect = job.Rect;
                    panel.BakedRadius = job.Radius;
                    panel.BakedVersion = job.Version;
                    panel.BakedScale = job.Scale;
                    panel.NeedsBake = false;
                }

                if (stale) _glassDirty = true;

                InvalidateVisual();
                if (_glassDirty) TryStartBake();
            });
        });
    }

    /// <summary>玻璃背后那块画面的采样器（清晰图 / 留白处的模糊填充 / 纯底色 / 明暗遮罩）。</summary>
    private LayerBackdrop BuildBackdrop(double scale, int layerW, int layerH)
    {
        var device = new Size(layerW, layerH);
        var blurImageSize = _blur?.PixelSize ?? default;

        // 折射采样必须走跟外面那张清晰图**同一套** ComputeDestRect，里外才能严丝合缝。
        return new LayerBackdrop
        {
            Sharp = _sharpPixels,
            SharpSize = _sharpPixelsSize,
            SharpDest = _sharpPixels is null
                ? default
                : BackgroundImageMap.ComputeDestRect(_sharpPixelsSize, device, _stretch),
            Blur = _blurPixels,
            BlurSize = blurImageSize,
            BlurDest = _blurPixels is null
                ? default
                : BackgroundImageMap.ComputeDestRect(blurImageSize, device, _stretch),
            // 留白处的填充与模糊那一路是同一张快照，别再拷一份（拷一次就是 8 MB 垃圾）
            Fill = _blurPixels,
            FillSize = _blurPixels is null ? default : blurImageSize,
            FillDest = _blurPixels is null
                ? default
                : BackgroundImageMap.ComputeDestRect(blurImageSize, device, (int)BackgroundImageStretch.FillCrop),
            ThemeBackground = _themeBg,
            VeilAlpha = Math.Abs(_dim) < 0.5 ? 0.0 : Math.Min(1.0, Math.Abs(_dim) / 100.0),
            VeilColor = _dim < 0 ? Colors.Black : Colors.White,
            LayerWidth = layerW,
            LayerHeight = layerH,
        };
    }

    /// <summary>一块玻璃的折射图与它的缓存账。全部字段只在 UI 线程上读写。</summary>
    private sealed class GlassPanel
    {
        public required GlassTarget Target { get; init; }

        /// <summary>当前矩形（每轮布局同步一次）。</summary>
        public Rect Rect;
        public double Radius;

        /// <summary>手上这张位图是按哪份参数烘的；与当前值对不上就得重烘。</summary>
        public Rect BakedRect;
        public double BakedRadius = -1;
        public int BakedVersion = -1;
        public double BakedScale = -1;

        public WriteableBitmap? Bitmap;   // 这块的折射图（只有这块那么大，不是整屏）
        public uint[]? Canvas;            // 烘它的复用缓冲，尺寸跟位图一致

        /// <summary>位图外接框在层里的整数设备像素原点（落位用，见 DrawPanel）。</summary>
        public int Px;
        public int Py;

        public bool NeedsBake;

        public void Dispose()
        {
            Bitmap?.Dispose();
            Bitmap = null;
            Canvas = null;
        }
    }

    /// <summary>一趟烘焙里的一块：参数在 UI 线程上取好，后台只读它。</summary>
    private sealed class BakeJob
    {
        public required GlassPanel Panel { get; init; }
        public Rect Rect;
        public double Radius;
        public int Version;
        public double Scale;

        public int Px;
        public int Py;
        public int Width;
        public int Height;
        public double PanelX;
        public double PanelY;
        public double PanelWidth;
        public double PanelHeight;
        public double RadiusDevice;

        public uint[]? Canvas;    // 复用的缓冲（尺寸对得上才有值）
        public uint[]? Result;    // 算出来的像素；null = 这一块算崩了
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
                        blur = BakeBlur(sharp, BlurRadiusInImagePixels(radius, sharp.PixelSize, pixels, stretch));
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
                    _contentVersion++;   // 底下的画面换了一张：手上那些块的折射像素全不作数了
                    MarkGlassDirty();
                    InvalidateVisual();
                }
                TryStartRebuild(Bounds.Size);
            });
        });
    }

    /// <summary>
    /// 把「屏幕上多少像素」的模糊半径换成底图自己的像素。
    ///
    /// 两个尺寸都得按**实际落位**算：底图（<paramref name="imageSize"/>）铺在这一层上占
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
