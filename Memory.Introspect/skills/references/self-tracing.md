---
name: self-tracing
description: Use Memory.Introspect to capture traces, allocation reports and heap dumps of the process it is running in — Environment.ProcessId, the observer effect the library has on its own numbers, and the SamplingExcludedModules filter. Use when building self-monitoring or on-demand diagnostics into an application.
---

# Tracing the current process

Every capture works against the current process. Pass `Environment.ProcessId` and the library
connects to its own diagnostics endpoint over the normal IPC channel — nothing special is
required, and standard user privileges are enough.

```csharp
int self = Environment.ProcessId;

var report  = await introspector.CollectAllocationReportAsync(self, TimeSpan.FromSeconds(10));
var sample  = await introspector.CollectSamplingProfileAsync(self, TimeSpan.FromSeconds(10));
var trace   = await introspector.CollectTraceAsync(self, new TraceCollectionOptions { … });
var graph   = await introspector.CollectMemoryGraphAsync(self);
await introspector.DumpAsync(self, "self.dmp", Dumper.CollectionType.Mini);
```

This is the whole point of the library: an application can trigger its own capture when it
notices something — a latency spike, a memory threshold, an operator request — without an
external tool, a sidecar, or shipping `dotnet-trace` to production.

## The observer effect

The tracing machinery allocates a little while it runs — mostly the
`StreamCopyBufferSizeInBytes` pump buffer, which lands on the LOH at its 1 MB default — so it
shows up in its own report.

Measured against an otherwise idle process it came to **~1.7 MiB over 6 seconds**: around
**0.03%** of the same self-trace with a real workload running. It is noise for any real
investigation, but on a quiet process it can be the top entry, which is confusing if you are
not expecting it.

To keep the library's own frames out of *method* reports, `SamplingExcludedModules` defaults
to `["Memory.Introspect"]` and does exactly that. Allocation reports have no equivalent
filter — the `System.Byte[]` entries from the pump buffer are simply visible; compare against
the LOH column and the sample counts before drawing conclusions from a near-idle capture.

## Keeping the cost predictable

```csharp
var trace = await introspector.CollectTraceAsync(Environment.ProcessId, new TraceCollectionOptions
{
    Duration                    = TimeSpan.FromSeconds(15),
    Profiles                    = TraceProfileKind.GcCollect,   // cheapest useful profile
    OutputPath                  = path,                          // don't hold it in your own heap
    Rundown                     = false,                         // faster stop, smaller file
    StreamCopyBufferSizeInBytes = 256 * 1024,                    // smaller LOH footprint
});
```

Buffering a self-trace in memory means the trace bytes are on your own heap — which is
counterproductive when the thing you are investigating is memory pressure. For self-tracing,
prefer `OutputPath` almost always.

## Serialise your captures

Starting several EventPipe sessions against yourself at once multiplies the cost and can
starve the very workload you are measuring. Gate them:

```csharp
private static readonly SemaphoreSlim _captureGate = new(1, 1);

public static async Task<string> CaptureAsync(TimeSpan duration, CancellationToken ct)
{
    if (!await _captureGate.WaitAsync(TimeSpan.Zero, ct))
        throw new InvalidOperationException("a capture is already running");

    try
    {
        string path = Path.Combine(Path.GetTempPath(), $"self-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.nettrace");
        var trace = await introspector.CollectTraceAsync(Environment.ProcessId,
            new TraceCollectionOptions { Duration = duration, OutputPath = path }, ct);
        return trace.Success ? trace.TraceFilePath : null;
    }
    finally { _captureGate.Release(); }
}
```

## Self-capture of a `.gcdump`

A heap dump of the current process forces a GC and walks the whole heap, which pauses the
process for the duration of the walk — seconds on a multi-GB heap. That is acceptable on
demand, never on a timer in a latency-sensitive service. See `gc-dump.md`.

## Related

- `diagnostics-endpoint.md` — exposing self-capture over HTTP
- `large-processes.md` — the knobs that control what a capture costs
- `allocation-tracing.md` — where the self-allocation numbers come from
