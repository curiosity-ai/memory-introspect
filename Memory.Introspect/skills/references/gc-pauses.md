---
name: gc-pauses
description: Detect long GC pauses in a running .NET process and explain their root cause with Memory.Introspect's GcPauseMonitor — a cheap always-on EventPipe session that breaks every pause into suspension, mark (by root kind), plan, relocate and compact time, and escalates to a detailed capture (call stacks of whoever triggered the GC, allocations, pinning, optional heap snapshot) when a pause is long. Use when an app freezes, a heartbeat or request latency spikes, or GC.Collect / compaction / a huge object graph is suspected.
---

# GC pause monitoring (why did the app freeze?)

`StartGcPauseMonitorAsync` watches a process for long GC pauses and explains each one as soon as
it ends, from the events the GC already emits. When a pause crosses the threshold it can
escalate on its own into a short, more expensive capture that names the code involved.

```csharp
using Memory.Introspect;
using Memory.Introspect.GcPauses;

var introspector = MemoryIntrospector.Create(new() { Logger = logger });

await using GcPauseMonitor monitor = await introspector.StartGcPauseMonitorAsync(Environment.ProcessId, new GcPauseMonitorOptions
{
    LongPauseThreshold = TimeSpan.FromMilliseconds(500),
    OnLongPause        = pause => logger.LogWarning("{Report}", pause.ToReport()),
    DetailedCapture    = new GcPauseDetailedCaptureOptions
    {
        OutputDirectory = "/var/log/myapp/gc",
        OnReport        = report => logger.LogWarning("{Report}", report.ToReport()),
    },
});
```

Works against the current process (`Environment.ProcessId`) or any traceable .NET process.

## Two tiers

| Tier | When | Session | Cost |
| --- | --- | --- | --- |
| Always-on | From start to dispose | `Microsoft-Windows-DotNETRuntime`, GC keyword (0x1), **Informational**, no stack walk, no rundown, 16 MB buffer | ~12 small events per GC; nothing per allocation |
| Detailed | After a long pause, at most `MaxCaptures` times, `Cooldown` apart | GC keyword at **Verbose** (+ private GC keyword), stack walk + rundown, optional object movement | Allocation ticks every ~100 KB, a stack per event, rundown at the end |

Measured against a 900 MB, 16 M object heap with multi-second pauses, the always-on tier read ~17
events per GC, dropped none, and made no difference to throughput or pause times beyond run-to-run
noise (see `Memory.Introspect.GcPauseRepro/README.md` in the repository).

The always-on tier needs .NET 9+ on the target to turn the stack walk off; on older runtimes it
falls back to recording stacks (`GcPauseMonitor.StackwalkEnabled` tells you).

## What one pause tells you (always-on tier)

Every `GcPause` is one SuspendEE → RestartEE window:

| Field | Source event | Answers |
| --- | --- | --- |
| `PauseMSec` | GCSuspendEEBegin → GCRestartEEEnd | How long managed threads were stopped |
| `SuspendMSec` | GCSuspendEEBegin → GCSuspendEEEnd | Time-to-safe-point: threads slow to stop |
| `Gc.Reason`, `Gc.Type`, `Gc.Generation` | GCStart | Induced (GC.Collect)? Blocking or background? Which gen? |
| `Gc.TriggeringThreadId` | GCTriggered | Which OS thread asked for it |
| `Gc.Phases` | GCGlobalHeapHistory_V4 (.NET 7+) | mark (roots / short weak / finalization / long weak), plan, relocate, compact or sweep — in µs, from the GC itself |
| `Gc.MarkRoots` | GCMarkWithType | Mark time and live bytes per root kind: stack, handles, older generation (cards), finalization queue, **overflow** |
| `Gc.CompactReasons`, `Gc.CondemnReasons` | GCPerHeapHistory / GCGlobalHeapHistory | Why it compacted, why this generation |
| `Gc.Generations` | GCPerHeapHistory | Size and fragmentation per generation before/after |
| `Gc.PromotedBytes`, `PinnedObjectCount`, `GCHandleCount`, ... | GCHeapStats | How much was live, pinned, finalizable |
| `Gc.CompactedLargeObjectHeap`, `LohCompactMSec` | GCLOHCompact | Whether the LOH was compacted and what it cost |

`Cause` gives the dominant category and `Findings` the reasoning in plain text:

| `GcPauseCause` | Means | Typical fix |
| --- | --- | --- |
| `TimeToSafePoint` | Threads took long to reach a safe point | Tight loops without calls, long cooperative-mode native calls, CPU starvation |
| `Marking` | Tracing the live graph dominated | Fewer live objects/references; check `MarkRoots` for overflow, handles, finalization |
| `Compaction` | Plan + relocate + compact dominated | Avoid forced compaction / LOH compaction; reduce survivors and pinning |
| `CardScanning` | A gen0/gen1 GC spent its time on old→young references | Stop storing new objects into long-lived ones at a high rate |
| `Sweeping` | Building free lists dominated | Usually a huge, fragmented gen2 |
| `BackgroundGc` | The stop-the-world part of a BGC | Final mark had a lot left to do |
| `Unaccounted` | Most of the pause is outside the GC's own phase timings | A heap walk (`.gcdump`, profiler) inside the GC, or the GC thread starved of CPU / throttled |
| `NonGcSuspension` | EE suspended without a GC | Debugger, profiler attach/ReJIT, thread dumps |

Example (5 M interlinked nodes in a `ConcurrentDictionary`, `GC.Collect(2, Forced, blocking, compacting)` + LOH compaction):

```
8,027 ms pause: GC #61 gen2 NonConcurrentGC reason=InducedCompacting compacting+LOH -> cause: Compaction
  phases: mark 1,849 ms (roots 1,849, ...), plan 195 ms, relocate 5,241 ms, compact 741 ms
  mark by root: overflow 1,042 ms/67.7 MB, stack 806 ms/833.1 MB, handles 0.8 ms/2.0 MB
  heap after 908.7 MB, promoted 906.0 MB, pinned objects 513, GC handles 643, heaps 1
  compacted because: compact_loh_forced
  * The GC was induced (InducedCompacting): something called GC.Collect asking for a compacting collection, on OS thread 1815. ...
  * Full blocking gen2 GC: the entire heap (844.7 MB in gen2) had to be marked with all threads stopped.
  * Compaction took 6,177 ms (plan 195, relocate 5,241, compact 741): every surviving object was moved ...
  * The mark stack overflowed: 1,042 ms went into recovering from it ...
  * The large object heap was compacted (710 ms). ...
  * Workstation GC: a single GC thread does the whole collection. Server GC ... parallelises mark and compact.
```

## When is a pause reported?

After it ends — never during it. The GC writes its events while the world is stopped, and the
monitor reads them once threads resume: measured at ~100 ms after the pause ended (EventPipe
flushes its buffers every 100 ms). Back-to-back pauses delay it further, since the monitor's
thread is paused along with everything else, self-monitoring or not. To catch the *next*
occurrence in more detail, use the detailed tier.

## The detailed tier

When a long pause is seen (and `DetailedCapture.Enabled`), the monitor starts a second
EventPipe session to a `.nettrace` in `OutputDirectory`. It records until it has seen another
long pause plus `StopAfterLongPause` (default 2 s), or until `MaxDuration`. Long pauses tend to
repeat, so the capture is usually short. Then it analyses the file into a `GcPauseDetailedReport`:

- `Pauses` / `LongPauses` — the same breakdown, for every pause in the capture.
- `TriggerSites` — the call stacks of the GCTriggered events, i.e. **who** asked for each GC:
  the `GC.Collect` caller, or the allocation that ran out the budget. Needs `CollectStacks`.
- `Allocations` — what was being allocated (GCAllocationTick). Needs `VerboseGcEvents`.
- `PinnedTypes` — what was pinned at GC time. Needs `VerboseGcEvents`.
- `HeapTypes` — with `CollectGcDump = true`, a `.gcdump` is taken after the trace and its type
  histogram shows **which types make up the graph the GC had to mark**. Taking it walks the whole
  heap inside an induced blocking gen2 GC — on a 5 M node graph that pause was ~10 s, longer than
  the pauses being investigated — so it is off by default. A heap with more objects than
  `MemoryIntrospectorOptions.MaxNodeCount` (10 M) gives a partial snapshot
  (`HeapSnapshotTruncated`); raise it for big heaps.

```
Who triggered the GCs (GCTriggered call stacks):
  1x InducedCompacting (GC #61)
      System.GC.Collect(int32,value class System.GCCollectionMode,bool,bool,bool)
      Program+<>c__DisplayClass0_0.<<Main>$>g__CompactorTick|6()
      System.Threading.TimerQueueTimer.Fire(bool)
      ...
```

Trigger a detailed capture yourself with `monitor.CaptureDetailsAsync(duration)`; it records
for the whole duration regardless of pauses.

## Offline

```csharp
// Any .nettrace with GC events: a detailed capture, a gc-collect or gc-verbose trace...
GcPauseDetailedReport report = introspector.ReportGcPauses("app.nettrace", TimeSpan.FromMilliseconds(200),
                                                           resolveTriggerStacks: true);
Console.WriteLine(report.ToReport());
```

## Options

| Option | Default | Notes |
| --- | --- | --- |
| `LongPauseThreshold` | 200 ms | Pauses at or above it are `IsLong` and can trigger a capture |
| `OnPause` / `OnLongPause` | null | Called on the event-processing thread; keep them quick. Exceptions are logged and swallowed |
| `CircularBufferSizeInMB` | 16 | Always-on session buffer; check `EventsLost` |
| `RecentLongPauseCapacity` | 32 | Size of `RecentLongPauses` |
| `DetailedCapture.Enabled` | true | false = always-on tier only |
| `DetailedCapture.MaxDuration` | 60 s | Upper bound per capture |
| `DetailedCapture.StopAfterLongPause` | 2 s | Stop this long after the capture saw a long pause; null = full duration |
| `DetailedCapture.CollectStacks` | true | Stack walk + rundown, for `TriggerSites` |
| `DetailedCapture.VerboseGcEvents` | true | Allocation ticks, pinned objects, server GC joins |
| `DetailedCapture.ObjectSurvivalAndMovement` | false | GCHeapSurvivalAndMovement; very verbose on big heaps |
| `DetailedCapture.CollectGcDump` | false | Heap snapshot + type histogram after the trace (induces a GC) |
| `DetailedCapture.MaxCaptures` / `Cooldown` | 3 / 5 min | Rate limit |

## Gotchas

- **The detailed tier is not free.** With stack walking on, every GC event and every allocation
  tick records a stack, and rundown runs at the end. Keep `MaxCaptures` low in production and use
  `GcPause.DuringDetailedCapture` to discount pauses measured while one was running.
- **Phase times need .NET 7+.** Older runtimes report the pause, reason, type and mark-by-root
  breakdown, but `Gc.Phases` is null.
- **Mark time per root is wall-clock between GCMarkWithType events**, averaged over heaps under
  server GC; it is the same method PerfView uses.
- **Background GCs** report only their stop-the-world parts as pauses; their concurrent mark
  runs alongside the application and is not a pause.
- A non-GC suspension (`Gc == null`) is still a pause: a debugger break or a profiler attach
  stops the world too.
