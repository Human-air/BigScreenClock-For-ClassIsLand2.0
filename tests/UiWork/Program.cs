using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using EveningSelfStudyClock.Helpers;

int checks = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
    checks++;
}
var queue = new ConcurrentQueue<Action>();
int runs = 0;
using var work = new CoalescedUiWork(queue.Enqueue, () => runs++);
Parallel.For(0, 10_000, _ => work.Request());
Check(queue.Count == 1, "UI stalls must not create an unbounded callback queue");
queue.TryDequeue(out var first);
first!();
Check(runs == 1, "one merged refresh executes");
work.Request();
Check(queue.Count == 1, "new request accepted after completion");
work.Dispose();
queue.TryDequeue(out var stopped);
stopped!();
Check(runs == 1, "stopped session must not run queued refresh");
work.Request();
Check(queue.IsEmpty, "disposed work must not enqueue more refreshes");

CoalescedUiWork? reentrant = null;
reentrant = new CoalescedUiWork(queue.Enqueue, () =>
{
    runs++;
    Parallel.For(0, 1000, _ => reentrant!.Request());
});
reentrant.Request();
queue.TryDequeue(out var active);
active!();
Check(queue.IsEmpty, "requests during execution must also coalesce");
reentrant.Dispose();

int oldRuns = 0, newRuns = 0;
var oldSession = new CoalescedUiWork(queue.Enqueue, () => oldRuns++);
oldSession.Request();
oldSession.Dispose();
using var newSession = new CoalescedUiWork(queue.Enqueue, () => newRuns++);
newSession.Request();
while (queue.TryDequeue(out var item)) item();
Check(oldRuns == 0 && newRuns == 1, "old queued callbacks cannot interfere with replacement session");

bool fail = true;
using var retry = new CoalescedUiWork(action =>
{
    if (fail) throw new InvalidOperationException("scheduler unavailable");
    queue.Enqueue(action);
}, () => runs++);
try { retry.Request(); } catch (InvalidOperationException) { }
fail = false;
retry.Request();
Check(queue.Count == 1, "scheduler exception must not permanently occupy the pending slot");
queue.TryDequeue(out var retried);
retried!();

var (retainedWork, target) = CreateTarget(queue);
retainedWork.Request();
retainedWork.Dispose();
GC.Collect();
GC.WaitForPendingFinalizers();
GC.Collect();
Check(!target.IsAlive, "queued disposed work must release callback target even before UI dequeues it");
while (queue.TryDequeue(out var item)) item();
var rangeText = EveningSelfStudyClock.Services.CiTimePointFormat.Range(TimeSpan.FromHours(20.5), TimeSpan.FromHours(21.5));
for (var mode = 0; mode <= 5; mode++)
{
    var text = EveningSelfStudyClock.Services.CiTimePointFormat.Select(mode, rangeText, 600, 3000, 3600, false, 10, 10, false);
    Check(text.Contains("—") == (mode == 0), "time modes never mix start/end with progress or countdown");
}
Check(!EveningSelfStudyClock.Services.CiTimePointFormat.Select(0,rangeText,3595,5,3600,true,10,10,false).Contains("—"), "CI countdown override replaces range");
var clockTimes = new List<DateTime>();
var pulse = new ClockDisplayPulse(clockTimes.Add);
var baseTime = new DateTime(2026,11,17,20,0,0);
foreach(var milliseconds in new[]{950,1050,1150,1950,2050,2950,3050}) pulse.Tick(baseTime.AddMilliseconds(milliseconds));
Check(clockTimes.Count==4 && clockTimes.Select(t=>t.Second).SequenceEqual(new[]{0,1,2,3}), "100ms wall-clock samples tolerate timer jitter without duplicated updates");
pulse.Tick(baseTime.AddSeconds(10));
Check(clockTimes.Last().Second==10, "after a real stall display current time instead of replaying obsolete seconds");
pulse.Tick(baseTime.AddSeconds(5));
Check(clockTimes.Last().Second==5, "system time adjustment is reflected immediately");
Console.WriteLine($"PASS: {checks} bounded UI work lifecycle checks");

[MethodImpl(MethodImplOptions.NoInlining)]
static (CoalescedUiWork Work, WeakReference Target) CreateTarget(ConcurrentQueue<Action> queue)
{
    var target = new CallbackTarget();
    return (new CoalescedUiWork(queue.Enqueue, target.Run), new WeakReference(target));
}
sealed class CallbackTarget { public void Run() { } }
