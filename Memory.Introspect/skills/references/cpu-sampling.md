---
name: cpu-sampling
description: Find which methods are hot with Memory.Introspect — CollectSamplingProfileAsync, the SamplingProfileResult, TopMethods and the dotnet-trace report topN table, plus the module and blocked-thread filters that keep parked threads out of the report. Use when profiling CPU or wall-clock time.
---

# CPU sampling profiles

The .NET sample profiler walks every managed thread's stack at ~100 Hz. Aggregating those
samples tells you where wall-clock time goes.

Two ways in, depending on what else you want from the capture:

| | `CollectSamplingProfileAsync` | `CollectTraceAsync` + `TopMethods` |
| --- | --- | --- |
| Providers | Just the sample profiler | Whatever you configure |
| Output | Always buffered in memory | Streamed or buffered |
| Use it when | You only want the top-N | You also want GC/exception/custom events in the same trace |

## CollectSamplingProfileAsync

```csharp
Task<SamplingProfileResult> CollectSamplingProfileAsync(
    int processId, TimeSpan duration, CancellationToken ct = default);
```

```csharp
var sample = await introspector.CollectSamplingProfileAsync(pid, TimeSpan.FromSeconds(10));

if (!sample.Success)
{
    logger.LogError(sample.Exception, "sampling failed (cancelled={0})", sample.Cancelled);
    return;
}

sample.SaveToDisk("profile.nettrace");         // keep the raw trace for PerfView

foreach (SampledMethod m in sample.TopMethods(count: 10, inclusive: false))
    Console.WriteLine($"{m.ExclusiveMetricPercent,6:0.00}%  {m.Name}");
```

`duration` must be positive (`ArgumentOutOfRangeException` otherwise).

### SamplingProfileResult

```csharp
bool      Success;
bool      Cancelled;
Exception Exception;
int       ProcessId;
TimeSpan  Duration;
byte[]    NetTraceData;
int       TraceSizeInBytes;
IReadOnlyList<string> DefaultExcludedModules;         // as configured at capture time
IReadOnlyList<string> DefaultBlockingMethodPatterns;  // as configured at capture time
void      SaveToDisk(string fileName);                // throws if nothing was captured
IReadOnlyList<SampledMethod> TopMethods(int count = 5, bool inclusive = false,
        IEnumerable<string> excludedModules = null,
        IEnumerable<string> blockingMethodPatterns = null, TextWriter log = null);
```

### SampledMethod

```csharp
string Name;                     // "Module!Namespace.Type.Method(args)"
float  InclusiveMetric;          // time with this method anywhere on the stack
float  ExclusiveMetric;          // time with this method on top of the stack
float  InclusiveMetricPercent;
float  ExclusiveMetricPercent;
```

**Exclusive** answers "which method is actually running"; **inclusive** answers "which call
tree is expensive". Rank by inclusive with `TopMethods(count, inclusive: true)`.

## The two filters

Without filtering, a top-N over a mostly idle process is dominated by threads parked in
waits, and a self-profile is dominated by this library's own frames. Both filters are on by
default.

**Module exclusion** — `MemoryIntrospectorOptions.SamplingExcludedModules`, default
`["Memory.Introspect"]`. Frames are matched by module prefix against the
`Module!Namespace.Type.Method(args)` frame name.

**Blocked-thread patterns** — `MemoryIntrospectorOptions.SamplingBlockingMethodPatterns`,
about thirty case-insensitive regexes covering `ManualResetEventSlim.Wait`,
`Monitor.Wait`/`ObjWait`, `WaitHandle.WaitOne`/`WaitAny`/`WaitAll`, `SemaphoreSlim.Wait`,
`Mutex.WaitOne`, `Thread.Sleep`/`Join`, `Task.Wait`/`WaitAny`/`WaitAll`/`InternalWaitCore`,
`Barrier.SignalAndWait`, `CountdownEvent.Wait`, `LowLevelLifoSemaphore.Wait*`,
`LowLevelLock.Acquire`, `PortableThreadPool.WorkerThread.WorkerDoWork`,
`BlockingCollection.TryTakeWithNoTimeValidation` and friends. If **any** frame in a sample's
stack matches, the whole sample is dropped.

Override per call or globally:

```csharp
// Globally, at construction
var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    SamplingExcludedModules        = new[] { "Memory.Introspect", "MyCompany.Infrastructure" },
    SamplingBlockingMethodPatterns = Array.Empty<string>(),   // keep blocked threads
});

// Per report: null = use the configured defaults, empty list = disable that filter
var unfiltered = sample.TopMethods(count: 20,
    excludedModules: Array.Empty<string>(),
    blockingMethodPatterns: Array.Empty<string>());
```

Investigating a deadlock or a thread-pool starvation is exactly when you want the blocked
threads back — pass an empty pattern list.

## The `report topN` table

```csharp
// From a trace captured with CollectTraceAsync
trace.WriteTopMethodsReport(Console.Out, count: 10, inclusive: false, verbose: false);

// Or format a list you already have
TraceReport.WriteTopMethodsReport(Console.Out, sample.TopMethods(10), inclusive: false, verbose: true);
```

`verbose: true` prints full method signatures instead of truncating them.

## Sampling inside a bigger trace

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(30),
    Profiles   = TraceProfileKind.DotNetSampledThreadTime | TraceProfileKind.DotNetCommon,
    OutputPath = "app.nettrace",
});

foreach (var m in trace.TopMethods(count: 10))
    Console.WriteLine($"{m.ExclusiveMetricPercent,6:0.00}%  {m.Name}");
```

A trace captured *without* `DotNetSampledThreadTime` has no samples, so `TopMethods` comes
back empty — that is the single most common cause of an empty top-N.

## Related

- `offline-reports.md` — `ReportTopMethods` over a `.nettrace` you already have
- `formats-and-conversion.md` — the same data as a speedscope flame graph
- `self-tracing.md` — profiling the process you are running in
