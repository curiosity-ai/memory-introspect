---
name: troubleshooting
description: Diagnose Memory.Introspect captures that come back empty, lossy or unreadable — empty top-N reports, IsEmpty allocation reports, question-mark frames, dropped events, connection and privilege failures, timeouts and conversion errors. Use when a capture did not produce what was expected.
---

# Troubleshooting

Start by passing a `Logger`. Almost every symptom below is explained explicitly in the
internal log, which is discarded when `MemoryIntrospectorOptions.Logger` is null.

```csharp
var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger = logger, Verbose = true, LogLevel = LogLevel.Debug,
});
```

## `TopMethods` returns nothing

**The capture had no sample profiler events.** `TopMethods` reads
`Microsoft-DotNETCore-SampleProfiler` samples and nothing else.

```csharp
Profiles = TraceProfileKind.DotNetSampledThreadTime   // or TraceProfileKind.Default
```

If the profile *was* enabled, the next suspect is the blocked-thread filter: on a mostly idle
process every sample can be filtered out. Re-run with the filters off to confirm:

```csharp
var all = sample.TopMethods(count: 20,
    excludedModules: Array.Empty<string>(),
    blockingMethodPatterns: Array.Empty<string>());
```

Note the offline `ReportTopMethods` defaults to *no* filtering, so a difference between it and
`result.TopMethods()` on the same trace is the filters, not the data.

## `AllocationReport.IsEmpty` is true

The trace carried no allocation events. Almost always one of:

1. **Wrong keywords.** You need
   `ClrEventKeywords.Gc | Type | GcHeapAndTypeNames` — i.e.
   `AllocationTracing.RequiredClrEvents`.
2. **Wrong level.** Allocation ticks are **Verbose**. At `Informational` you get GC collection
   events and no allocations. This is the most common cause.
3. **Nothing was allocated.** `GCAllocationTick` fires once per ~100 KB; a short capture of a
   quiet process genuinely has no events.

The safe configuration is `AllocationTracing.CreateOptions(duration, outputPath)` — it sets
all of it correctly.

## Call stacks are all `?` frames

The capture did not include **rundown**, so there are no jitted method names to resolve
against. Fix the capture, not the analysis:

```csharp
AllocationTracing.CreateOptions(duration, path, resolveCallStacks: true)   // turns rundown on
// or explicitly
new TraceCollectionOptions { …, Rundown = true }
```

Asking a rundown-less trace for stacks does not error — it gives unnamed frames.

## Numbers look too low / percentages do not add up

**Events were dropped.** The runtime's circular buffer wrapped before the collector drained
it, and the capture still reports success.

- Raise `CircularBufferSizeInMB` (per trace, or on the introspector).
- Raise `StreamCopyBufferSizeInBytes` if the output is a slow disk.
- Narrow the provider set — fewer keywords and a lower level beat a bigger buffer.
- `RequestStackwalk = false` (needs .NET 9+ on the target) removes the dominant per-event cost.

See `large-processes.md`.

## The process is not in `GetTraceableProcesses()`

- Not a .NET (Core) process, or .NET Framework.
- Running as a different user — the IPC endpoint is owned by the target's user. Run as the
  same user, or elevated.
- Diagnostics disabled on the target (`DOTNET_EnableDiagnostics=0`).
- Different container / PID namespace / `TMPDIR`. Use a diagnostic port
  (`diagnostic-ports.md`) or capture in-process (`self-tracing.md`).
- Just started: the runtime publishes its endpoint a moment after launch. Poll
  `GetTraceableProcesses()` instead of sleeping a fixed interval.

## The capture throws or fails to connect

Check `result.Exception` — it is populated rather than thrown for traces, sampling and heap
dumps.

- `TimeoutException` / `ServerNotAvailable` — wrong pid, the process exited, or no permission.
- `UnsupportedCommandException` — the target runtime is too old for the requested feature
  (e.g. `RequestStackwalk = false` on pre-.NET 9).
- `FormatException` from a `DiagnosticPort` — malformed port string; see
  `diagnostic-ports.md` for the `address[,connect|,listen]` grammar.
- A rundown configuration rejected by the target is retried automatically unless
  `RetryOnUnsupportedConfiguration = false`; `TraceResult.RundownKeyword` shows what it settled
  on.

## The trace never stops

- `Duration` is null or zero and nothing else ends it. Always set a duration as a backstop.
- A stopping event that never fires. Check `StoppingEventPayloadFilterMismatched`, and check
  the stopping-event provider is actually enabled in the capture (`stopping-events.md`).
- Stopping is genuinely slow because of rundown on a large app — that is the stop cost, not a
  hang. `Rundown = false` if you do not need symbols.

## Process suspended at startup never runs

The target has `DOTNET_DefaultDiagnosticPortSuspend=1` and is waiting for a session. Set
`ResumeRuntime = true` on the capture.

## `.gcdump` fails

- `Timeouted` — raise `MemoryIntrospectorOptions.Timeout` (it is clamped to a 30s minimum, and
  a multi-GB heap can need minutes).
- Very large heaps — set `ExpectLargeGraph = true` and raise `MaxNodeCount`.
- `NoHeapFound` — connected but no managed heap: wrong pid, or the process exited mid-walk.

## `DumpAsync` returns -1

It reports failure by return code, not exception. Check the logger output. Usual causes:
insufficient privileges for a cross-user dump, an unwritable `targetPath`, or not enough disk
for a `Heap`/`Full` dump of a large process.

## `ConvertTo` throws "An output path is required"

The trace was buffered in memory, so there is no `.nettrace` path to derive a converted name
from. Pass an explicit `outputPath`, or set `TraceCollectionOptions.OutputPath` at capture
time.

## Conversion warns about a broken trace

"Read past end of stream" means the trace is truncated — the process was killed mid-capture,
or the disk filled. The converter retries in best-effort mode and produces a file with
possibly-broken stacks. The `.nettrace` is still worth opening in PerfView.

## Speedscope / Chromium output is empty

Both formats are built from CPU samples only. Capture with
`TraceProfileKind.DotNetSampledThreadTime`. `EventSource` events are not represented in either
format — open the `.nettrace` in PerfView for those.

## The library's own frames dominate a self-trace

Expected on a near-idle process — see `self-tracing.md` for the measured cost.
`SamplingExcludedModules` already filters this library's frames from *method* reports;
allocation reports have no such filter, so the pump-buffer `System.Byte[]` entries are visible.

## Related

- `large-processes.md` — dropped events and capture cost
- `getting-started.md` — privileges, platforms and requirements
