using Avalonia.Media;
using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Helpers;

/// <summary>
/// 一套调色板对应的画刷实例。整批一次性构造、切换主题时整批替换，
/// 界面绑到 <c>Chrome.Xxx</c> 上：换主题只需通知一次 Chrome 属性，不必逐个通知十几个颜色。
/// 画刷实例缓存复用——每次刷新都新建画刷会让控件反复失效重绘、观感发顿。
/// </summary>
public sealed class ClockBrushes
{
    /// <summary>这套画刷是浅底（明亮主题）还是深底。语义色（五档/预警/计数）据此选深浅版本。</summary>
    public bool IsLight { get; }

    public IBrush TextPrimary { get; }
    public IBrush TextBody { get; }
    public IBrush TextSecondary { get; }
    public IBrush TextMuted { get; }
    public IBrush TextFaint { get; }
    public IBrush Countdown { get; }
    public IBrush Divider { get; }
    public IBrush ButtonForeground { get; }
    public IBrush ButtonBackground { get; }
    public IBrush ButtonHover { get; }
    public IBrush PopupBackground { get; }
    public IBrush PopupBorder { get; }
    public IBrush PopupText { get; }
    public IBrush VolumeTrack { get; }
    public IBrush PanelBackground { get; }

    private ClockBrushes(ClockThemePalette p)
    {
        IsLight = p.IsLight;
        TextPrimary = Solid(p.TextPrimary);
        TextBody = Solid(p.TextBody);
        TextSecondary = Solid(p.TextSecondary);
        TextMuted = Solid(p.TextMuted);
        TextFaint = Solid(p.TextFaint);
        Countdown = Solid(p.Countdown);
        Divider = Solid(p.Divider);
        ButtonForeground = Solid(p.ButtonForeground);
        ButtonBackground = Solid(p.ButtonBackground);
        ButtonHover = Solid(p.ButtonHover);
        PopupBackground = Solid(p.PopupBackground);
        PopupBorder = Solid(p.PopupBorder);
        PopupText = Solid(p.PopupText);
        VolumeTrack = Solid(p.VolumeTrack);
        PanelBackground = Solid(p.PanelBackground);
    }

    public static ClockBrushes From(ClockThemePalette palette) => new(palette);

    private static IBrush Solid(string hex) => new SolidColorBrush(Color.Parse(hex));
}
