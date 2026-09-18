---
name: diagnostics-endpoint
description: Expose Memory.Introspect captures from a running service — an ASP.NET Core diagnostics endpoint that streams a .nettrace, allocation report or .gcdump on demand, with concurrency gating, authorization, size limits and threshold-triggered background capture. Use when adding on-demand profiling to a deployed application.
---

# Exposing captures from a running service

The pattern this library exists for: a service that can profile itself on request, with no
tooling deployed alongside it.

## A minimal ASP.NET Core endpoint

```csharp
using Memory.Introspect;
using Memory.Introspect.Trace;

public sealed class DiagnosticsService
{
    private static readonly SemaphoreSlim _gate = new(1, 1);
    private readonly MemoryIntrospector _introspector;
    private readonly ILogger<DiagnosticsService> _logger;

    public DiagnosticsService(ILogger<DiagnosticsService> logger)
    {
        _logger = logger;
        _introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
        {
            Logger   = logger,
            LogLevel = LogLevel.Debug,      // keep protocol chatter out of Information
        });
    }

    public async Task<string> CaptureTraceAsync(TimeSpan duration, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct))
            throw new InvalidOperationException("A capture is already in progress.");

        try
        {
            string path = Path.Combine(Path.GetTempPath(),
                $"trace-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.nettrace");

            var trace = await _introspector.CollectTraceAsync(Environment.ProcessId,
                new TraceCollectionOptions
                {
                    Duration   = duration,
                    Profiles   = TraceProfileKind.Default,
                    OutputPath = path,
                    Rundown    = true,           // we want symbolised stacks
                }, ct);

            if (!trace.Success)
                throw new InvalidOperationException("Capture failed.", trace.Exception);

            return trace.TraceFilePath;
        }
        finally { _gate.Release(); }
    }
}
```

```csharp
app.MapGet("/diagnostics/trace", async (
        DiagnosticsService diagnostics, int? seconds, CancellationToken ct) =>
    {
        var duration = TimeSpan.FromSeconds(Math.Clamp(seconds ?? 30, 1, 120));
        string path  = await diagnostics.CaptureTraceAsync(duration, ct);

        return Results.File(path, "application/octet-stream",
            fileDownloadName: Path.GetFileName(path));
    })
    .RequireAuthorization("Diagnostics");
```

## Non-negotiables

- **Authorize it.** A trace contains method names, SQL text (with the `database` profile),
  `EventSource` payloads and timing. A `.gcdump` or `.dmp` contains live memory. Never expose
  any of these anonymously.
- **Gate concurrency.** One capture at a time, per the `SemaphoreSlim` above. Concurrent
  EventPipe sessions multiply the cost inside the very process you are trying to keep healthy.
- **Bound the duration.** Clamp caller-supplied values. An unbounded `?seconds=` is a
  denial-of-service knob.
- **Stream to disk, not to memory.** `OutputPath` keeps the trace off your own heap.
- **Clean up.** Delete the file after it is served, or age files out of the directory; capture
  files are large and accumulate.
- **Watch disk space.** Check free space before starting, and fail with a clear error instead
  of filling the volume.

## A cheaper "what is happening right now" endpoint

Returning a rendered report instead of a file avoids the download entirely, and is often all
an operator needs:

```csharp
app.MapGet("/diagnostics/hot-methods", async (int? seconds, CancellationToken ct) =>
{
    var sample = await introspector.CollectSamplingProfileAsync(
        Environment.ProcessId, TimeSpan.FromSeconds(Math.Clamp(seconds ?? 10, 1, 60)), ct);

    if (!sample.Success) return Results.Problem("sampling failed");

    var sw = new StringWriter();
    TraceReport.WriteTopMethodsReport(sw, sample.TopMethods(count: 20));
    return Results.Text(sw.ToString(), "text/plain");
}).RequireAuthorization("Diagnostics");

app.MapGet("/diagnostics/allocations", async (int? seconds, CancellationToken ct) =>
{
    var report = await introspector.CollectAllocationReportAsync(
        Environment.ProcessId, TimeSpan.FromSeconds(Math.Clamp(seconds ?? 10, 1, 30)),
        count: 20, outputPath: null, ct);

    var sw = new StringWriter();
    AllocationTracing.Write(sw, report);
    return Results.Text(sw.ToString(), "text/plain");
}).RequireAuthorization("Diagnostics");
```

## Threshold-triggered background capture

Capture automatically when the process misbehaves, rather than waiting for someone to ask:

```csharp
public sealed class MemoryWatchdog : BackgroundService
{
    private readonly MemoryIntrospector _introspector;
    private readonly ILogger<MemoryWatchdog> _logger;
    private DateTimeOffset _lastCapture = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);

            long bytes = GC.GetTotalMemory(forceFullCollection: false);
            if (bytes < 4L * 1024 * 1024 * 1024) continue;

            // Rate-limit: a heap dump is expensive and the condition will persist
            if (DateTimeOffset.UtcNow - _lastCapture < TimeSpan.FromHours(1)) continue;
            _lastCapture = DateTimeOffset.UtcNow;

            _logger.LogWarning("Heap at {0:N0} bytes — capturing a gcdump", bytes);

            var graph = await _introspector.CollectMemoryGraphAsync(Environment.ProcessId, ct);
            if (graph.Success)
                graph.SaveToDisk($"/var/diagnostics/heap-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.gcdump");
            else
                _logger.LogError(graph.Exception, "gcdump failed (timeout={0})", graph.Timeouted);
        }
    }
}
```

Rate-limiting matters more than the threshold: the condition that triggers a capture usually
persists, and an unthrottled watchdog will dump in a loop and take the process down.

For "capture the run-up to a failure" rather than "capture on a threshold", a stopping event
is better — it needs no polling and the trace ends exactly at the event
(`stopping-events.md`).

## Related

- `self-tracing.md` — the cost model for capturing yourself
- `large-processes.md` — keeping a production capture affordable
- `offline-reports.md` — doing the expensive analysis off-box
