using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 关于窗口：显示制作者、版本号（正式 / 测试 / 开发版）、项目仓库与演示视频链接。
/// 60 秒自动关闭，点击窗口外（失焦）也会关闭。
///
/// 配色跟大屏主题走：暗底白字 / 亮底黑字。大屏后面是磨砂或液态玻璃时就半透明，
/// 让身后被糊掉的大屏透出来（模糊由 ClockSurface 在弹出期间给整面墙套 Effect 做）。
/// </summary>
public partial class AboutPopupWindow : Window
{
    private const string RepoUrl = "https://github.com/Human-air/BigScreenClock-For-ClassIsLand2.0";

    private const string VideoUrl =
        "https://www.bilibili.com/video/BV1b7tH6EEmj/?spm_id_from=333.1387.homepage.video_card.click&vd_source=672e65ac988bbb49080779dccc57cdb2";

    /// <summary>从插件目录的 manifest.yml 动态读取版本号，不再写死（避免升级后忘记同步）。</summary>
    private static readonly string PluginVersion = ReadManifestVersion();

    private readonly DispatcherTimer _autoCloseTimer;
    private readonly DateTime _shownAt = DateTime.Now;

    /// <summary>
    /// 读取插件安装目录下 manifest.yml 的 version 字段。
    /// manifest.yml 由 csproj 复制到输出目录，与 DLL 同目录，运行时可取到。
    /// </summary>
    private static string ReadManifestVersion()
    {
        try
        {
            var dir = Path.GetDirectoryName(typeof(AboutPopupWindow).Assembly.Location);
            if (!string.IsNullOrEmpty(dir))
            {
                var manifestPath = Path.Combine(dir, "manifest.yml");
                if (File.Exists(manifestPath))
                {
                    foreach (var line in File.ReadAllLines(manifestPath))
                    {
                        var trimmed = line.TrimStart();
                        if (trimmed.StartsWith("version:", StringComparison.OrdinalIgnoreCase))
                        {
                            var v = trimmed["version:".Length..].Trim().Trim('"', '\'');
                            if (v.Length > 0) return v;
                        }
                    }
                }
            }
        }
        catch { }
        return "未知";
    }

    /// <param name="light">大屏当前是明亮主题还是黑暗主题（文字、底色跟着翻）。</param>
    /// <param name="frosted">身后的大屏是不是磨砂/液态玻璃档（是就半透明，让糊掉的画面透出来）。</param>
    public AboutPopupWindow(bool light = false, bool frosted = false)
    {
        InitializeComponent();

        ApplyTheme(light, frosted);

        // 版本号 + 版本类型：
        //   五位（如 1.1.1.10.6）= 本机开发版，构建时自动盖号，不对外发布；
        //   四位最后一位非 0 = 测试版，为 0 = 正式版。
        VersionNumberText.Text = $"版本号：{PluginVersion}";
        var (type, color) = VersionKind(PluginVersion);
        VersionTypeText.Text = type;
        VersionTypeText.Foreground = new SolidColorBrush(Color.Parse(color));

        RepoLinkText.PointerPressed += (_, _) => OpenUrl(RepoUrl);
        VideoLinkText.PointerPressed += (_, _) => OpenUrl(VideoUrl);

        // 60 秒自动关闭
        _autoCloseTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _autoCloseTimer.Tick += (_, _) => Close();
        _autoCloseTimer.Start();

        // 点击窗口外 → 失焦 → 关闭（加 0.5 秒宽限期，避免刚显示就误关）
        Deactivated += (_, _) =>
        {
            if ((DateTime.Now - _shownAt).TotalMilliseconds > 500) Close();
        };
        Closed += (_, _) => _autoCloseTimer.Stop();
    }

    private void ApplyTheme(bool light, bool frosted)
    {
        // 有糊掉的画面在身后：留一点透明，透出来才看得出是磨砂；没有就纯实色，保证字看得清
        PanelBorder.Background = new SolidColorBrush(light
            ? (frosted ? Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF) : Color.FromRgb(0xFF, 0xFF, 0xFF))
            : (frosted ? Color.FromArgb(0xC4, 0x14, 0x14, 0x1F) : Color.FromRgb(0x14, 0x14, 0x1F)));

        // 圆角边上压一道极淡的描边：亮底上白面板才不会糊进亮背景里
        PanelBorder.BorderBrush = new SolidColorBrush(light
            ? Color.FromArgb(0x24, 0x00, 0x00, 0x00)
            : Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));

        var title = light ? Color.FromRgb(0x14, 0x14, 0x14) : Colors.White;
        var body = light ? Color.FromArgb(0xCC, 0x14, 0x14, 0x14) : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
        var faint = light ? Color.FromArgb(0x66, 0x00, 0x00, 0x00) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
        var link = light ? Color.FromRgb(0x00, 0x66, 0xCC) : Color.FromRgb(0x6C, 0xB6, 0xFF);
        var divider = light ? Color.FromArgb(0x40, 0x00, 0x00, 0x00) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);

        TitleIcon.Foreground = new SolidColorBrush(title);
        TitleText.Foreground = new SolidColorBrush(title);
        VersionNumberText.Foreground = new SolidColorBrush(title);
        AuthorText.Foreground = new SolidColorBrush(body);
        HintText.Foreground = new SolidColorBrush(faint);
        RepoLinkText.Foreground = new SolidColorBrush(link);
        VideoLinkText.Foreground = new SolidColorBrush(link);
        DividerLine.Fill = new SolidColorBrush(divider);
    }

    /// <summary>版本类型徽标：五位 = 开发版；四位最后一位非 0 = 测试版，为 0 = 正式版。</summary>
    private static (string Text, string Color) VersionKind(string version)
    {
        var parts = version.Split('.');

        if (parts.Length >= 5) return ("开发版", "#ff9800");

        var last = parts.Length >= 4 && int.TryParse(parts[^1], out var n) ? n : 0;
        return last != 0 ? ("测试版", "#ff5555") : ("正式版", "#4caf50");
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { }
    }
}
