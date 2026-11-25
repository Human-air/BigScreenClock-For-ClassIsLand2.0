using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Threading;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;
using EveningSelfStudyClock.Services;
using NAudio.Wave;

int checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); checks++; }
var migrationFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
var testManifest = Path.Combine(AppContext.BaseDirectory, "manifest.yml");
File.WriteAllText(testManifest, "version: 1.2.2.0.6");
try
{
    Check(SettingsMigration.SupportsVersion("1.2.2.0.6") &&
        !SettingsMigration.SupportsVersion("1.2.2.0.5") &&
        SettingsMigration.SupportsVersion("1.2.3.0.1"), "migration version gate");
    using var exported = new MemoryStream();
    await SettingsMigration.ExportAsync(exported, new PluginSettings
    {
        ClockFontSize = 123, BackgroundImageFile = "source-wallpaper", FeishuSecret = "source-secret"
    });
    var payload = System.Text.Encoding.UTF8.GetString(exported.ToArray());
    Check(!payload.Contains("source-secret") && !payload.Contains("source-wallpaper"),
        "migration export excludes machine-local settings");
    var target = new PluginSettings { ClockFontSize = 200, FeishuSecret = "target-secret" };
    exported.Position = 0;
    await SettingsMigration.QueueAsync(exported, migrationFolder, target);
    var targetPath = Path.Combine(migrationFolder, "Settings.json");
    File.WriteAllText(targetPath, JsonSerializer.Serialize(target));
    var imported = SettingsMigration.ApplyPending(targetPath, target);
    Check(imported.ClockFontSize == 123 && imported.FeishuSecret == "target-secret",
        "migration applies portable settings while preserving target credentials");
    Check(Directory.GetFiles(migrationFolder, "*.bak").Length == 1 &&
        ReferenceEquals(SettingsMigration.ApplyPending(targetPath, imported), imported),
        "migration backs up original settings and consumes the pending import");
}
finally
{
    File.Delete(testManifest);
}
var size = new PixelSize(1920,1080);
BackdropPreloadCache.Clear();
Check(BackdropPreloadCache.TryBeginBuild(out var generation), "initial build");
Check(!BackdropPreloadCache.TryBeginBuild(out _), "only one build");
BackdropPreloadCache.Clear();
var stale = new BackdropImageSet();
Check(!BackdropPreloadCache.Publish(stale, generation), "settings change must reject stale image");
Check(stale.Disposed == 0, "rejected image remains owned by caller");
stale.Dispose();
Check(!BackdropPreloadCache.TryBeginBuild(out _), "clear must not allow overlapping builds");
BackdropPreloadCache.EndBuild();
Check(BackdropPreloadCache.TryBeginBuild(out generation), "build slot reusable");
var image = new BackdropImageSet();
Check(BackdropPreloadCache.Publish(image, generation), "current image accepted");
BackdropPreloadCache.EndBuild();
Check(ReferenceEquals(BackdropPreloadCache.TryTake("image",size,0,0,false),image), "take transfers ownership");
BackdropPreloadCache.Clear();
Check(image.Disposed == 0, "clear must not dispose transferred image");
image.Dispose();
Check(BackdropPreloadCache.TryBeginBuild(out generation), "new build");
var cached = new BackdropImageSet();
BackdropPreloadCache.Publish(cached,generation);
BackdropPreloadCache.EndBuild();
BackdropPreloadCache.Clear();
Check(cached.Disposed == 1, "cache clears resources exactly once");

var defaultCameraSettings = new PluginSettings();
var camerasInUse = new[]
{
    new CameraActivity("内置摄像头", "device-a", "未知程序"),
    new CameraActivity("内置摄像头", "device-b", "教室集控"),
};
List<AlertInfo> CameraAlerts(PluginSettings settings) => CameraAlertBuilder.Build(
    settings.MonitoredCameras, camerasInUse, settings.CameraAlertFontSize, settings.CameraListFilled);
Check(CameraAlerts(defaultCameraSettings).Count == 1,
    "fresh settings show occupied cameras before the settings page has populated selection");
defaultCameraSettings.CameraListFilled = true;
Check(CameraAlerts(defaultCameraSettings).Count == 0, "deselecting every device suppresses camera reminders");
defaultCameraSettings.MonitoredCameras.Add(new MonitoredCamera("内置摄像头", "DEVICE-B").ToSettingValue());
Check(CameraAlerts(defaultCameraSettings).Single().Detail?.Contains("教室集控") == true,
    "camera selection uses the interface path and ignores its casing, even if another camera has the same name");
defaultCameraSettings.CameraListFilled = false;
Check(CameraAlerts(defaultCameraSettings).Single().Detail?.Contains("教室集控") == true,
    "a stored selection still limits alerts before selection initialization");
defaultCameraSettings.MonitoredCameras.Clear();
Check(CameraAlertBuilder.Build(defaultCameraSettings.MonitoredCameras, [], selectionInitialized: false).Count == 0,
    "first-run defaults do not create an alert without an operating-system activity report");

object? Field(object obj,string name) => obj.GetType().GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(obj);
// Load the production settings model with the same serializer used by ConfigureFileHelper.
var legacySettings = JsonSerializer.Deserialize<PluginSettings>("""{"NoisySustainSeconds":1.5}""")!;
Check(legacySettings.NoisySustainSeconds == 1.5 && legacySettings.NoisyContinuousSeconds == 5 &&
    legacySettings.NoisyWindowRequiredSeconds == 6, "older settings preserve the separate V3 defaults");
legacySettings.NoisyContinuousSeconds = double.NaN;
legacySettings.NoisyWindowRequiredSeconds = -1;
Check(legacySettings.NoisyContinuousSeconds == 5 && legacySettings.NoisyWindowRequiredSeconds == .5,
    "invalid duration cannot poison detector settings");
legacySettings.NoisyWindowRequiredSeconds = 11;
Check(legacySettings.NoisyWindowRequiredSeconds == 10, "cumulative duration cannot exceed the ten-second window");
legacySettings.NoisyContinuousSeconds = 2.5;
legacySettings.NoisyWindowRequiredSeconds = 4;
var savedSettings = JsonSerializer.Deserialize<PluginSettings>(JsonSerializer.Serialize(legacySettings))!;
Check(savedSettings.NoisyContinuousSeconds == 2.5 && savedSettings.NoisyWindowRequiredSeconds == 4,
    "both promotion settings survive saving and reopening");
using var meter = new DecibelMeterService(savedSettings);

meter.StartMonitoring();
var oldDevice = (WaveInEvent)Field(meter,"_waveIn")!;
var detector = (NoiseEventDetector)Field(meter, "_detector")!;
oldDevice.RaiseData();
Check(detector.NoisyContinuousSeconds == 2.5 && detector.NoisyWindowRequiredSeconds == 4,
    "production audio callback uses the configured promotion durations");
savedSettings.NoisyContinuousSeconds = 3;
savedSettings.NoisyWindowRequiredSeconds = 2;
oldDevice.RaiseData();
Check(detector.NoisyContinuousSeconds == 3 && detector.NoisyWindowRequiredSeconds == 2,
    "configuration changes apply to the next audio block without reopening the clock");
oldDevice.RaiseStopped(); // queues an old device's UI update
meter.StopMonitoring();
meter.StartMonitoring();
Dispatcher.UIThread.Drain();
Check(meter.IsMonitoring, "late stopped event must not mark replacement device stopped");
// Queue a watchdog restart while UI is busy, then stop before processing it.
var timer = (System.Timers.Timer)Field(meter,"_watchdogTimer")!;
var handler = (System.Timers.ElapsedEventHandler)typeof(System.Timers.Timer).GetField("_onIntervalElapsed",BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(timer)!;
typeof(DecibelMeterService).GetField("_lastDataReceived",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(meter,DateTime.MinValue);
handler(timer,null!);
Check(!Dispatcher.UIThread.Queue.IsEmpty, "watchdog restart queued");
meter.StopMonitoring();
var created = WaveInEvent.Created;
Dispatcher.UIThread.Drain();
Check(WaveInEvent.Created == created && !meter.IsMonitoring, "queued watchdog must not reopen stopped microphone");
meter.StartMonitoring();
var disposed = WaveInEvent.Disposed;
WaveInEvent.ThrowOnStop = true;
meter.StopMonitoring();
Check(WaveInEvent.Disposed == disposed + 1, "device must be disposed even when stop throws");
Console.WriteLine($"PASS: {checks} regression checks");
