using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;

AppBuilder.Configure<Application>()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .UseSkia().SetupWithoutStarting();
int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
void Cancelled(Action action, string message)
{
    try { action(); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException(message);
}

// SHA256 values were captured from the previous short[] StackBlur implementation.
// This guards pixel-for-pixel equivalence when changing its scratch buffers to byte[].
foreach (var (w, h, radius, expected) in new[]
{
    (7,5,1,"531F4FD86F53137AEB1EF45C68510B9F8EA2985A6531C226E8DAB28EB07C7B6C"),
    (31,19,4,"CB60BB71562933F75F5D9121838F1F2259E5DDD4E3CAFB2ABC6F6F1AB61136E7"),
    (64,33,24,"64E6FC222231B0D8D60B5B5DBD7DC376CB9268C07CFA19EA5C5B2AC799DE6AB2"),
    (17,9,88,"F6D9F53B6F6B69A115BBB69207A832A03D37B6669CACC872E5E5FA5E86DCD1A6"),
    (8,8,100,"BBB4587B8E3DCA795F2B1E7161134DFD0611CB1E67B4AF5B4DCC373CD32ABA6C")
})
{
    var pixels = new uint[w*h];
    var state = 0x12345678u;
    for (var i=0;i<pixels.Length;i++) { state=unchecked(state*1664525u+1013904223u); pixels[i]=state; }
    StackBlur.Apply(pixels,w,h,radius);
    Check(Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(pixels.AsSpan())))==expected,
        $"blur changed pixels for {w}x{h}, radius {radius}");
}
var directory = Path.Combine(Path.GetTempPath(),"BigScreenClock-memory-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    var path = Path.Combine(directory,"background-4k.png");
    CreateBackground(path);
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    Cancelled(() => BackdropImageSet.Build(path,new PixelSize(3840,2160),0,24,true,cancelled.Token),
        "pre-cancelled image build must throw");
    var scratch = Enumerable.Repeat(0xff123456u,64*64).ToArray();
    Cancelled(() => StackBlur.Apply(scratch,64,64,24,cancelled.Token),"pre-cancelled blur must throw");
    Check(scratch.All(v=>v==0xff123456u),"pre-cancelled blur must not modify pixels");

    // Hold the shared gate to deterministically simulate another layer decoding. Cancellation
    // of a waiting layer must not allocate an image or consume/release somebody else's gate.
    var gate = (SemaphoreSlim)typeof(BackdropImageSet).GetField("BuildGate",BindingFlags.NonPublic|BindingFlags.Static)!.GetValue(null)!;
    gate.Wait();
    try
    {
        using var waiterCancellation = new CancellationTokenSource();
        using var began = new ManualResetEventSlim();
        var waiting = Task.Run(() =>
        {
            began.Set();
            return BackdropImageSet.Build(path,new PixelSize(3840,2160),0,24,true,waiterCancellation.Token);
        });
        Check(began.Wait(TimeSpan.FromSeconds(5)),"waiting build was not scheduled");
        waiterCancellation.Cancel();
        Cancelled(() => waiting.GetAwaiter().GetResult(),"cancelled waiting build must terminate");
        Check(gate.CurrentCount==0,"cancelled waiter must not release another build's gate");
    }
    finally { gate.Release(); }

    using (var missing = BackdropImageSet.Build(Path.Combine(directory,"missing.png"),new PixelSize(3840,2160),0,24,true))
        Check(missing.IsEmpty && missing.BuildError is FileNotFoundException,"missing file reports a bounded empty result");
    var badPath = Path.Combine(directory,"invalid.png");
    File.WriteAllText(badPath,"not an image");
    using (var invalid = BackdropImageSet.Build(badPath,new PixelSize(3840,2160),0,24,true))
        Check(invalid.IsEmpty && invalid.BuildError is not null && invalid.Blur is null && invalid.SharpPixels is null,
            "invalid image must not retain partially allocated resources");

    using (var clear = BackdropImageSet.Build(path,new PixelSize(3840,2160),0,24,false))
    {
        Check(!clear.IsEmpty && clear.Sharp!.PixelSize==new PixelSize(3840,2160),"clear background keeps requested 4K resolution");
        Check(clear.Blur is null && clear.BlurPixels is null,"non-blurred background must not allocate blur resources");
    }

    var buffers = BuildAndRelease(path);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Check(buffers.All(reference=>!reference.IsAlive),"disposed image set must not keep bitmap wrappers or pixel arrays rooted");

    var stress = Stress(path, Check);
    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
    Check(stress.References.All(reference=>!reference.IsAlive),"repeated builds must release all retired arrays and bitmap wrappers");
    Console.WriteLine($"STRESS: 12 alternating 4K/half-screen builds; max image buffers={stress.Resident/1048576.0:F1} MiB; sampled process private peak={stress.Private/1048576.0:F1} MiB");
    Console.WriteLine("RSS/private memory is diagnostic only; this is real Skia image construction, not the Windows/GPU/ClassIsland host.");
}
finally { Directory.Delete(directory,true); }
Console.WriteLine($"PASS: {checks} background memory and pixel checks");

[MethodImpl(MethodImplOptions.NoInlining)]
static (WeakReference[] References, long Resident, long Private) Stress(string path, Action<bool,string> check)
{
    var refs = new List<WeakReference>();
    BackdropImageSet? current = null;
    long maxResidentBytes = 0;
    long sampledPrivateBytes = 0;
    int running = 1;
    var monitor = Task.Run(async () =>
    {
        while (Volatile.Read(ref running)==1)
        {
            using var process = Process.GetCurrentProcess();
            sampledPrivateBytes = Math.Max(sampledPrivateBytes,process.PrivateMemorySize64);
            await Task.Delay(10).ConfigureAwait(false);
        }
    });
    try
    {
        for (var i=0;i<12;i++)
        {
            var wanted = i%2==0 ? new PixelSize(3840,2160) : new PixelSize(1920,2160);
            var next = BackdropImageSet.Build(path,wanted,0,24,true);
            check(next.BuildError is null && !next.IsEmpty,"stress build must succeed");
            check((long)next.SharpPixelsSize.Width*next.SharpPixelsSize.Height<=BackdropImageSet.OpticalPixelLimit,
                "refraction snapshot obeys optical memory cap");
            check(next.Blur is not null && (long)next.Blur.PixelSize.Width*next.Blur.PixelSize.Height<=BackdropImageSet.OpticalPixelLimit,
                "blur bitmap obeys optical memory cap");
            check(next.SharpPixels!.Length==next.SharpPixelsSize.Width*next.SharpPixelsSize.Height
                && next.BlurPixels!.Length==next.Blur!.PixelSize.Width*next.Blur.PixelSize.Height,
                "snapshot dimensions must match the separately scaled optical images");
            refs.AddRange(CaptureReferences(next));
            maxResidentBytes = Math.Max(maxResidentBytes,ResidentBytes(next));
            current?.Dispose();
            current = next;
        }
    }
    finally
    {
        current?.Dispose();
        Volatile.Write(ref running,0);
        monitor.GetAwaiter().GetResult();
    }
    return (refs.ToArray(),maxResidentBytes,sampledPrivateBytes);
}

static long ResidentBytes(BackdropImageSet set) =>
    (long)set.Sharp!.PixelSize.Width*set.Sharp.PixelSize.Height*4
    + (set.Blur is { } blur ? (long)blur.PixelSize.Width*blur.PixelSize.Height*4 : 0)
    + (long)(set.SharpPixels?.Length??0)*4 + (long)(set.BlurPixels?.Length??0)*4;

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference[] CaptureReferences(BackdropImageSet set) =>
    [new WeakReference(set.Sharp), new WeakReference(set.Blur), new WeakReference(set.SharpPixels), new WeakReference(set.BlurPixels)];

[MethodImpl(MethodImplOptions.NoInlining)]
static WeakReference[] BuildAndRelease(string path)
{
    var set = BackdropImageSet.Build(path,new PixelSize(3840,2160),0,24,true);
    if (set.IsEmpty) throw new InvalidOperationException("build before release must succeed");
    var refs = new[]{new WeakReference(set.Sharp),new WeakReference(set.Blur),new WeakReference(set.SharpPixels),new WeakReference(set.BlurPixels)};
    set.Dispose();
    set.Dispose();
    if (!set.IsEmpty || set.Blur is not null || set.SharpPixels is not null || set.BlurPixels is not null
        || set.Matches(path,new PixelSize(3840,2160),0,24,true))
        throw new InvalidOperationException("dispose must clear owned resources and make cache match fail");
    return refs;
}

static unsafe void CreateBackground(string path)
{
    using var bitmap = new WriteableBitmap(new PixelSize(3840,2160),new Vector(96,96),PixelFormat.Bgra8888,AlphaFormat.Premul);
    using (var frame = bitmap.Lock())
        for (var y=0;y<2160;y++)
        {
            var row = (uint*)((byte*)frame.Address+(long)y*frame.RowBytes);
            for (var x=0;x<3840;x++) row[x]=0xff000000u|((uint)(x%256)<<16)|((uint)(y%256)<<8)|(uint)((x+y)%256);
        }
    bitmap.Save(path);
}
