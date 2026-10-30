using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 内容层上的磨砂底（比如大屏中央弹出的地震提醒卡片）。
///
/// 为什么不把卡片登记成 <see cref="BackdropLayer.GlassTargets"/>：玻璃层画在所有内容的**下面**，
/// 居中卡片的底下正好压着时钟，登记进去磨砂会跑到时钟下面，时钟的字反而浮在磨砂上面。
/// 所以由卡片自己画——但像素还是从背景层那张整屏模糊图上按同一套映射裁，
/// 这样卡片里透出来的画面和外面的磨砂是连着的。
///
/// 用法：放在卡片的 <c>Panel</c> 里当第一层，<see cref="Layer"/> 指向同一棵树里的背景层。
/// 没有模糊图时什么也不画，由卡片自己的底色兜着。
/// </summary>
public sealed class FrostedCardBackdrop : Control
{
    public static readonly StyledProperty<double> RadiusProperty =
        AvaloniaProperty.Register<FrostedCardBackdrop, double>(nameof(Radius), 12);

    /// <summary>卡片自己的语义色（烈度色），压在磨砂上面。透明时可留 null。</summary>
    public static readonly StyledProperty<IBrush?> TintProperty =
        AvaloniaProperty.Register<FrostedCardBackdrop, IBrush?>(nameof(Tint));

    /// <summary>同一棵树里的背景层。必须设，否则这块就是空的。</summary>
    public static readonly StyledProperty<BackdropLayer?> LayerProperty =
        AvaloniaProperty.Register<FrostedCardBackdrop, BackdropLayer?>(nameof(Layer));

    private BackdropLayer? _hooked;

    static FrostedCardBackdrop()
    {
        AffectsRender<FrostedCardBackdrop>(RadiusProperty, TintProperty, LayerProperty);
    }

    public double Radius
    {
        get => GetValue(RadiusProperty);
        set => SetValue(RadiusProperty, value);
    }

    public IBrush? Tint
    {
        get => GetValue(TintProperty);
        set => SetValue(TintProperty, value);
    }

    public BackdropLayer? Layer
    {
        get => GetValue(LayerProperty);
        set => SetValue(LayerProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == LayerProperty) HookLayer(Layer);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        HookLayer(null);
    }

    /// <summary>换图 / 改明暗 / 换留白底色之后模糊图变了，这块得跟着重画。</summary>
    private void HookLayer(BackdropLayer? layer)
    {
        if (ReferenceEquals(_hooked, layer)) return;

        if (_hooked is not null) _hooked.BackdropChanged -= OnBackdropChanged;
        _hooked = layer;
        if (_hooked is not null) _hooked.BackdropChanged += OnBackdropChanged;

        InvalidateVisual();
    }

    private void OnBackdropChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        if (Layer is not { } layer) return;
        if (Bounds.Width <= 1 || Bounds.Height <= 1) return;

        // 这块在背景层坐标里的位置：由它算该从模糊图的哪儿裁
        if (this.TranslatePoint(default, layer) is not { } origin) return;

        layer.DrawFrostedCard(context, new Rect(origin, Bounds.Size), Radius, Tint);
    }
}
