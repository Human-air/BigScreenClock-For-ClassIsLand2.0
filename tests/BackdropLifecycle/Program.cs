using System.Collections;
using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Views;

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia().SetupWithoutStarting();
var folder = Path.Combine(Path.GetTempPath(), "bigscreen-backdrop-lifecycle-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(folder);
var imagePath = Path.Combine(folder, "4k.png");
using (var bitmap = new WriteableBitmap(new PixelSize(3840, 2160), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul))
{
    unsafe
    {
        using var pixels = bitmap.Lock();
        for (int y = 0; y < 2160; y++)
        {
            var row = (uint*)((byte*)pixels.Address + (long)y * pixels.RowBytes);
            for (int x = 0; x < 3840; x++) row[x] = 0xff000000u | ((uint)(x % 256) << 16) | ((uint)(y % 256) << 8) | (uint)((x + y) % 256);
        }
    }
    bitmap.Save(imagePath);
}
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
object? Read(object target, string name) => target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target);
void Call(object target, string method) => target.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.Public)!.Invoke(target, null);
void Pump(Window window)
{
    using var pumpCancellation = new CancellationTokenSource(20);
    try { Dispatcher.UIThread.MainLoop(pumpCancellation.Token); }
    catch (OperationCanceledException) { }
    Dispatcher.UIThread.RunJobs();
    window.UpdateLayout();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    Dispatcher.UIThread.RunJobs();
}
void Wait(Window window, Func<bool> condition, string description)
{
    var stopwatch = Stopwatch.StartNew();
    while (!condition())
    {
        Pump(window);
        if (stopwatch.Elapsed > TimeSpan.FromSeconds(25))
        {
            var debugLayer = (window.Content as Panel)?.Children.OfType<BackdropLayer>().FirstOrDefault();
            if (debugLayer is not null)
            {
                Console.WriteLine("DEBUG " + string.Join(" ", new[] { "_busy", "_glassBusy", "_glassDirty", "_needsRebuild", "_suspended", "_attached", "_image" }
                    .Select(field => field + "=" + Read(debugLayer, field))));
                Console.WriteLine($"DEBUG bounds={debugLayer.Bounds}, visible={debugLayer.IsEffectivelyVisible}, timer={(Read(debugLayer, "_glassTimer") as DispatcherTimer)?.IsEnabled}");
            }
            throw new TimeoutException(description);
        }
        Thread.Sleep(10);
    }
    Pump(window);
}
(Window Window, Grid Root, BackdropLayer Layer, Border Target) Create()
{
    var layer = new BackdropLayer { Width = 3840, Height = 2160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
    var target = new Border { Width = 600, Height = 160, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top, Margin = new Thickness(80, 100, 0, 0), Background = Brushes.Transparent };
    layer.GlassTargets = [new GlassTarget { Visual = target }];
    var root = new Grid(); root.Children.Add(layer); root.Children.Add(target);
    layer.Apply(imagePath, 0, 0, (int)GlassMode.Liquid, 35, 24, 50, 45);
    var window = new Window { Width = 3840, Height = 2160, Content = root };
    window.Show();
    Pump(window);
    return (window, root, layer, target);
}
bool Idle(BackdropLayer layer) => !(bool)Read(layer, "_busy")! && !(bool)Read(layer, "_glassBusy")!;
BackdropImageSet? Image(BackdropLayer layer) => (BackdropImageSet?)Read(layer, "_image");
int Panels(BackdropLayer layer) => ((IList)Read(layer, "_panels")!).Count;
bool GlassReady(BackdropLayer layer)
{
    var panels = (IList)Read(layer, "_panels")!;
    return panels.Count > 0 && !(bool)Read(layer, "_glassDirty")!
        && (Read(layer, "_glassTimer") is not DispatcherTimer timer || !timer.IsEnabled)
        && panels.Cast<object>().All(panel =>
            panel.GetType().GetField("Bitmap", BindingFlags.Instance | BindingFlags.Public)!.GetValue(panel) is not null
            && !(bool)panel.GetType().GetField("NeedsBake", BindingFlags.Instance | BindingFlags.Public)!.GetValue(panel)!);
}
[MethodImpl(MethodImplOptions.NoInlining)]
void CheckPresent(BackdropLayer layer, string message) => Check(Image(layer) is not null, message);
[MethodImpl(MethodImplOptions.NoInlining)]
void CheckFullSize(BackdropLayer layer) => Check(Image(layer)?.Pixel == new PixelSize(3840,2160), "outdated half-size job cannot remain adopted");
var fixture = Create();
Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer) is not null && GlassReady(fixture.Layer), "initial 4K background and glass");
[MethodImpl(MethodImplOptions.NoInlining)]
void CheckInitialImage(BackdropLayer layer)
{
    Check(Image(layer)!.Sharp!.PixelSize == new PixelSize(3840, 2160), "clear background keeps 4K detail");
    Check(Image(layer)!.SharpPixels!.Length <= BackdropImageSet.OpticalPixelLimit, "optical snapshot is bounded");
}
CheckInitialImage(fixture.Layer);

// Large overlapping glass regions exercise the aggregate raster budget, not just
// a small clock tile. The clear background itself must remain full resolution.
fixture.Target.Width = 2000; fixture.Target.Height = 1200;
var secondTarget = new Border { Width = 2400, Height = 1000,
    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
    Margin = new Thickness(120, 300, 0, 0), Background = Brushes.Transparent };
fixture.Root.Children.Add(secondTarget);
fixture.Layer.GlassTargets = [new GlassTarget { Visual = fixture.Target }, new GlassTarget { Visual = secondTarget }];
fixture.Layer.InvalidateGlass();
Wait(fixture.Window, () => Idle(fixture.Layer) && GlassReady(fixture.Layer), "large liquid tiles settle");
long GlassPixels(BackdropLayer layer) => ((IList)Read(layer, "_panels")!).Cast<object>().Sum(panel =>
{
    var bitmap = (WriteableBitmap?)panel.GetType().GetField("Bitmap", BindingFlags.Instance | BindingFlags.Public)!.GetValue(panel);
    return bitmap is null ? 0L : (long)bitmap.PixelSize.Width * bitmap.PixelSize.Height;
});
Check(GlassPixels(fixture.Layer) > 0 && GlassPixels(fixture.Layer) <= BackdropImageSet.OpticalPixelLimit * 1.02,
    "all liquid tiles share a bounded optical raster budget");
var stableTotals = fixture.Layer.GlassBakeTotals;
[MethodImpl(MethodImplOptions.NoInlining)]
void CheckUnchangedBackdrop()
{
    var settledImage = Image(fixture.Layer);
    for (var pass = 0; pass < 6; pass++)
    {
        fixture.Layer.Resume();
        fixture.Layer.InvalidateGlass();
        Pump(fixture.Window);
    }
    Wait(fixture.Window, () => Idle(fixture.Layer) && GlassReady(fixture.Layer), "unchanged glass stays cached");
    Check(ReferenceEquals(Image(fixture.Layer), settledImage), "repeated visible resume with identical inputs reuses the backdrop");
    Check(fixture.Layer.GlassBakeTotals == stableTotals, "repeated layout signals with identical inputs perform zero glass bakes");
}
CheckUnchangedBackdrop();
secondTarget.Width -= 12;
Pump(fixture.Window);
Wait(fixture.Window, () => Idle(fixture.Layer) && GlassReady(fixture.Layer), "one panel changes size under the raster cap");
Check(fixture.Layer.GlassBakeTotals.Panels == stableTotals.Panels + 1,
    "changing one large panel under the cap reuses the unchanged panel's baked image");
var beforeGrowth = fixture.Layer.GlassBakeTotals;
secondTarget.Width += 700;
Pump(fixture.Window);
Wait(fixture.Window, () => Idle(fixture.Layer) && GlassReady(fixture.Layer), "large panel grows past the shared raster quota");
Check(fixture.Layer.GlassBakeTotals.Panels == beforeGrowth.Panels + 1,
    "growing a panel past the budget only reduces and refreshes that panel");
Check(GlassPixels(fixture.Layer) <= BackdropImageSet.OpticalPixelLimit,
    "local resizing remains within total raster budget");
[MethodImpl(MethodImplOptions.NoInlining)]
WeakReference[] CaptureRetiredResources(BackdropLayer layer)
{
    var image = Image(layer)!;
    return [new WeakReference(image), new WeakReference(image.SharpPixels!), new WeakReference(image.BlurPixels!)];
}
[MethodImpl(MethodImplOptions.NoInlining)]
void CheckImageCap(BackdropLayer layer) => Check(Image(layer)!.SharpPixels!.Length <= BackdropImageSet.OpticalPixelLimit, "settled optical snapshot stays bounded");
var retiredResources = new List<WeakReference>();
var samples = new List<string>();
for (int round = 0; round < 6; round++)
{
    foreach (var width in new[] { 1920, 3840 })
    {
        retiredResources.AddRange(CaptureRetiredResources(fixture.Layer));
        fixture.Layer.Width = width; Pump(fixture.Window);
        Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer)?.Pixel == new PixelSize(width, 2160)
            && GlassReady(fixture.Layer), "settled resize and glass");
        CheckImageCap(fixture.Layer);
        Check(GlassPixels(fixture.Layer) <= BackdropImageSet.OpticalPixelLimit * 1.02, "settled glass raster stays bounded");
    }
    using var process = Process.GetCurrentProcess();
    samples.Add($"round={round + 1} private MiB={process.PrivateMemorySize64 / 1048576d:F1}, managed MiB={GC.GetTotalMemory(false) / 1048576d:F1}");
}
Pump(fixture.Window);
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
Check(retiredResources.All(reference => !reference.IsAlive), "retired image sets and both optical snapshots are collectible across settled cycles");
foreach (var sample in samples) Console.WriteLine("REPORT " + sample);
// Turning liquid glass off must dispose its native tiles and reuse canvases.
fixture.Layer.Apply(imagePath, 0, 0, (int)GlassMode.Off, 35, 24, 50, 45);
Pump(fixture.Window);
Wait(fixture.Window, () => Idle(fixture.Layer), "liquid off work drains");
Check(GlassPixels(fixture.Layer) == 0, "turning liquid off releases panel bitmaps");
Check(((IList)Read(fixture.Layer, "_panels")!).Cast<object>().All(panel =>
    panel.GetType().GetField("Canvas", BindingFlags.Instance | BindingFlags.Public)!.GetValue(panel) is null),
    "turning liquid off releases panel canvases");
fixture.Layer.Apply(imagePath, 0, 0, (int)GlassMode.Liquid, 35, 24, 50, 45);
fixture.Layer.InvalidateGlass();
Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer) is not null && GlassReady(fixture.Layer), "liquid restored");

// Resize repeatedly before previous jobs can finish. Only the final requested size may win.
for (int round = 0; round < 20; round++)
{
    fixture.Layer.Width = 1920; Pump(fixture.Window);
    fixture.Layer.Width = 3840; Pump(fixture.Window);
}
Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer)?.Pixel == new PixelSize(3840, 2160), "latest full size wins");
CheckFullSize(fixture.Layer);

// The host keeps its Window alive after Hide or minimize; these owner events must
// still release resources without relying on detachment or caller-specific hooks.
fixture.Window.Hide();
Wait(fixture.Window, () => Idle(fixture.Layer), "hidden owner work drains");
Check(Image(fixture.Layer) is null && Panels(fixture.Layer) == 0, "Window.Hide releases resources while layer remains attached");
fixture.Window.Show();
Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer)?.Pixel == new PixelSize(3840, 2160), "owner show restores current background");
CheckPresent(fixture.Layer, "Window.Show restarts visible background work");
fixture.Window.WindowState = WindowState.Minimized;
Wait(fixture.Window, () => Idle(fixture.Layer), "minimized owner work drains");
Check(Image(fixture.Layer) is null && Panels(fixture.Layer) == 0, "minimized owner releases background without detaching");
fixture.Window.WindowState = WindowState.Normal;
Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer)?.Pixel == new PixelSize(3840, 2160), "owner restore rebuilds background");
CheckPresent(fixture.Layer, "owner restore restarts background work");

[MethodImpl(MethodImplOptions.NoInlining)]
WeakReference CaptureImageReference(BackdropLayer layer) => new(Image(layer));
var oldImage = CaptureImageReference(fixture.Layer);
Call(fixture.Layer, "Suspend");
Wait(fixture.Window, () => Idle(fixture.Layer), "suspended jobs drain");
Check(Image(fixture.Layer) is null, "suspend releases background image");
Check(Panels(fixture.Layer) == 0, "suspend releases all panel bitmaps and canvases");
var timer = Read(fixture.Layer, "_glassTimer") as DispatcherTimer;
Check(timer is null || !timer.IsEnabled, "suspend stops glass timer");
for (int i = 0; i < 4; i++) { fixture.Layer.Width = i % 2 == 0 ? 1920 : 3840; Pump(fixture.Window); }
Check(Image(fixture.Layer) is null && Idle(fixture.Layer), "hidden layout changes cannot restart background work");
GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
Check(!oldImage.IsAlive, "suspended image object is collectible after callbacks drain");
Call(fixture.Layer, "Resume");
Wait(fixture.Window, () => Idle(fixture.Layer) && Image(fixture.Layer)?.Pixel == new PixelSize(3840, 2160), "resume rebuilds current size");
CheckPresent(fixture.Layer, "resume restores background");

// Removing a layer while a job is pending must not publish its result after detachment.
fixture.Layer.Width = 1920; Pump(fixture.Window);
fixture.Root.Children.Remove(fixture.Layer);
Wait(fixture.Window, () => Idle(fixture.Layer), "detached work drains");
Check(Image(fixture.Layer) is null && Panels(fixture.Layer) == 0, "detach releases all owned bitmap resources");
fixture.Window.Close();
Dispatcher.UIThread.RunJobs();

[MethodImpl(MethodImplOptions.NoInlining)]
WeakReference MakeDetachedLayer(string path)
{
    var transient = new BackdropLayer();
    var root = new Grid(); root.Children.Add(transient);
    var window = new Window { Width = 640, Height = 360, Content = root };
    window.Show();
    transient.Apply(path, 0, 0, (int)GlassMode.Liquid, 35, 24, 50, 45);
    window.UpdateLayout();
    root.Children.Remove(transient);
    window.Close();
    // Capturing only the WeakReference lets the test verify DispatcherTimer/task ownership.
    return new WeakReference(transient);
}
var detached = MakeDetachedLayer(imagePath);
var finish = Stopwatch.StartNew();
while (detached.IsAlive && finish.Elapsed < TimeSpan.FromSeconds(25))
{
    Dispatcher.UIThread.RunJobs();
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Thread.Sleep(30);
}
Check(!detached.IsAlive, "detached layer is collectible after pending work completes");
BackgroundDiagnostics.DestinationFolder = folder;
var adaptive = Create();
adaptive.Layer.Apply(imagePath, 0, 0, (int)GlassMode.Liquid, 35, 24, 50, 45, autoGlassFallback: true);
Wait(adaptive.Window, () => Idle(adaptive.Layer) && GlassReady(adaptive.Layer), "adaptive liquid ready");
var originalSharp = Image(adaptive.Layer)!.Sharp;
var fullQualityPixels = GlassPixels(adaptive.Layer);
var notices = new List<(GlassMode From, GlassMode To, GlassFallbackReason Reason)>();
adaptive.Layer.GlassFallbackApplied += (from, to, reason) => notices.Add((from, to, reason));
void Stall(int seconds) => adaptive.Layer.ObserveUiStall(TimeSpan.FromSeconds(seconds), 1100);
Stall(1); Stall(3);
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Liquid && !adaptive.Layer.ReducedGlassResolution,
    "two stall signals do not reduce quality");
Stall(5);
Wait(adaptive.Window, () => Idle(adaptive.Layer) && GlassReady(adaptive.Layer), "low resolution liquid ready");
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Liquid && adaptive.Layer.ReducedGlassResolution &&
    GlassPixels(adaptive.Layer) < fullQualityPixels * .5, "first response lowers render resolution while retaining liquid");
Check(ReferenceEquals(Image(adaptive.Layer)!.Sharp, originalSharp), "lowering refraction resolution does not decode the wallpaper again");
Stall(8); Stall(10); Stall(12);
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Frosted, "new pressure after resolution reduction selects frosted");
Check(ReferenceEquals(Image(adaptive.Layer)!.Sharp, originalSharp) && adaptive.Layer.HasBlurredBackdrop,
    "frosted preserves the sharp wallpaper and the blur bitmap");
Check(Image(adaptive.Layer)!.SharpPixels is null && Image(adaptive.Layer)!.BlurPixels is null &&
    GlassPixels(adaptive.Layer) == 0, "frosted releases refraction snapshots and panel bitmaps");
Stall(14); Stall(16); Stall(41);
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Frosted, "old stall signals do not contribute to a new downgrade");
Stall(43); Stall(45);
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Translucent, "persistent pressure after frosted selects translucent");
Check(notices.SequenceEqual(new[]
{
    (GlassMode.Liquid, GlassMode.Liquid, GlassFallbackReason.UiStall),
    (GlassMode.Liquid, GlassMode.Frosted, GlassFallbackReason.UiStall),
    (GlassMode.Frosted, GlassMode.Translucent, GlassFallbackReason.UiStall),
}), "all notices carry the exact original mode, new mode and reason");
Check(ReferenceEquals(Image(adaptive.Layer)!.Sharp, originalSharp) && Image(adaptive.Layer)!.OpticalBytes == 0,
    "translucent preserves the same sharp wallpaper and releases all optics");
adaptive.Layer.Width = 1920; Pump(adaptive.Window);
Wait(adaptive.Window, () => Idle(adaptive.Layer) && Image(adaptive.Layer)?.Pixel.Width == 1920,
    "translucent continues loading the wallpaper on resize");
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Translucent && Image(adaptive.Layer)!.OpticalBytes == 0,
    "resize does not silently restore liquid or allocate optics");
adaptive.Layer.Apply(imagePath, 0, 0, (int)GlassMode.Liquid, 35, 24, 50, 45, autoGlassFallback: false);
Wait(adaptive.Window, () => Idle(adaptive.Layer) && GlassReady(adaptive.Layer), "switch off fallback restores liquid and snapshots");
Stall(48); Stall(50); Stall(52);
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Liquid && Image(adaptive.Layer)!.SharpPixels is not null,
    "disabling the option keeps the configured mode and restores samples");
adaptive.Layer.Apply(imagePath, 0, 0, (int)GlassMode.Frosted, 35, 24, 50, 45, autoGlassFallback: true);
Stall(54); Stall(56); Stall(58);
Check(adaptive.Layer.EffectiveGlassMode == GlassMode.Translucent, "automatic fallback also works when frosted was selected directly");
adaptive.Window.Close();

BackgroundDiagnostics.Write("image-error", "pixels=3840x2160", new IOException("private-wallpaper-path"));
using (var zip = new ZipArchive(new MemoryStream(BackgroundDiagnostics.CreateArchive("pluginVersion=1.2.2.0.4"))))
{
    Check(zip.Entries.Count == 2 && zip.GetEntry("summary.txt") is not null, "export contains only the summary and known diagnostic logs");
    using var log = new StreamReader(zip.GetEntry("ClockDiagnostics.log")!.Open());
    var content = log.ReadToEnd();
    Check(content.Contains("from=Liquid to=Frosted") && content.Contains("from=Frosted to=Translucent") &&
        content.Contains("privateMiB="), "export includes both fallback transitions and process memory");
    Check(content.Contains("error=IOException") && !content.Contains("private-wallpaper-path"),
        "export records error type without the private error text");
}
BackgroundDiagnostics.DestinationFolder = null;
BackdropPreloadCache.Clear();
using (var process = Process.GetCurrentProcess())
    Console.WriteLine($"REPORT private MiB={process.PrivateMemorySize64 / 1048576d:F1}, managed MiB={GC.GetTotalMemory(false) / 1048576d:F1}");
Console.WriteLine($"PASS: {checks} real backdrop lifecycle and resize assertions");
try { Directory.Delete(folder, true); } catch (IOException) { }
