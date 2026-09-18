---
name: memory-introspect
description: Capture .nettrace traces, CPU sampling profiles, allocation reports, .gcdump heap graphs and .dmp process dumps from .NET code with the Memory.Introspect NuGet package — the in-process equivalent of dotnet-trace, dotnet-gcdump and dotnet-dump. Use when adding self-profiling, diagnostics endpoints, leak hunting, allocation analysis or CI performance capture to a .NET app, or when looking up the library's API. Per-capture-type references live in references/.
---

# Memory.Introspect

`Memory.Introspect` captures .NET diagnostic artifacts **from inside your own code**, with no
CLI tool to install and no external process to manage. It is a port of the official
`dotnet-trace`, `dotnet-gcdump` and `dotnet-dump` tools into a library, so everything those
tools do against a PID, you do against an `int processId` — including your own
`Environment.ProcessId`.

```bash
dotnet add package Memory.Introspect
```

Everything starts from one object:

```csharp
using Memory.Introspect;
using Memory.Introspect.Trace;

var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger  = logger,      // ILogger; internal protocol progress goes here. Null = silent.
    Verbose = true,
});
```

`MemoryIntrospector` is cheap, stateless and safe to reuse. Every capture method takes a
process id and returns a result object rather than throwing on failure — check `.Success`.

## The capture types, and which one to reach for

| You want to know | Capture | Entry point | Reference |
| --- | --- | --- | --- |
| Which methods burn CPU / wall-clock | CPU sampling profile | `CollectSamplingProfileAsync` | `references/cpu-sampling.md` |
| Which types are being allocated | Allocation report | `CollectAllocationReportAsync` | `references/allocation-tracing.md` |
| *Where* those allocations come from | Allocation call stacks | `CollectAllocationReportAsync(..., resolveCallStacks: true)` | `references/allocation-call-stacks.md` |
| What the runtime is doing (GC, JIT, exceptions, contention, your own `EventSource`) | EventPipe trace | `CollectTraceAsync` | `references/trace-collect.md` |
| What is on the heap right now, and what roots it | GC heap dump (`.gcdump`) | `CollectMemoryGraphAsync` | `references/gc-dump.md` |
| Post-mortem state: threads, stacks, exceptions, native memory | Process dump (`.dmp`) | `DumpAsync` | `references/process-dump.md` |

Rough cost ordering, cheapest first: `gc-collect` trace → CPU sampling → default trace →
allocation tracing → allocation tracing with call stacks → `.gcdump` → full `.dmp`.

## The five captures in one screenful

```csharp
int pid = Environment.ProcessId;          // or any traceable .NET process on the machine

// 1. CPU sampling — "what is hot right now"
var sample = await introspector.CollectSamplingProfileAsync(pid, TimeSpan.FromSeconds(10));
foreach (var m in sample.TopMethods(count: 10))
    Console.WriteLine($"{m.ExclusiveMetricPercent,6:0.00}%  {m.Name}");

// 2. Allocation report — "what is churning the GC"
var alloc = await introspector.CollectAllocationReportAsync(pid, TimeSpan.FromSeconds(10), count: 10);
AllocationTracing.Write(Console.Out, alloc);

// 3. Full EventPipe trace — "give me the raw events for PerfView / speedscope"
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(30),
    Profiles   = TraceProfileKind.Default,        // dotnet-common + dotnet-sampled-thread-time
    OutputPath = "app.nettrace",
});

// 4. GC heap dump — "what is alive, and who is holding it"
var graph = await introspector.CollectMemoryGraphAsync(pid);
if (graph.Success) graph.SaveToDisk("app.gcdump");

// 5. Process dump — "freeze everything for a debugger"
await introspector.DumpAsync(pid, "app.dmp", Dumper.CollectionType.Mini);
```

## Rules that apply to every capture

- **Nothing throws on a failed capture.** `TraceResult`, `SamplingProfileResult` and
  `MemoryGraphResult` all carry `Success`, `Cancelled` and `Exception`. Check `Success`
  before using the payload; a cancelled capture still returns whatever was collected.
- **Stream to a file for anything non-trivial.** Set `TraceCollectionOptions.OutputPath` and
  the trace never has to fit in memory. Leave it null and the bytes land in
  `TraceResult.NetTraceData` — fine for a few seconds of `gc-collect`, not for a minute of a
  busy service.
- **A duration is an upper bound, not a promise.** A capture also ends when the cancellation
  token fires, the stopping event is seen, or the target process exits.
- **Cancellation keeps the data.** Passing a `CancellationToken` that fires mid-capture gives
  you a result with `Cancelled = true` and the events captured so far — it is a "stop now",
  not an "abort".
- **Tracing costs the traced process something.** Event collection, rundown and stack walking
  all run inside the target. `references/large-processes.md` covers what to turn down.
- **Self-tracing is supported and shows up in its own numbers.** See
  `references/self-tracing.md`.

## Reference index

**Setup and fundamentals**
- `references/getting-started.md` — install, `MemoryIntrospector.Create`, every
  `MemoryIntrospectorOptions` field, platform and privilege requirements.
- `references/api-reference.md` — every public type and member, grouped by namespace.
- `references/troubleshooting.md` — empty reports, dropped events, missing method names,
  permission errors, and what each symptom actually means.

**Capturing traces**
- `references/trace-collect.md` — `CollectTraceAsync` and every `TraceCollectionOptions`
  field. Start here for tracing.
- `references/profiles.md` — the built-in profiles (`dotnet-common`,
  `dotnet-sampled-thread-time`, `gc-verbose`, `gc-collect`, `database`) and what each enables.
- `references/providers-and-clrevents.md` — provider spec strings, `ClrEventKeywords`,
  `EventLevel`, and parsing them from configuration.
- `references/custom-eventsource.md` — tracing your own `EventSource` alongside runtime events.
- `references/stopping-events.md` — end a trace on an event instead of a timer.
- `references/large-processes.md` — buffer sizing, rundown, stack walking, dropped events.
- `references/self-tracing.md` — tracing the process you are running in.
- `references/diagnostic-ports.md` — connecting through a diagnostic port instead of a PID,
  including suspended-startup tracing.

**Analysing what you captured**
- `references/cpu-sampling.md` — sampling profiles and `dotnet-trace report topN` output.
- `references/allocation-tracing.md` — per-type allocation reports.
- `references/allocation-call-stacks.md` — resolving the stacks behind the allocations.
- `references/formats-and-conversion.md` — speedscope, Chromium/Perfetto, PerfView, VS.
- `references/offline-reports.md` — running the same reports over `.nettrace` files you
  already have.

**The other artifact types**
- `references/gc-dump.md` — `.gcdump` heap graphs and leak hunting.
- `references/process-dump.md` — `.dmp` process dumps and the four collection types.

**Putting it in an app**
- `references/diagnostics-endpoint.md` — exposing captures over HTTP in a service.
- `references/ci-and-tests.md` — regression tests and CI pipelines that capture artifacts.

To find a reference, match what you are trying to *do* against the index above; each file
opens with the API signature, then the options that matter, then a working example.
