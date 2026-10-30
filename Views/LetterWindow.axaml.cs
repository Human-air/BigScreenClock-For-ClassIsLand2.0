using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 「致所有用户的一封信」。打开大屏时钟设置页时弹一次（每次启动 CI 只弹一次），
/// 右上角 × / 底部「我知道了」关闭。勾了「不再显示」再关，就再也不弹了。
///
/// 配色跟 ClassIsland 的亮 / 暗主题走（这个窗口在设置页里，不是压在大屏上）。
/// </summary>
public partial class LetterWindow : Window
{
    /// <summary>这次启动已经弹过了没有。静态字段：一个会话只打扰一次。</summary>
    private static bool _shownThisSession;

    public LetterWindow()
    {
        InitializeComponent();

        ApplyTheme();
        ActualThemeVariantChanged += (_, _) => ApplyTheme();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) CommitClose();
        };
    }

    /// <summary>
    /// 该不该弹：没勾过「不再显示」，而且这次启动还没弹过。
    /// </summary>
    public static bool ShouldShow()
        => !_shownThisSession && Plugin.Settings is { LetterDismissed: false };

    /// <summary>标记这次启动弹过了（先标，免得排版期间又进来一次）。</summary>
    public static void MarkShown() => _shownThisSession = true;

    private void ApplyTheme()
    {
        var light = ActualThemeVariant != ThemeVariant.Dark;

        PanelBorder.Background = new SolidColorBrush(light
            ? Color.FromRgb(0xFF, 0xFF, 0xFF)
            : Color.FromRgb(0x1E, 0x1E, 0x24));

        PanelBorder.BorderBrush = new SolidColorBrush(light
            ? Color.FromArgb(0x22, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));

        var title = light ? Color.FromRgb(0x14, 0x14, 0x14) : Colors.White;
        var body = light ? Color.FromArgb(0xE6, 0x14, 0x14, 0x14) : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF);
        var faint = light ? Color.FromArgb(0x88, 0x00, 0x00, 0x00) : Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF);

        TitleIcon.Foreground = new SolidColorBrush(title);
        TitleText.Foreground = new SolidColorBrush(title);
        CloseIcon.Foreground = new SolidColorBrush(faint);

        foreach (var block in new[]
                 {
                     TextFont, TextIntro, TextVersions, TextVer2, TextVer5, TextVer8,
                     TextAi, TextEnd, TextPlan, TextContact, TextThanks, SignName, SignDate,
                 })
        {
            block.Foreground = new SolidColorBrush(body);
        }

        DontShowAgainBox.Foreground = new SolidColorBrush(faint);

        OkButton.Foreground = new SolidColorBrush(light ? Color.FromRgb(0xFF, 0xFF, 0xFF) : Color.FromRgb(0x14, 0x14, 0x14));
        OkButton.Background = new SolidColorBrush(light ? Color.FromRgb(0x00, 0x66, 0xCC) : Color.FromRgb(0xFF, 0xFF, 0xFF));
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e) => CommitClose();

    /// <summary>关窗：勾了「不再显示」就把开关落进设置（CI 会自己存盘）。</summary>
    private void CommitClose()
    {
        if (DontShowAgainBox.IsChecked == true && Plugin.Settings is { } settings)
            settings.LetterDismissed = true;

        Close();
    }
}
