using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 翻页时钟里的一位数字卡片：后层常驻旧数字，上层新牌绕顶边从上往下展开盖入。
/// 字号 / 颜色 / 字体 / 牌面底色全部从外层时钟绑定过来，本身不持有配色。
/// </summary>
public partial class FlipDigit : UserControl
{
    public static readonly StyledProperty<string> DigitProperty =
        AvaloniaProperty.Register<FlipDigit, string>(nameof(Digit), "0");

    public static readonly StyledProperty<IBrush?> CardBackgroundProperty =
        AvaloniaProperty.Register<FlipDigit, IBrush?>(nameof(CardBackground));

    public static readonly StyledProperty<IBrush?> CardBorderProperty =
        AvaloniaProperty.Register<FlipDigit, IBrush?>(nameof(CardBorder));

    public static readonly StyledProperty<IBrush?> DividerBrushProperty =
        AvaloniaProperty.Register<FlipDigit, IBrush?>(nameof(DividerBrush));

    public static readonly StyledProperty<IBrush?> PlateBackgroundProperty =
        AvaloniaProperty.Register<FlipDigit, IBrush?>(nameof(PlateBackground));

    public static readonly StyledProperty<IBrush?> FlapBackgroundProperty =
        AvaloniaProperty.Register<FlipDigit, IBrush?>(nameof(FlapBackground));

    public static readonly StyledProperty<FontFamily?> ClockFontFamilyProperty =
        AvaloniaProperty.Register<FlipDigit, FontFamily?>(nameof(ClockFontFamily));

    /// <summary>翻牌动画时长（毫秒）。大屏每秒跳一次，动画不能拖到下一秒。</summary>
    private static readonly TimeSpan FlipDuration = TimeSpan.FromMilliseconds(300);

    private bool _contentLoaded;

    /// <summary>
    /// 翻牌动画代际序号。数字连续跳变时上一轮异步动画的延迟回调可能晚于新一轮执行，
    /// 这里每次新值自增；旧代际的回调一律不回写可见状态，避免把数字覆盖成旧值。
    /// </summary>
    private int _flipGeneration;

    public FlipDigit()
    {
        InitializeComponent();

        FlapPlate.RenderTransform = new ScaleTransform(1, 1);
        _contentLoaded = true;
        BackText.Text = Digit;
        FlapText.Text = Digit;
        SyncTextStyle();
    }

    /// <summary>当前显示的数字（一位 0~9）。</summary>
    public string Digit
    {
        get => GetValue(DigitProperty);
        set => SetValue(DigitProperty, value);
    }

    public IBrush? CardBackground
    {
        get => GetValue(CardBackgroundProperty);
        set => SetValue(CardBackgroundProperty, value);
    }

    public IBrush? CardBorder
    {
        get => GetValue(CardBorderProperty);
        set => SetValue(CardBorderProperty, value);
    }

    public IBrush? DividerBrush
    {
        get => GetValue(DividerBrushProperty);
        set => SetValue(DividerBrushProperty, value);
    }

    public IBrush? PlateBackground
    {
        get => GetValue(PlateBackgroundProperty);
        set => SetValue(PlateBackgroundProperty, value);
    }

    public IBrush? FlapBackground
    {
        get => GetValue(FlapBackgroundProperty);
        set => SetValue(FlapBackgroundProperty, value);
    }

    public FontFamily? ClockFontFamily
    {
        get => GetValue(ClockFontFamilyProperty);
        set => SetValue(ClockFontFamilyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 外层绑定过来的字号 / 颜色 / 字体 / 等宽特性变了，显式同步到两块数字文本框。
        if (change.Property == FontSizeProperty ||
            change.Property == ForegroundProperty ||
            change.Property == ClockFontFamilyProperty ||
            change.Property == FontFeaturesProperty)
        {
            SyncTextStyle();
        }

        if (change.Property == DigitProperty)
        {
            var oldDigit = change.OldValue as string ?? "";
            var newDigit = change.NewValue as string ?? "";

            if (!_contentLoaded || !IsLoaded || oldDigit == newDigit)
            {
                BackText.Text = newDigit;
                FlapText.Text = newDigit;
                ResetFlap();
                return;
            }

            var generation = ++_flipGeneration;
            _ = PlayFlipAsync(generation, oldDigit, newDigit);
        }
    }

    /// <summary>把上层翻入牌复位到展开、盖在后牌上的静止状态。</summary>
    private void ResetFlap()
    {
        if (FlapPlate.RenderTransform is ScaleTransform scale)
        {
            scale.ScaleY = 1;
        }
        FlapPlate.Opacity = 1;
    }

    /// <summary>把外层传入的字号 / 颜色 / 字体 / 等宽特性同步到两块数字文本框。</summary>
    private void SyncTextStyle()
    {
        if (!_contentLoaded) return;

        var family = ClockFontFamily ?? FontFamily;
        foreach (var tb in new[] { BackText, FlapText })
        {
            tb.FontSize = FontSize;
            tb.Foreground = Foreground;
            tb.FontFamily = family;
            tb.FontFeatures = FontFeatures;
        }

        // 卡片高度按字号显式给定：Viewbox 做无限高度测量时期望尺寸必须有限，
        // 否则内部星号行会把高度喂到饱和值，整牌被等比缩成一条看不见的线。
        double cardH = FontSize > 0 ? FontSize * 1.25 : double.NaN;
        Height = cardH;
    }

    /// <summary>
    /// 播一次翻入：后层留旧数字，上层新牌绕顶边从上往下展开盖满，结束后后层换成新值、藏起上层。
    /// 代际过期或控件卸载就不再回写界面。
    /// </summary>
    private async Task PlayFlipAsync(int generation, string oldDigit, string newDigit)
    {
        BackText.Text = oldDigit;
        FlapText.Text = newDigit;

        if (FlapPlate.RenderTransform is not ScaleTransform scale)
        {
            scale = new ScaleTransform(1, 1);
            FlapPlate.RenderTransform = scale;
        }
        scale.ScaleY = 0;
        FlapPlate.Opacity = 1;

        var flap = new Animation
        {
            Duration = FlipDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(ScaleTransform.ScaleYProperty, 0.0),
                        new Setter(OpacityProperty, 1.0)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(ScaleTransform.ScaleYProperty, 1.0),
                        new Setter(OpacityProperty, 1.0)
                    }
                }
            }
        };

        var run = flap.RunAsync(FlapPlate);
        await run;
        if (!IsLoaded || generation != _flipGeneration) return;

        // 展开盖满后：后层落新值，上层藏起归位，不留残片盖在后牌上。
        BackText.Text = newDigit;
        FlapPlate.Opacity = 0;
        FlapText.Text = newDigit;
        if (FlapPlate.RenderTransform is ScaleTransform doneScale)
        {
            doneScale.ScaleY = 1;
        }
    }
}
