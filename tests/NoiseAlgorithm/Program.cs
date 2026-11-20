using System.Reflection;
using EveningSelfStudyClock.NoiseDetection;

const double Quiet = .001, Normal = .05, Noisy = .12;
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

// Deterministic level inputs test attribution separately from the existing EMA/hysteresis.
foreach (var dt in new[] { .1, .13, .2, .5, 1.0 })
{
    void Scene(string name, int normal, int noisy, params (double Rms, double Seconds)[] parts)
    {
        var run = new Run(dt, smooth: false);
        foreach (var part in parts) run.Feed(part.Rms, part.Seconds);
        run.Feed(Quiet, 7);
        Check(run.Normal == normal && run.Noisy == noisy,
            $"{name} dt={dt}: expected {normal}/{noisy}, got {run.Normal}/{run.Noisy}");
    }
    Scene("quiet and good do not open segments", 0, 0, (Quiet, 60), (.02, 60));
    Scene("ordinary conversation", 1, 0, (Normal, 120));
    Scene("short segment below floor", 0, 0, (Noisy, .2));
    Scene("short loud event stays normal", 1, 0, (Noisy, 2), (Normal, 60));
    Scene("continuous just below threshold", 1, 0, (Noisy, 4.9), (Normal, 40));
    Scene("continuous exact threshold", 0, 1, (Noisy, 5), (Normal, 300));
    Scene("late noisy after long normal can upgrade", 0, 1, (Normal, 300), (Noisy, 8), (Normal, 300));
    Scene("intermittent recent noisy", 0, 1, (Noisy, 3), (Normal, 2), (Noisy, 3), (Normal, 300));
    Scene("exact six of ten seconds", 0, 1,
        (Noisy, 2), (Normal, 2), (Noisy, 2), (Normal, 2), (Noisy, 2), (Normal, 60));
    Scene("expired evidence cannot accumulate", 1, 0,
        (Noisy, 3), (Normal, 7.1), (Noisy, 3), (Normal, 60));
    Scene("fractional oldest block is clipped", 1, 0,
        (Noisy, 3), (Normal, 4.1), (Noisy, 3), (Normal, 60));
    Scene("short quiet gap merges", 0, 1, (Noisy, 8), (Quiet, 3), (Noisy, 8), (Normal, 40));
    Scene("long quiet gap separates without cooldown", 0, 2, (Noisy, 8), (Quiet, 8), (Noisy, 8));
    Scene("lock cannot leak into next segment", 1, 1,
        (Noisy, 8), (Quiet, 8), (Normal, 60));
    Scene("long noisy remains a single event", 0, 1, (Noisy, 600), (Normal, 600));

    var live = new Run(dt, smooth: false);
    live.Feed(Normal, 120);
    Check(live.Detector.SegmentLevel == NoiseLevel.Normal && live.Total == 0, "floor does not settle early");
    live.Feed(Noisy, 8);
    Check(live.Detector.SegmentLevel == NoiseLevel.Noisy && live.Total == 0, "lock is visible before settlement");
    live.Feed(Normal, 300);
    Check(live.Detector.Level == NoiseLevel.Normal && live.Detector.SegmentLevel == NoiseLevel.Noisy,
        "live meter stays independent of locked attribution");
    live.Feed(Quiet, 7);
    Check(live.Total == 1 && live.Noisy == 1 && !live.Detector.IsSegmentActive, "settles exactly once at segment end");
    live.Feed(Quiet, 60);
    Check(live.Total == 1, "remaining quiet cannot repeat event");
    live.Feed(Noisy, 8);
    live.Detector.Reset();
    live.Feed(Normal, 5);
    live.Feed(Quiet, 7);
    Check(live.Normal == 1 && live.Noisy == 1, "reset drops unfinished evidence and lock");
}

// Synthetic microphone RMS uses production EMA and Schmitt hysteresis.
foreach (var dt in new[] { .1, .2, .5, 1.0 })
{
    foreach (var late in new[] { false, true })
    {
        var run = new Run(dt);
        run.Feed(Quiet, 2);
        if (late) run.Feed(Normal, 120);
        run.Feed(Noisy, 8);
        run.Feed(Normal, 300);
        run.Feed(Quiet, 9);
        Check(run.Normal == 0 && run.Noisy == 1, $"production smoothing prevents dilution, late={late}, dt={dt}");
    }
    var clap = new Run(dt);
    clap.Feed(Quiet, 2);
    clap.Feed(.65, .2);
    clap.Feed(Quiet, 9);
    Check(clap.Noisy == 0, $"single clap does not lock noisy, dt={dt}");
}

var stress = new Run(.1, smooth: false);
var queue = (System.Collections.ICollection)typeof(NoiseEventDetector)
    .GetField("_recentNoisy", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(stress.Detector)!;
for (int i = 0; i < 3600; i++)
{
    stress.Feed(Noisy, .2);
    stress.Feed(Normal, .8);
    if (i % 600 == 0) Check(queue.Count <= 101, "one-hour low-density evidence stays in ten-second window");
}
Check(stress.Total == 0 && stress.Detector.SegmentLevel == NoiseLevel.Normal, "sparse bursts never build lifetime evidence");
stress.Feed(Noisy, 5);
Check(queue.Count == 0 && stress.Detector.SegmentLevel == NoiseLevel.Noisy, "lock releases evidence buffer");
stress.Feed(Normal, 3600);
Check(queue.Count == 0 && stress.Total == 0, "long locked segment stores no further evidence or events");
stress.Feed(Quiet, 7);
Check(stress.Noisy == 1, "two-hour segment settles once");

var invalid = new Run(.1, smooth: false);
invalid.Feed(Noisy, 4.9);
foreach (var input in new[] { (double.NaN, .1), (double.PositiveInfinity, .1), (-1d, .1), (Noisy, 0d), (Noisy, double.NaN) })
{
    var r = invalid.Detector.Process(input.Item1, input.Item2);
    Check(!r.EventFired && invalid.Detector.SegmentLevel == NoiseLevel.Normal && double.IsFinite(r.SmoothedRms),
        "invalid samples neither poison smoothing nor add noisy duration");
}
invalid.Feed(Noisy, .1);
Check(invalid.Detector.SegmentLevel == NoiseLevel.Noisy, "valid samples continue after invalid input");

var tunedContinuous = new Run(.1, smooth: false);
tunedContinuous.Detector.NoisyContinuousSeconds = 2.5;
tunedContinuous.Feed(Noisy, 2.4);
Check(tunedContinuous.Detector.SegmentLevel == NoiseLevel.Normal, "configured continuous threshold waits below its boundary");
tunedContinuous.Feed(Noisy, .1);
Check(tunedContinuous.Detector.SegmentLevel == NoiseLevel.Noisy, "configured continuous threshold upgrades without waiting for five seconds");
tunedContinuous.Detector.NoisyContinuousSeconds = 10;
tunedContinuous.Feed(Normal, 2);
tunedContinuous.Feed(Quiet, 7);
Check(tunedContinuous.Noisy == 1 && tunedContinuous.Normal == 0, "changing settings does not undo a locked noisy segment");

var tunedWindow = new Run(.1, smooth: false);
tunedWindow.Detector.NoisyWindowRequiredSeconds = 3;
tunedWindow.Feed(Noisy, 1.5);
tunedWindow.Feed(Normal, 2);
tunedWindow.Feed(Noisy, 1.5);
tunedWindow.Feed(Quiet, 7);
Check(tunedWindow.Noisy == 1 && tunedWindow.Normal == 0,
    "configured cumulative threshold upgrades interrupted bursts below the continuous threshold");

var normalOnly = new Run(.1, smooth: false);
normalOnly.Detector.NoisyContinuousSeconds = .5;
normalOnly.Detector.NoisyWindowRequiredSeconds = .5;
normalOnly.Feed(Normal, 60);
normalOnly.Feed(Quiet, 7);
Check(normalOnly.Normal == 1 && normalOnly.Noisy == 0, "long normal-level audio never counts toward either noisy threshold");

var settling = new Run(.1, smooth: false);
settling.Feed(Noisy, 2);
settling.Feed(Quiet, 1);
settling.Detector.NoisyWindowRequiredSeconds = 1;
settling.Feed(Quiet, 6);
Check(settling.Normal == 1 && settling.Noisy == 0, "lowering threshold during a quiet tail does not promote past audio");

var counter = new NoiseCounter();
Check(counter.OnEvent(NoiseLevel.Noisy, true) == CountDecision.None, "class-start protection");
counter.SetBreak(true);
Check(counter.OnEvent(NoiseLevel.Noisy, false) == CountDecision.None, "break protection");
counter.SetBreak(false);
Check(counter.OnEvent(NoiseLevel.Normal, false) == CountDecision.CountNormal, "normal settlement");
Check(counter.OnEvent(NoiseLevel.Noisy, false) == CountDecision.CountNoisy, "no per-level cooldown");
Check(counter.OnEvent(NoiseLevel.Noisy, false) == CountDecision.CountNoisy && counter.NoisyCount == 2,
    "distinct close segments are both counted");
counter.StartNewClass();
Check(counter.NormalCount == 0 && counter.NoisyCount == 0, "new class resets counts");
// V2（1.2.0.0 的整段占比）：吵闹会被后面的一般稀释，占比过半才记吵闹
{
    var v2 = new Run(0.1, smooth: false, useV3: false);
    v2.Feed(Noisy, 8);
    v2.Feed(Normal, 40);
    v2.Feed(Quiet, 7);
    Check(v2.Normal == 1 && v2.Noisy == 0, "V2: short noisy diluted by long normal");
}
{
    var v2 = new Run(0.1, smooth: false, useV3: false);
    v2.Feed(Noisy, 30);
    v2.Feed(Normal, 10);
    v2.Feed(Quiet, 7);
    Check(v2.Normal == 0 && v2.Noisy == 1, "V2: majority noisy stays noisy");
}

Console.WriteLine($"PASS: {checks} noise algorithm checks (synthetic inputs, not classroom accuracy)");

sealed class Run(double step, bool smooth = true, bool useV3 = true)
{
    public NoiseEventDetector Detector { get; } = new() { SmoothAlpha = smooth ? .2 : 1, UseV3 = useV3 };
    public int Normal, Noisy;
    public int Total => Normal + Noisy;
    public void Feed(double rms, double seconds)
    {
        for (double remaining = seconds; remaining > 1e-8;)
        {
            var dt = Math.Min(remaining, step);
            remaining -= dt;
            var r = Detector.Process(rms, dt);
            if (!r.EventFired) continue;
            if (r.EventLevel == NoiseLevel.Noisy) Noisy++;
            else if (r.EventLevel == NoiseLevel.Normal) Normal++;
            else throw new Exception("invalid settled level");
        }
    }
}
