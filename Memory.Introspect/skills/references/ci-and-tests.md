---
name: ci-and-tests
description: Use Memory.Introspect in automated tests and CI pipelines — allocation regression tests, capturing artifacts only on failure, tracing a child process under test, and publishing .nettrace/.gcdump files as build artifacts. Use when adding performance or memory regression checks to a test suite or pipeline.
---

# Tests and CI pipelines

The library needs no tooling installed on the agent, which makes it practical to capture
diagnostics from a test run and to assert on what a capture found.

## Allocation regression test

```csharp
[Fact]
public async Task CheckoutDoesNotAllocateLargeBuffers()
{
    var introspector = MemoryIntrospector.Create();

    using var cts = new CancellationTokenSource();
    var workload = Task.Run(() => RunCheckoutLoopAsync(cts.Token));

    var report = await introspector.CollectAllocationReportAsync(
        Environment.ProcessId, TimeSpan.FromSeconds(5), count: 25);

    cts.Cancel();
    try { await workload; } catch (OperationCanceledException) { }

    Assert.False(report.IsEmpty, "no allocation events captured — check the capture configuration");

    long loh = report.Types.Sum(t => t.LargeObjectHeapBytes);
    Assert.True(loh < 10 * 1024 * 1024,
        $"LOH allocations regressed to {loh:N0} bytes:\n{Render(report)}");
}

private static string Render(AllocationReport report)
{
    var sw = new StringWriter();
    AllocationTracing.Write(sw, report, verbose: true);
    return sw.ToString();
}
```

Two things make this kind of test survive contact with CI:

- **Assert on shape, not on exact numbers.** "No type over N MB", "no LOH allocations from
  this type", "this type is not in the top 5" hold across machines; byte counts do not.
- **Always include the rendered report in the failure message.** A red build with a number and
  no context is unactionable.

Be aware that allocation numbers come from ~100 KB sampling (`allocation-tracing.md`), so very
short or very light workloads produce noisy totals. Give the workload enough to do.

## Capture artifacts only on failure

The most useful CI pattern: trace continuously, keep the file only if the test fails.

```csharp
public sealed class TracedTest : IAsyncLifetime
{
    private readonly MemoryIntrospector _introspector = MemoryIntrospector.Create();
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.nettrace");
    private CancellationTokenSource _cts;
    private Task<TraceResult> _capture;

    public Task InitializeAsync()
    {
        _cts = new CancellationTokenSource();
        _capture = _introspector.CollectTraceAsync(Environment.ProcessId, new TraceCollectionOptions
        {
            Duration   = TimeSpan.FromMinutes(10),      // upper bound; cancellation ends it
            Profiles   = TraceProfileKind.Default,
            OutputPath = _path,
        }, _cts.Token);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _cts.Cancel();
        var trace = await _capture;      // cancellation keeps what was captured

        if (TestFailed && trace.Success)
            File.Move(trace.TraceFilePath, Path.Combine(ArtifactDirectory, "failure.nettrace"));
        else
            File.Delete(_path);
    }
}
```

## Tracing a child process under test

To keep the capture's own cost out of the measurement, run the workload in a child process and
trace that:

```csharp
var child = Process.Start(new ProcessStartInfo
{
    FileName        = "dotnet",
    Arguments       = $"{typeof(Workload).Assembly.Location} child",
    UseShellExecute = false,
    CreateNoWindow  = true,
});

try
{
    // Give the runtime a moment to publish its diagnostics endpoint
    await WaitUntilTraceableAsync(child.Id, TimeSpan.FromSeconds(10));

    var trace = await introspector.CollectTraceAsync(child.Id, new TraceCollectionOptions
    {
        Duration   = TimeSpan.FromSeconds(10),
        Profiles   = TraceProfileKind.Default,
        OutputPath = Path.Combine(artifactDir, "child.nettrace"),
    });
}
finally { child.Kill(); }

static async Task WaitUntilTraceableAsync(int pid, TimeSpan timeout)
{
    var deadline = DateTime.UtcNow + timeout;
    while (DateTime.UtcNow < deadline)
    {
        if (MemoryIntrospector.GetTraceableProcesses().Contains(pid)) return;
        await Task.Delay(100);
    }
    throw new TimeoutException($"process {pid} never published a diagnostics endpoint");
}
```

A freshly started process is not immediately traceable — poll `GetTraceableProcesses()` rather
than sleeping a guessed interval. For events from the very first milliseconds, use startup
tracing through a diagnostic port (`diagnostic-ports.md`).

## Publishing artifacts

```yaml
# Azure Pipelines
- task: PublishBuildArtifacts@1
  condition: failed()
  inputs:
    PathtoPublish: '$(Build.ArtifactStagingDirectory)/diagnostics'
    ArtifactName: 'diagnostics'
```

```yaml
# GitHub Actions
- uses: actions/upload-artifact@v4
  if: failure()
  with:
    name: diagnostics
    path: artifacts/diagnostics/
```

Publish the `.nettrace` rather than a converted file: it is smaller, it is the source of
truth, and anyone can convert or report over it afterwards (`offline-reports.md`).

## Agent considerations

- **Privileges** — tracing your own process (or a child you started) needs nothing special.
  Tracing an unrelated process on a hosted agent generally will not work.
- **Containers** — a containerised agent must be able to see the target's diagnostics socket;
  same-container or self-tracing is the reliable case.
- **Disk** — hosted agents have modest disk. Bound durations, prefer `GcCollect` or
  sampling-only profiles over full traces, and delete artifacts on success.
- **Time** — rundown and ETLX conversion are slow. Keep conversion out of the test path;
  convert in a later stage or on a developer machine.

## Related

- `offline-reports.md` — analysing the artifacts after the run
- `allocation-tracing.md` — what the numbers in an allocation assertion mean
- `large-processes.md` — keeping capture cost inside an agent's budget
