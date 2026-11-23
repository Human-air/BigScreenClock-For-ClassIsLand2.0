using System;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 翻页时钟里的一位数字卡片：圆角卡片 + 中缝，数字变化时旧数字向下翻出、新数字从上方翻入。
/// 样式（字号 / 颜色 / 字体 / 卡片底色描边）全部从外层时钟绑定过来，本身不持有配色。
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

    public static readonly StyledProperty<FontFamily?> ClockFontFamilyProperty =
        AvaloniaProperty.Register<FlipDigit, FontFamily?>(nameof(ClockFontFamily));

    /// <summary>翻牌动画时长（毫秒）。大屏每秒跳一次，动画不能拖到下一秒。</summary>
    private static readonly TimeSpan FlipDuration = TimeSpan.FromMilliseconds(280);

    private bool _contentLoaded;

    public FlipDigit()
    {
        InitializeComponent();

        OldText.RenderTransform = new TranslateTransform();
        NewText.RenderTransform = new TranslateTransform();
        _contentLoaded = true;
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

    public FontFamily? ClockFontFamily
    {
        get => GetValue(ClockFontFamilyProperty);
        set => SetValue(ClockFontFamilyProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        // 外层绑定过来的字号 / 颜色 / 字体 / 等宽特性变了，显式同步到两个数字文本框；
        // 不走 XAML 继承，避免 UserControl 边界上继承链路不稳定把字号塌成默认值。
        if (change.Property == FontSizeProperty ||
            change.Property == ForegroundProperty ||
            change.Property == ClockFontFamilyProperty ||
            change.Property == FontFeaturesProperty)
        {
            SyncTextStyle();
        }

        if (change.Property != DigitProperty || !_contentLoaded) return;

        var oldDigit = change.OldValue as string ?? "";
        var newDigit = change.NewValue as string ?? "";
        PlayFlip(oldDigit, newDigit);
    }

    /// <summary>把外层传入的字号 / 颜色 / 字体 / 等宽特性同步到旧、新两个数字文本框。</summary>
    private void SyncTextStyle()
    {
        if (!_contentLoaded) return;

        var family = ClockFontFamily ?? FontFamily;
        foreach (var tb in new[] { OldText, NewText })
        {
            tb.FontSize = FontSize;
            tb.Foreground = Foreground;
            tb.FontFamily = family;
            tb.FontFeatures = FontFeatures;
        }
    }

    /// <summary>
    /// 播一次翻牌：旧数字向下翻出并淡出，新数字从上方翻入并淡入。
    /// 还没挂上可视树（设置页预览初次绑定）时不播动画，直接落新值。
    /// </summary>
    private void PlayFlip(string oldDigit, string newDigit)
    {
        if (oldDigit == newDigit)
        {
            NewText.Text = newDigit;
            return;
        }

        if (!IsLoaded)
        {
            OldText.Text = "";
            NewText.Text = newDigit;
            return;
        }

        OldText.Text = oldDigit;
        NewText.Text = newDigit;

        var travel = FontSize * 0.18;

        var oldOut = new Animation
        {
            Duration = FlipDuration,
            Easing = new CubicEaseIn(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame
                {
                    Cue = new Cue(0d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.YProperty, 0.0)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.YProperty, travel)
                    }
                }
            }
        };

        var newIn = new Animation
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
                        new Setter(OpacityProperty, 0.0),
                        new Setter(TranslateTransform.YProperty, -travel)
                    }
                },
                new KeyFrame
                {
                    Cue = new Cue(1d),
                    Setters =
                    {
                        new Setter(OpacityProperty, 1.0),
                        new Setter(TranslateTransform.YProperty, 0.0)
                    }
                }
            }
        };

        _ = oldOut.RunAsync(OldText);
        _ = newIn.RunAsync(NewText);
    }
}
