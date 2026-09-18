---
name: trace-collect
description: Capture EventPipe .nettrace traces with CollectTraceAsync — every TraceCollectionOptions field, streaming to disk vs buffering in memory, progress reporting, cancellation, and reading the TraceResult. The dotnet-trace collect equivalent. Use when capturing a trace of any kind from a .NET process.
---

# Collecting a trace (`dotnet-trace collect`)

`CollectTraceAsync` is the general-purpose capture: everything else in the library
(allocation reports, CPU sampling) is this method with a particular provider set.

## Signatures

```csharp
Task<TraceResult> CollectTraceAsync(int processId, TimeSpan duration, CancellationToken ct = default);
Task<TraceResult> CollectTraceAsync(int processId, TraceCollectionOptions options, CancellationToken ct = default);
```

The `TimeSpan` overload is shorthand for the default profiles buffered in memory. Use the
options overload for anything real.

## The smallest useful capture

```csharp
using Memory.Introspect;
using Memory.Introspect.Trace;

var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions { Logger = logger });

var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(30),
    OutputPath = "app.nettrace",
    Progress   = new Progress<TraceProgress>(p => logger.LogInformation("{0}", p)),
});

if (!trace.Success)
{
    logger.LogError(trace.Exception, "trace failed (cancelled={0})", trace.Cancelled);
    return;
}

logger.LogInformation("{0:N0} bytes in {1:0.##}s → {2}",
    trace.TraceSizeInBytes, trace.Elapsed.TotalSeconds, trace.TraceFilePath);
```

With no `Profiles`, `Providers` or `ClrEvents` set, the capture uses the same default as the
CLI tool: `TraceProfileKind.Default` = `dotnet-common` + `dotnet-sampled-thread-time`.

## TraceCollectionOptions

### What to record

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `Profiles` | `TraceProfileKind` | `None` | Built-in profiles, `[Flags]`, combine with `\|`. See `profiles.md`. |
| `Providers` | `IReadOnlyList<string>` | `null` | `dotnet-trace --providers` syntax: `Name[:Keywords[:Level[:KeyValueArgs]]]`, keywords in hex. See `providers-and-clrevents.md`. |
| `ProviderConfigurations` | `IReadOnlyList<EventPipeProvider>` | `null` | Already-typed providers, merged with `Providers`. |
| `ClrEvents` | `ClrEventKeywords` | `None` | `[Flags]` keywords on `Microsoft-Windows-DotNETRuntime` (`--clrevents`). |
| `ClrEventLevel` | `EventLevel?` | `null` → `Informational` | Verbosity for `ClrEvents` (`--clreventlevel`). |

All four sources are merged into one provider set, exactly the way the CLI does it. The set
actually enabled comes back in `TraceResult.Providers`.

### How long to record

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `Duration` | `TimeSpan?` | `null` | Null or zero records until cancellation, the stopping event, or process exit. |
| `StoppingEventProviderName` | `string` | `null` | Stop on an event instead — see `stopping-events.md`. |
| `StoppingEventEventName` | `string` | `null` | Narrows the stopping event to one event name. |
| `StoppingEventPayloadFilter` | `IReadOnlyDictionary<string,string>` | `null` | Narrows further by payload field values. Requires both fields above. |

### Where it goes

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `OutputPath` | `string` | `null` | Set → streamed to this `.nettrace` file (the directory is created). Null → buffered into `TraceResult.NetTraceData`. |
| `Format` | `TraceFileFormat` | `NetTrace` | Also produce speedscope/Chromium output — see `formats-and-conversion.md`. |
| `ConvertedOutputPath` | `string` | `null` | Where the converted file goes; derived from `OutputPath` when null. |

### Cost and reliability knobs

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `CircularBufferSizeInMB` | `int?` | `null` → introspector's 1024 | Runtime-side buffer. Raise when events are dropped. |
| `StreamCopyBufferSizeInBytes` | `int` | 1 MB | Client-side pump buffer. Raise for very chatty processes. Note the 1 MB default lands on the LOH. |
| `Rundown` | `bool?` | `null` → what the profiles ask for | Rundown names jitted methods. Turning it off makes stopping much faster and the file smaller, at the cost of unresolved frames. |
| `RundownKeyword` | `long?` | `null` | Explicit keyword, overriding `Rundown` and the profiles. |
| `RequestStackwalk` | `bool` | `true` | Record a stack for every event. `false` is much cheaper on busy processes but **requires .NET 9+ on the target**. |
| `RetryOnUnsupportedConfiguration` | `bool` | `true` | When the target rejects the rundown configuration, retry with a progressively simpler one (CLI behaviour). `false` fails instead. |

### Connection and progress

| Field | Type | Default | Notes |
| --- | --- | --- | --- |
| `DiagnosticPort` | `string` | `null` → introspector's | Connect through a port instead of a PID. See `diagnostic-ports.md`. |
| `ResumeRuntime` | `bool` | `false` | Resume a runtime suspended at startup waiting for a diagnostics connection (`--resume-runtime`). |
| `Progress` | `IProgress<TraceProgress>` | `null` | Invoked roughly once per second with `Elapsed` and `SizeInBytes`. |

## TraceResult

```csharp
bool      Success;                              // trace data was captured
bool      Cancelled;                            // ended because the token fired
Exception Exception;                            // the failure, when it threw
int       ProcessId;
TimeSpan? RequestedDuration;
TimeSpan  Elapsed;                              // how long it actually ran
bool      StoppedByStoppingEvent;
bool      StoppingEventPayloadFilterMismatched; // the filter named fields the event lacks
IReadOnlyList<EventPipeProvider> Providers;     // what was really enabled
long      RundownKeyword;                       // 0 when rundown was off
int       CircularBufferSizeInMB;
string    TraceFilePath;                        // set when streamed to disk
string    ConvertedFilePath;                    // set when Format != NetTrace
byte[]    NetTraceData;                         // set when buffered in memory
long      TraceSizeInBytes;
```

Methods on the result:

```csharp
void   SaveToDisk(string fileName);
string ConvertTo(TraceFileFormat format, string outputPath = null, TextWriter log = null);
IReadOnlyList<SampledMethod> TopMethods(int count = 5, bool inclusive = false,
        IEnumerable<string> excludedModules = null,
        IEnumerable<string> blockingMethodPatterns = null, TextWriter log = null);
void   WriteTopMethodsReport(TextWriter output, int count = 5, bool inclusive = false,
        bool verbose = false, IEnumerable<string> excludedModules = null,
        IEnumerable<string> blockingMethodPatterns = null);
AllocationReport TopAllocatedTypes(int count = 10, TextWriter log = null, bool resolveCallStacks = false);
void   WriteAllocationReport(TextWriter output, int count = 10, bool verbose = false);
```

Any of the analysis methods work on an in-memory capture too — the trace is spilled to a
temporary file and cleaned up afterwards. The one exception is `ConvertTo` with no
`outputPath` on an in-memory trace: there is no `.nettrace` path to derive a name from, so it
throws. Pass an explicit path, or set `OutputPath` when collecting.

## In memory vs streamed to disk

```csharp
// In memory: fine for short, low-volume captures. Bytes land in NetTraceData.
var small = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration = TimeSpan.FromSeconds(4),
    Profiles = TraceProfileKind.GcCollect,
});
byte[] bytes = small.NetTraceData;      // TraceFilePath is null

// Streamed: what you want for anything longer or chattier.
var big = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromMinutes(2),
    OutputPath = Path.Combine(dir, "app.nettrace"),
});                                      // NetTraceData is null
```

`SaveToDisk` works either way, so code that does not care can always call it.

## Cancellation

```csharp
using var cts = new CancellationTokenSource();
var task = introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromMinutes(10),   // upper bound
    OutputPath = "app.nettrace",
}, cts.Token);

// …something interesting happened, stop now and keep what we have
cts.Cancel();
var trace = await task;

// trace.Cancelled == true, trace.Success == true, trace.TraceSizeInBytes > 0
```

This is the idiomatic "record until X happens" shape when X is something your own code
notices. When X is an *event* the target emits, use a stopping event instead
(`stopping-events.md`) — it does not need your code in the loop.

## Related

- `profiles.md`, `providers-and-clrevents.md` — deciding what to record
- `large-processes.md` — when the defaults are too expensive
- `formats-and-conversion.md`, `cpu-sampling.md`, `allocation-tracing.md` — what to do with
  the result
