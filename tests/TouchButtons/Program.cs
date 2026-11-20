using System.Globalization;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using EveningSelfStudyClock.Helpers;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions())
    .SetupWithoutStarting();
Application.Current!.Styles.Add(new FluentTheme());

// 使用生产 XAML 中的配置，避免测试按钮与实际按钮的 ClickMode / 点击区域漂移。
var document = XDocument.Load(args.Length > 0 ? args[0] : Path.Combine(AppContext.BaseDirectory, "ClockSurface.axaml"));
XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}

foreach (var name in new[] { "ExitButton", "MinimizeButton" })
{
    var markup = document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
    double Number(string attribute) => double.Parse((string?)markup.Attribute(attribute) ?? "0", CultureInfo.InvariantCulture);
    var button = new Button
    {
        Content = name,
        ClickMode = Enum.Parse<ClickMode>((string?)markup.Attribute("ClickMode") ?? "Release"),
        MinHeight = Number("MinHeight"),
        Padding = Thickness.Parse((string)markup.Attribute("Padding")!),
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top
    };
    int clicks = 0;
    button.Click += (_, _) => clicks++;
    var window = new Window { Width = 400, Height = 200, Content = button };
    window.Show();
    window.UpdateLayout();

    foreach (var type in new[] { PointerType.Touch, PointerType.Pen, PointerType.Mouse })
    {
        var pointer = new Pointer(1, type, true);
        var before = clicks;
        button.RaiseEvent(new PointerPressedEventArgs(button, pointer, window, new Point(10, 10), 1,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None, 1));
        Check(clicks == before + 1, $"{name}/{type}: respond before finger or pointer release");
        // 抬手时滑出按钮：旧的 Release 模式在这里会取消点击。
        button.RaiseEvent(new PointerReleasedEventArgs(button, pointer, window, new Point(350, 150), 2,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        Check(clicks == before + 1, $"{name}/{type}: outside release must not activate twice");
    }

    Check(button.Padding == new Thickness(12, 6), $"{name}: original compact padding is preserved");

    var count = clicks;
    var mouse = new Pointer(2, PointerType.Mouse, true);
    button.RaiseEvent(new PointerPressedEventArgs(button, mouse, window, new Point(10, 10), 3,
        new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed),
        KeyModifiers.None, 1));
    Check(clicks == count, $"{name}: right mouse button must not activate");

    button.Focus();
    window.KeyPressQwerty(PhysicalKey.Space, RawInputModifiers.None);
    window.KeyReleaseQwerty(PhysicalKey.Space, RawInputModifiers.None);
    Check(clicks == count + 1, $"{name}: keyboard activation still works exactly once");
    window.Close();
    Dispatcher.UIThread.RunJobs();
}
// Overlay 测试从生产 XAML 摘取实际祖先层级和布局属性，不另写一套遮罩层级。
// 省略与按钮命中无关的时钟/课程内容，用固定的顶栏高度覆盖全屏与紧凑布局。
var surfaceMarkup = document.Root!.Elements().Single(e => e.Name.LocalName == "Grid");
var relevantNames = new HashSet<string>
{
    "Shell", "MainContent", "HeaderGrid", "FooterGrid", "ButtonBar",
    "SettingsButton", "MinimizeButton", "ExitButton", "QuickSettingsOverlay",
    "QuickSettingsVeil", "QuickSettingsDismissArea", "QuickSettingsCard"
};

foreach (var region in new[] { 0, 1, 2 })
foreach (var initiallyOpen in new[] { false, true })
foreach (var mode in new[] { ClickMode.Release, ClickMode.Press })
foreach (var targetName in new[] { "ExitButton", "MinimizeButton" })
{
    var named = new Dictionary<string, Control>();
    Control Build(XElement markup)
    {
        var name = (string?)markup.Attribute(x + "Name");
        Control control = markup.Name.LocalName switch
        {
            "Grid" => new Grid(),
            "StackPanel" => new StackPanel(),
            "Border" => new Border(),
            "Button" => new Button { Content = name == "SettingsButton" ? "⚙" : name == "MinimizeButton" ? "最小化" : "退出" },
            _ => throw new InvalidOperationException($"Unexpected test ancestor {markup.Name}")
        };
        if (name is not null) { control.Name = name; named.Add(name, control); }
        string? Text(string attribute) => (string?)markup.Attribute(attribute);
        if (Text("Margin") is { } margin) control.Margin = Thickness.Parse(margin);
        if (Text("HorizontalAlignment") is { } horizontal)
            control.HorizontalAlignment = Enum.Parse<HorizontalAlignment>(horizontal);
        if (Text("VerticalAlignment") is { } vertical)
            control.VerticalAlignment = Enum.Parse<VerticalAlignment>(vertical);
        if (Text("IsHitTestVisible") is { } hitTest) control.IsHitTestVisible = bool.Parse(hitTest);
        if (Text("Width") is { } width && !width.StartsWith('{'))
            control.Width = double.Parse(width, CultureInfo.InvariantCulture);
        foreach (var (attribute, setter) in new (string, Action<Control, int>)[]
        {
            ("Grid.Row", Grid.SetRow), ("Grid.Column", Grid.SetColumn),
            ("Grid.RowSpan", Grid.SetRowSpan), ("Grid.ColumnSpan", Grid.SetColumnSpan)
        })
            if (Text(attribute) is { } value) setter(control, int.Parse(value, CultureInfo.InvariantCulture));

        if (control is Grid grid)
        {
            var rows = Text("RowDefinitions") ?? string.Join(',', markup.Elements()
                .Where(e => e.Name.LocalName == "Grid.RowDefinitions")
                .Elements().Select(e => (string?)e.Attribute("Height") ?? "*"));
            var columns = Text("ColumnDefinitions");
            if (rows.Length > 0) grid.RowDefinitions = new RowDefinitions(rows);
            if (columns is not null) grid.ColumnDefinitions = new ColumnDefinitions(columns);
            if (Text("Background") is { } bg && !bg.StartsWith('{')) grid.Background = Brush.Parse(bg);
        }
        if (control is StackPanel stack)
        {
            if (Text("Orientation") is { } orientation) stack.Orientation = Enum.Parse<Orientation>(orientation);
            if (Text("Spacing") is { } spacing) stack.Spacing = double.Parse(spacing, CultureInfo.InvariantCulture);
        }
        if (control is Border border && Text("Background") is { } background && !background.StartsWith('{'))
            border.Background = Brush.Parse(background);
        if (control is Button button)
        {
            button.ClickMode = mode;
            if (Text("MinHeight") is { } minimum) button.MinHeight = double.Parse(minimum, CultureInfo.InvariantCulture);
            if (Text("Padding") is { } padding) button.Padding = Thickness.Parse(padding);
        }
        if (control is Panel panel)
            foreach (var child in markup.Elements().Where(e => relevantNames.Contains((string?)e.Attribute(x + "Name") ?? "")))
                panel.Children.Add(Build(child));
        return control;
    }

    var root = (Grid)Build(surfaceMarkup);
    var shell = (Grid)named["Shell"];
    var overlay = (Grid)named["QuickSettingsOverlay"];
    var card = (Border)named["QuickSettingsCard"];
    var dismissArea = (Border)named["QuickSettingsDismissArea"];
    var header = (Grid)named["HeaderGrid"];
    var footer = (Grid)named["FooterGrid"];
    var bar = (StackPanel)named["ButtonBar"];
    var target = (Button)named[targetName];
    var other = (Button)named[targetName == "ExitButton" ? "MinimizeButton" : "ExitButton"];
    const double surfaceWidth = 1280, surfaceHeight = 720;
    if (region != 0)
    {
        shell.Width = surfaceWidth / 2;
        shell.HorizontalAlignment = region == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        Grid.SetRow(bar, 1);
        footer.RowSpacing = 24;
        footer.Children.Add(new Border { Height = 32 });
    }
    header.Height = region == 0 ? 60 : 100;
    card.HorizontalAlignment = region == 1 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    overlay.IsVisible = initiallyOpen;
    int clicks = 0, otherClicks = 0, dismissals = 0;
    target.Click += (_, _) => clicks++;
    other.Click += (_, _) => otherClicks++;
    overlay.PointerPressed += (_, e) =>
    {
        for (var visual = e.Source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (ReferenceEquals(visual, card)) return;
        overlay.IsVisible = false;
        dismissals++;
    };
    var window = new Window { Width = surfaceWidth, Height = surfaceHeight, Content = root };
    window.Show();
    window.UpdateLayout();
    var headerBottom = header.TranslatePoint(new Point(0, header.Bounds.Height), root)!.Value.Y;
    var buttonTop = bar.TranslatePoint(default, root)!.Value.Y;
    var margins = QuickSettingsBounds.GetMargins(headerBottom, buttonTop, root.Bounds.Height);
    card.Margin = margins.Card;
    dismissArea.Margin = margins.DismissArea;
    window.UpdateLayout();

    var scenario = $"region={region}, open={initiallyOpen}, mode={mode}, button={targetName}";
    void ClickAt(Point point)
    {
        window.MouseMove(point, RawInputModifiers.None);
        window.MouseDown(point, MouseButton.Left, RawInputModifiers.None);
        window.MouseUp(point, MouseButton.Left, RawInputModifiers.None);
        Dispatcher.UIThread.RunJobs();
    }
    Point Center(Control control) => control.TranslatePoint(
        new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    if (initiallyOpen)
    {
        ClickAt(Center(card));
        Check(overlay.IsVisible && dismissals == 0, $"{scenario}: card content must not dismiss settings");
    }
    ClickAt(Center(target));
    Check(clicks == 1 && otherClicks == 0, $"{scenario}: first click must reach exactly the intended button");
    Check(dismissals == 0, $"{scenario}: button click must not be consumed by settings dismissal");
    if (initiallyOpen)
    {
        // 中间区域的空白仍应一击关闭设置，不能为修复按钮而让整层都穿透。
        var blank = dismissArea.TranslatePoint(new Point(dismissArea.Bounds.Width / 2, dismissArea.Bounds.Height / 2), window)!.Value;
        ClickAt(blank);
        Check(!overlay.IsVisible && dismissals == 1, $"{scenario}: middle blank area must dismiss settings once");
        Check(clicks == 1 && otherClicks == 0, $"{scenario}: dismissing settings must not activate a button");
    }
    window.Close();
    Dispatcher.UIThread.RunJobs();
}
// Exercise the production space reservation/scaling for the shared recording status on a narrow screen.
XElement Named(string name) => document.Descendants().Single(e => (string?)e.Attribute(x + "Name") == name);
var meterMarkup = Named("VolumeTitleRow");
var statusMarkup = Named("MeterRecordingStatus");
var counterMarkup = Named("CounterStrip");
var courseMarkup = Named("CourseInfoBox");
var courseName = courseMarkup.Descendants().Single(e => (string?)e.Attribute("Text") == "{Binding CourseInfoText}");
var courseExtras = courseMarkup.Descendants().Where(e =>
    (string?)e.Attribute("Text") is "{Binding CourseTimeText}" or "{Binding CourseTimeWidthSample}");
Check(courseExtras.Count() == 2 && courseExtras.All(e =>
    (string?)e.Attribute("FontWeight") == (string?)courseName.Attribute("FontWeight") &&
    (string?)e.Attribute("FontSize") == (string?)courseName.Attribute("FontSize")),
    "course time and its width sample use the same weight and size as the course name");
Check((string?)courseExtras.Single(e => (string?)e.Attribute("Text") == "{Binding CourseTimeText}").Attribute("Opacity") is null,
    "course time has the same opacity as the title");

static Viewbox ReadStatusViewbox(XElement markup, Control child) => new()
{
    Child = child,
    StretchDirection = Enum.Parse<StretchDirection>((string)markup.Attribute("StretchDirection")!),
    HorizontalAlignment = Enum.Parse<HorizontalAlignment>((string)markup.Attribute("HorizontalAlignment")!),
    VerticalAlignment = Enum.Parse<VerticalAlignment>((string)markup.Attribute("VerticalAlignment")!),
};

foreach (var width in new[] { 100.0, 280.0 })
{
    var meterText = new TextBlock { Text = "保护中 03:09", FontFamily = new FontFamily("sans-serif"), FontSize = 20, FontWeight = FontWeight.Bold };
    var meter = new Grid
    {
        Width = width,
        Height = double.Parse((string)meterMarkup.Attribute("Height")!, CultureInfo.InvariantCulture),
        ColumnDefinitions = new ColumnDefinitions((string)meterMarkup.Attribute("ColumnDefinitions")!),
    };
    var status = ReadStatusViewbox(statusMarkup, meterText);
    Grid.SetColumn(status, 1);
    meter.Children.Add(status);
    foreach (var (column, label) in new[] { (0, "教室音量"), (2, "吵闹") })
    {
        var side = ReadStatusViewbox(meterMarkup.Elements().First(e =>
            ((string?)e.Attribute("Grid.Column") ?? "0") == column.ToString(CultureInfo.InvariantCulture)),
            new TextBlock { Text = label, FontSize = 20 });
        Grid.SetColumn(side, column);
        meter.Children.Add(side);
    }

    var footerText = new TextBlock { Text = "保护中 03:09", FontFamily = new FontFamily("sans-serif"), FontSize = 32, FontWeight = FontWeight.Bold };
    var wholeRow = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 12,
        Children = { new TextBlock { Text = "较长的课程名称", FontSize = 32 }, footerText },
    };
    var footer = new Border { Width = width, Child = ReadStatusViewbox(counterMarkup, wholeRow), ClipToBounds = true };
    var panel = new StackPanel { Children = { meter, footer } };
    var window = new Window { Width = 400, Height = 180, Content = panel };
    window.Show();
    window.UpdateLayout();

    foreach (var (label, text, container) in new (string, TextBlock, Control)[]
        { ("meter", meterText, meter), ("footer", footerText, footer) })
    {
        var left = text.TranslatePoint(default, container)!.Value.X;
        var right = text.TranslatePoint(new Point(text.Bounds.Width, 0), container)!.Value.X;
        Check(text.Bounds.Width >= text.DesiredSize.Width - 0.5 && right > left &&
              left >= -0.5 && right <= container.Bounds.Width + 0.5,
            $"{label} at {width} DIP: the full protection countdown fits inside its visible container");
    }
    window.Close();
    Dispatcher.UIThread.RunJobs();
}

var bodyPlacement = new Grid();
var footerPlacement = new Grid();
var coursePlacement = new Viewbox();
footerPlacement.Children.Add(coursePlacement);
foreach (var (between, compact) in new[] { (true,false), (true,true), (false,true), (false,false), (true,false) })
{
    CourseInfoPlacement.Apply(coursePlacement, bodyPlacement, footerPlacement, between, compact);
    Check(ReferenceEquals(coursePlacement.Parent, between ? bodyPlacement : footerPlacement), "course moves to the selected area");
    Check(Grid.GetColumn(coursePlacement) == (between || compact ? 0 : 1), "course restores its column");
    Check(Grid.GetColumnSpan(coursePlacement) == (!between && compact ? 3 : 1), "course restores the compact footer span");
    Check(coursePlacement.VerticalAlignment == (between ? VerticalAlignment.Top : VerticalAlignment.Center),
        "course uses the positioning rules for the destination");
}
Check(CourseInfoPlacement.BetweenTop(240, 500, 40, 24, 0) == 264, "slider starts below the clock glass");
Check(CourseInfoPlacement.BetweenTop(240, 500, 40, 24, 50) == 350, "default course position is centered in the gap");
Check(CourseInfoPlacement.BetweenTop(240, 500, 40, 24, 100) == 436, "slider stays above the meter glass");
Check(CourseInfoPlacement.BetweenTop(240, 480, 40, 24, 100) == 416, "hidden meter uses the bottom of the body");
var trayProvider = new TrayTests.Provider();
var traySettings = new EveningSelfStudyClock.Models.PluginSettings();
var trayVm = new EveningSelfStudyClock.ViewModels.FullScreenClockViewModel();
var trayService = new EveningSelfStudyClock.Services.TrayMenuService(trayVm,trayProvider,traySettings);
var trayStart = trayService.StartAsync(default);
Dispatcher.UIThread.RunJobs();
trayStart.GetAwaiter().GetResult();
trayService.EnsureRegistered(); // tray not ready at startup
var firstMenu = new NativeMenu();
trayProvider.MainTaskBarIcon = new TrayIcon { Menu = firstMenu };
trayService.EnsureRegistered();
Check(firstMenu.Items.Count == 1, "late tray readiness registers item");
trayService.EnsureRegistered();
Check(firstMenu.Items.Count == 1, "tray registration is idempotent");
var secondMenu = new NativeMenu();
trayProvider.MainTaskBarIcon.Menu = secondMenu;
Check(firstMenu.Items.Count == 0 && secondMenu.Items.Count == 1, "tray menu replacement detaches old and registers new");
traySettings.ShowTrayMenuEntry = false;
Dispatcher.UIThread.RunJobs();
Check(secondMenu.Items.Count == 0, "tray switch removes item immediately");
traySettings.ShowTrayMenuEntry = true;
Dispatcher.UIThread.RunJobs();
Check(secondMenu.Items.Count == 1, "tray switch restores item");
((NativeMenuItem)secondMenu.Items[0]).Command!.Execute(null);
Check(trayVm.Shows == 1, "tray command invokes clock");
var trayStop = trayService.StopAsync(default);
Dispatcher.UIThread.RunJobs();
trayStop.GetAwaiter().GetResult();
trayService.EnsureRegistered();
Check(secondMenu.Items.Count == 0, "stopped tray service cannot register again");
trayProvider.MainTaskBarIcon.Dispose();
Console.WriteLine($"PASS: {checks} Avalonia input, layout and tray checks");
