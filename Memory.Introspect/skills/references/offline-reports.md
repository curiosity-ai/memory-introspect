---
name: offline-reports
description: Run Memory.Introspect reports over .nettrace files captured earlier or elsewhere — ReportTopMethods, ReportTopAllocatedTypes, TraceReport and AllocationTracing.FromFile — so capture and analysis can be separated across machines or CI stages. Use when analysing a trace file you already have.
---

# Reports over an existing `.nettrace` file

Capture and analysis are independent. A trace captured on a production box can be analysed
anywhere — including on a machine that never ran the target process.

## Top methods

```csharp
IReadOnlyList<SampledMethod> ReportTopMethods(
    string traceFilePath, int count = 5, bool inclusive = false,
    IEnumerable<string> excludedModules = null,
    IEnumerable<string> blockingMethodPatterns = null);
```

```csharp
var top = introspector.ReportTopMethods("app.nettrace", count: 10);
TraceReport.WriteTopMethodsReport(Console.Out, top, inclusive: false, verbose: true);
```

Unlike the result-bound `TopMethods`, the filter parameters here default to **no filtering**
when null — pass `MemoryIntrospectorOptions.SamplingExcludedModules` explicitly if you want
the same defaults a live capture applies.

Or straight through the static helper:

```csharp
var methods = TraceReport.TopMethodsFromFile("app.nettrace", count: 10, inclusive: false,
    excludedModules: new[] { "Memory.Introspect" },
    blockingMethodPatterns: null, log: Console.Out);
```

## Top allocated types

```csharp
AllocationReport ReportTopAllocatedTypes(
    string traceFilePath, int count = 10, bool resolveCallStacks = false);
```

```csharp
var report = introspector.ReportTopAllocatedTypes("allocations.nettrace", count: 20);
AllocationTracing.Write(Console.Out, report);

// With stacks — only produces named frames if the capture included rundown
var stacks = introspector.ReportTopAllocatedTypes("allocations.nettrace", count: 20, resolveCallStacks: true);
AllocationTracing.WriteCallStacks(Console.Out, stacks);
```

Or the static form: `AllocationTracing.FromFile(path, count, log, resolveCallStacks)`.

`report.IsEmpty` means the trace had no allocation events — usually because it was captured
without `AllocationTracing.RequiredClrEvents` at Verbose. See `allocation-tracing.md`.

## What the file must contain

The report can only report what the capture recorded:

| Report | Needs the capture to have enabled |
| --- | --- |
| `ReportTopMethods` | `TraceProfileKind.DotNetSampledThreadTime` (the sample profiler) |
| `ReportTopAllocatedTypes` | `AllocationTracing.RequiredClrEvents` at `EventLevel.Verbose` |
| `…(resolveCallStacks: true)` | The above **plus rundown** |
| Speedscope / Chromium conversion | `DotNetSampledThreadTime` |

Neither report throws on a trace that lacks the right events — you get an empty list or an
empty report. Check before concluding the process was idle.

## A capture-now, analyse-later pipeline

```csharp
// Stage 1, on the production host: capture only, minimal cost, ship the file
await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(30),
    Profiles   = TraceProfileKind.DotNetSampledThreadTime,
    OutputPath = "/var/diagnostics/app-20260918.nettrace",
});

// Stage 2, anywhere: analysis, which is where the CPU and temp disk actually go
var analyzer = MemoryIntrospector.Create();
var top = analyzer.ReportTopMethods("app-20260918.nettrace", count: 25);
string flame = analyzer.ConvertTraceFile("app-20260918.nettrace", TraceFileFormat.Speedscope);
```

This is the right split for production: the expensive parts (ETLX conversion, symbol
resolution, stack aggregation) never run on the traced host.

## Related

- `formats-and-conversion.md` — converting instead of reporting
- `ci-and-tests.md` — wiring this into a pipeline
