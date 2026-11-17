using System.ComponentModel;
using System.Runtime.CompilerServices;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;

int checks = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); checks++; }
var settings = new PluginSettings();
var service = new SnitchHotkeyService(settings, new EmptyProvider());
service.StartAsync(default).GetAwaiter().GetResult();
service.StartAsync(default).GetAwaiter().GetResult();
Check(settings.Subscribers == 1, "repeated start must have one settings subscription");
var queued = settings.CapturePendingEvent();
service.StopAsync(default).GetAwaiter().GetResult();
Check(settings.Subscribers == 0, "stop removes settings subscription");
settings.EnableSnitch = true;
// Simulate an event captured before Stop but delivered afterward. This must return
// before any Win32 call; therefore the same check is safe on Linux and Windows.
queued!(settings, new PropertyChangedEventArgs(nameof(PluginSettings.EnableSnitch)));
Check(settings.Subscribers == 0, "late event cannot resurrect a stopped service");
settings.EnableSnitch = false;
service.StartAsync(default).GetAwaiter().GetResult();
Check(settings.Subscribers == 1, "restart subscribes once");
service.Dispose();
service.Dispose();
Check(settings.Subscribers == 0, "dispose is idempotent and removes event roots");
var retainedSettings = new PluginSettings();
var weak = CreateDisposed(retainedSettings);
for (int i = 0; i < 3 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
Check(!weak.IsAlive, "long-lived settings must not retain disposed service");
Console.WriteLine($"PASS: {checks} hotkey lifecycle checks (native APIs intentionally not invoked)");

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference CreateDisposed(PluginSettings settings)
{
    var instance = new SnitchHotkeyService(settings, new EmptyProvider());
    instance.StartAsync(default).GetAwaiter().GetResult();
    instance.Dispose();
    return new WeakReference(instance);
}
sealed class EmptyProvider : IServiceProvider { public object? GetService(Type type) => null; }
