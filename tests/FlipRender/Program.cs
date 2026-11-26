using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using EveningSelfStudyClock.Views;

// 离屏（Headless + Skia）渲染真实 FlipDigit，验证半牌布局、文本落值与翻页切换，
// 不必拉起 ClassIsland 大屏。纯断言脚本，不进入插件产物。

try
{
Run();
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    File.WriteAllText(Path.Combine(Path.GetTempPath(), "fliprender-crash.txt"), ex.ToString());
    Environment.Exit(1);
    return;
}

static void Run()
{

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia()
    .SetupWithoutStarting();

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

void Pump(Window window)
{
    using var cts = new CancellationTokenSource(20);
    try { Dispatcher.UIThread.MainLoop(cts.Token); }
    catch (OperationCanceledException) { }
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}

IReadOnlyList<TextBlock> Digits(FlipDigit digit) =>
    digit.GetVisualDescendants().OfType<TextBlock>().ToList();

// 后层常驻牌（上层翻入牌翻完应隐藏）。
IEnumerable<TextBlock> BackOnly(FlipDigit digit) =>
    digit.GetVisualDescendants().OfType<TextBlock>()
        .Where(t => t.Name == "BackText");

var digit = new FlipDigit
{
    Width = 200,
    Height = 240,
    FontSize = 120,
    Foreground = Brushes.White,
    Digit = "7",
    CardBackground = new SolidColorBrush(Color.Parse("#2A2A35")),
    CardBorder = new SolidColorBrush(Color.Parse("#AAAAAA")),
    DividerBrush = new SolidColorBrush(Color.Parse("#888888")),
    PlateBackground = new SolidColorBrush(Color.Parse("#4A4A58")),
    FlapBackground = new SolidColorBrush(Color.Parse("#4A4A58")),
    ClockFontFamily = FontFamily.Parse("avares://EveningSelfStudyClock/Assets/Fonts/DSEG7Classic-Bold.ttf#DSEG7 Classic"),
    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
};

var root = new Grid();
root.Children.Add(digit);
var window = new Window { Width = 420, Height = 300, Content = root, Background = Brushes.Black };
window.Show();
Pump(window);

Check(digit.Bounds.Width > 0 && digit.Bounds.Height > 0, "card lays out with non-zero bounds");
var initialFrame = window.CaptureRenderedFrame();
initialFrame?.Save(Path.Combine(Path.GetTempPath(), "flip-digit-initial.png"));
var initial = Digits(digit);
Check(initial.Count == 2, "back plate and flap plate text blocks present");
Check(initial.All(t => t.Text == "7"), "initial digit rendered on both plates");

// 切到新值：上层翻入牌先带上新值展开。
digit.Digit = "3";
Pump(window);
Check(Digits(digit).Any(t => t.Text == "3"), "new digit lands on the incoming flap");

// 等一段翻牌动画结束后，后层常驻牌应是新值；隐藏的上层牌不算在内。
Thread.Sleep(400);
Pump(window);
bool Visible(TextBlock t) => t.GetVisualAncestors().All(a => a.Opacity > 0.05);
Check(Digits(digit).Where(Visible).All(t => t.Text == "3"), "after flip settles, visible plate shows the new digit");

// 遍历 0~9，任何一位都不能出现空白文本。
foreach (var d in "0123456789")
{
    digit.Digit = d.ToString();
    Pump(window);
}
Thread.Sleep(400);
Pump(window);
Check(Digits(digit).All(t => !string.IsNullOrEmpty(t.Text)), "no blank digit across 0-9");

// 截一张全亮数字 8 的离屏帧，核对半牌裁剪与数码管字形。
digit.Digit = "8";
Pump(window);
Thread.Sleep(400);
Pump(window);
// 回归守护：连发 0-9 后静止，后层常驻牌必须是最终值 8，
// 旧代际动画回调不得把数字覆盖成旧值、上层旧牌不得残留可见。
Check(BackOnly(digit).All(t => t.Text == "8"), "after rapid 0-9 cycling, back plate settles on 8");
var frame = window.CaptureRenderedFrame();
var pngPath = Path.Combine(Path.GetTempPath(), "flip-digit.png");
frame?.Save(pngPath);
Console.WriteLine("IMAGE " + pngPath);

window.Close();
Dispatcher.UIThread.RunJobs();

// 模拟真机：卡片不设宽高，放在横向 StackPanel 里靠内容撑开，核对 Bounds 是否塌成 0。
var auto = new FlipDigit
{
    FontSize = 120,
    Foreground = Brushes.White,
    Digit = "7",
    CardBackground = new SolidColorBrush(Color.Parse("#2A2A35")),
    CardBorder = new SolidColorBrush(Color.Parse("#AAAAAA")),
    DividerBrush = new SolidColorBrush(Color.Parse("#888888")),
    PlateBackground = new SolidColorBrush(Color.Parse("#4A4A58")),
    FlapBackground = new SolidColorBrush(Color.Parse("#4A4A58")),
    ClockFontFamily = FontFamily.Parse("avares://EveningSelfStudyClock/Assets/Fonts/DSEG7Classic-Bold.ttf#DSEG7 Classic"),
};
var hpanel = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 12 };
hpanel.Children.Add(auto);
var vb = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = hpanel };
var w2 = new Window { Width = 900, Height = 400, Content = vb, Background = Brushes.Black };
w2.Show();
Pump(w2);
// 回归守护：内部星号行曾在 Viewbox 无限高度测量下把期望高度喂到饱和值（2.8 亿），
// 整牌被等比缩成一条看不见的线。这里断言期望尺寸有限、Viewbox 实际宽度非零。
Check(auto.DesiredSize.Height is > 0 and < 1000, "card desired height stays finite under viewbox");
Check(vb.Bounds.Width > 10, "viewbox lays the card out at a visible width");
w2.CaptureRenderedFrame()?.Save(Path.Combine(Path.GetTempPath(), "flip-auto.png"));
w2.Close();
Dispatcher.UIThread.RunJobs();

Console.WriteLine($"PASS: {checks} flip digit offscreen rendering checks");
}
