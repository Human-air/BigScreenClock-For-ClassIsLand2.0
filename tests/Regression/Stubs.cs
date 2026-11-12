using System.Collections.Concurrent;
namespace Avalonia { public readonly record struct PixelSize(int Width, int Height); }
namespace Avalonia.Threading {
 public class Dispatcher {
 public static Dispatcher UIThread { get; } = new();
 public ConcurrentQueue<Action> Queue { get; } = new();
 public void Post(Action action) => Queue.Enqueue(action);
 public void Drain() { while (Queue.TryDequeue(out var action)) action(); }
 }
}
namespace EveningSelfStudyClock { public static class Plugin { public static string ConfigFolder => ""; } }
namespace EveningSelfStudyClock.Models {
 public class BackdropImageSet : IDisposable {
 public int Disposed; public string Path = "image";
 public bool Matches(string path, Avalonia.PixelSize size, int stretch, double radius, bool blur) => Path == path;
 public void Dispose() => Disposed++;
 }
 public class PluginSettings {
 public bool EnableNoiseDebugLog, LogKeepByDuration;
 public int LogKeepMinKb, LogKeepMinutes, LogRetentionDays, SkipFirstMinutes;
 public double SamplingIntervalSeconds = .2, DecibelQuietThreshold, DecibelGoodThreshold, DecibelNormalThreshold, DecibelNoisyThreshold, NoisySustainSeconds, FallWindowSeconds;
 }
}
namespace NAudio.Wave {
 public class WaveFormat(int sampleRate, int bits, int channels) { public int SampleRate => sampleRate; public int BitsPerSample => bits; public int Channels => channels; }
 public class WaveInEventArgs : EventArgs { public int BytesRecorded; public byte[] Buffer = []; }
 public class StoppedEventArgs : EventArgs { public Exception? Exception; }
 public class WaveInEvent : IDisposable {
 public static int Created, Disposed; public static bool ThrowOnStop;
 public static int DeviceCount => 1;
 public int DeviceNumber, BufferMilliseconds; public WaveFormat WaveFormat = new(44100,16,1);
 public event EventHandler<WaveInEventArgs>? DataAvailable;
 public event EventHandler<StoppedEventArgs>? RecordingStopped;
 public WaveInEvent() => Created++;
 public void StartRecording() {}
 public void StopRecording() { if(ThrowOnStop) throw new Exception("device disconnected"); }
 public void Dispose() => Disposed++;
 public void RaiseData() => DataAvailable?.Invoke(this,new());
 public void RaiseStopped() => RecordingStopped?.Invoke(this,new());
 }
}
