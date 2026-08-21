# Memory Introspect

**Programmatic `.gcdump`, `.dmp` and `.nettrace` capture for .NET applications.**

[![Memory.Introspect](https://img.shields.io/nuget/v/Memory.Introspect.svg?style=flat-square)](https://www.nuget.org/packages/Memory.Introspect/)

`Memory.Introspect` is a lightweight C\# library that wraps the functionality of the official `dotnet-gcdump`, `dotnet-dump` and `dotnet-trace` tools. It allows developers to capture garbage collection (GC) dumps, process dumps and EventPipe traces directly from within their code, without needing to shell out to a CLI or manage external processes.

## 🚀 Why use this?

Normally, capturing a `.gcdump` requires running the `dotnet-gcdump` command-line tool against a Process ID (PID). While effective for ad-hoc debugging, it is difficult to automate within an application.

**Memory.Introspect allows you to:**

  * **Self-Monitor:** Have an application trigger its own memory dump to analyze memory leaks.
  * **Automate:** Integrate memory capturing into integration tests or CI/CD pipelines.
  * **Streamline:** Avoid parsing CLI text output; work with strong types and direct boolean results.

-----

## 📦 Installation

[![Memory.Introspect](https://img.shields.io/nuget/v/Memory.Introspect.svg?style=flat-square)](https://www.nuget.org/packages/Memory.Introspect/)

Memory.Introspect is available as a [NuGet package](https://www.nuget.org/packages/Memory.Introspect/)

```bash
dotnet add package Memory.Introspect
```

-----

## 💻 Usage

The library exposes a simple `CollectMemoryGraphAsync` method that connects to the target process via the .NET Diagnostics Client (EventPipe).

### Basic Example

Here is how to capture the current process's memory graph and save it to a temporary file:

```csharp
using System.Diagnostics;
using Memory.Introspect;
using Microsoft.Extensions.Logging;

int currentPid    = Process.GetCurrentProcess().Id;

var loggerFactory = LoggerFactory.Create(f => f.AddConsole());
var logger = loggerFactory.CreateLogger("Memory.Introspect");

logger.LogInformation("Starting creating gcdump file from process {0}", currentPid);

var result = await Memory.Introspect.Create(new() { Logger = logger, Verbose = true }).CollectMemoryGraphAsync(currentPid);

if (result.Success)
{
    var gcDumpFile =  $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{currentPid}.gcdump";
    logger.LogInformation("Writing .gcdump file to {0}", gcDumpFile);
    result.SaveToDisk(gcDumpFile);
}

logger.LogInformation("Finished creating gcdump file");
```

### Collecting a trace (`dotnet-trace` equivalent)

`CollectTraceAsync` is the programmatic equivalent of `dotnet-trace collect`. Streaming
straight to a file is what you want for anything non-trivial — the trace never has to fit in
memory:

```csharp
var introspector = MemoryIntrospector.Create(new() { Logger = logger });

var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(30),
    OutputPath = "app.nettrace",
    Progress   = new Progress<TraceProgress>(p => logger.LogInformation("{0}", p)),
});

// dotnet-trace report topN
foreach (var m in trace.TopMethods(count: 10))
{
    logger.LogInformation("{0,6:0.00}%  {1}", m.ExclusiveMetricPercent, m.Name);
}
```

Omit `OutputPath` to buffer the trace in memory instead and read it back from
`trace.NetTraceData`.

#### Providers, profiles and CLR event keywords

```csharp
// dotnet-trace list-profiles
foreach (var profile in MemoryIntrospector.ListTraceProfiles())
{
    Console.WriteLine($"{profile.Name}: {profile.Description}");
}

var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration = TimeSpan.FromSeconds(20),

    // --profile gc-collect (combine several with |)
    Profiles = TraceProfileKind.GcCollect,

    // --providers "MyCompany-MyApp:0xF:5"
    Providers = new[] { "MyCompany-MyApp:0xF:5" },

    // --clrevents gc+exception --clreventlevel verbose
    ClrEvents     = ClrEventKeywords.Gc | ClrEventKeywords.Exception,
    ClrEventLevel = EventLevel.Verbose,

    OutputPath = "app.nettrace",
});
```

Profiles and CLR event keywords are strongly typed: `TraceProfileKind` and `ClrEventKeywords`
are `[Flags]` enums, and `ClrEventLevel` is `System.Diagnostics.Tracing.EventLevel`. When the
values come from configuration or a command line instead of from code,
`ProviderUtils.ParseClrEvents("gc+exception")`, `ProviderUtils.ParseEventLevel("verbose")` and
`TraceProfiles.Find("gc-collect")` map the CLI spellings onto them.

When no profile, provider or CLR event is configured at all, the same defaults as the CLI tool
are used: `TraceProfileKind.Default` (`dotnet-common` + `dotnet-sampled-thread-time`).

#### Stopping on an event instead of a timer

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration                   = TimeSpan.FromMinutes(5),   // upper bound
    Providers                  = new[] { "MyCompany-MyApp:0x0:4" },
    StoppingEventProviderName  = "MyCompany-MyApp",
    StoppingEventEventName     = "RequestFailed",
    StoppingEventPayloadFilter = new Dictionary<string, string> { ["statusCode"] = "500" },
    OutputPath                 = "failure.nettrace",
});

if (trace.StoppedByStoppingEvent) { /* the trace was cut short by the event */ }
```

#### Other formats and offline reports

```csharp
// dotnet-trace collect --format speedscope
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(20),
    OutputPath = "app.nettrace",
    Format     = TraceFileFormat.Speedscope,   // trace.ConvertedFilePath
});

// dotnet-trace convert --format chromium
string chromium = introspector.ConvertTraceFile("app.nettrace", TraceFileFormat.Chromium);

// dotnet-trace report topN, on any .nettrace file
var top = introspector.ReportTopMethods("app.nettrace", count: 10);

// dotnet-trace ps
IReadOnlyList<int> pids = MemoryIntrospector.GetTraceableProcesses();
```

### Which objects are being allocated?

`CollectAllocationReportAsync` traces the CLR's allocation sampling events and reports what a
process allocated over the interval, by type:

```csharp
var report = await introspector.CollectAllocationReportAsync(pid, TimeSpan.FromSeconds(10), count: 10);

AllocationTracing.Write(Console.Out, report);
```

```
Top 3 Allocated Types of 3 (16.66 GiB total, 167,438 AllocationTick events)
Type                                                        Bytes        %          LOH    Objects
1. System.Byte[]                                         16.65 GiB   99.99%           -          -
2. System.InvalidOperationException                       1.73 MiB    0.01%           -          -
3. System.GCMemoryInfoData                              104.29 KiB       0%           -          -
```

Each `AllocatedType` carries `AllocatedBytes`, `AllocatedBytesPercent`, `SampleCount` and the
`SmallObjectHeapBytes` / `LargeObjectHeapBytes` split — a type showing bytes in the LOH column
is allocating objects past the 85,000 byte threshold.

To keep the underlying `.nettrace`, or to run the report over a file you already have:

```csharp
// keep the trace as well as the report
var report = await introspector.CollectAllocationReportAsync(pid, TimeSpan.FromSeconds(10),
                                                             outputPath: "allocations.nettrace");

// or drive the capture yourself and report afterwards
var trace  = await introspector.CollectTraceAsync(pid, AllocationTracing.CreateOptions(TimeSpan.FromSeconds(10), "allocations.nettrace"));
var report = trace.TopAllocatedTypes(count: 20);
trace.WriteAllocationReport(Console.Out, count: 20);

// or analyse a .nettrace captured earlier
var offline = introspector.ReportTopAllocatedTypes("allocations.nettrace", count: 20);
```

A few things worth knowing:

  * The numbers come from `GCAllocationTick`, which the runtime emits once per ~100 KB
    allocated. That makes allocated **bytes per type** accurate, but there are no per-object
    counts — `ObjectCount` stays 0 unless the runtime emitted the per-object
    `GCSampledObjectAllocation` events instead.
  * Allocation tracing is verbose. An allocation-heavy process can produce tens of MB of events
    per second, so prefer short intervals and raise `CircularBufferSizeInMB` if events drop.
  * A trace captured without `AllocationTracing.RequiredClrEvents` reports
    `AllocationReport.IsEmpty` rather than throwing.

### Where did the allocations come from?

EventPipe already records a call stack for every allocation event. What it cannot do without
rundown is give you the method names to resolve those stacks against — so asking for call
stacks turns rundown on:

```csharp
var report = await introspector.CollectAllocationReportAsync(
    pid, TimeSpan.FromSeconds(10), count: 10, outputPath: null, resolveCallStacks: true);

AllocationTracing.WriteCallStacks(Console.Out, report);
```

```
Top 3 Allocating Call Stacks

1. 44.35 GiB (100%)  System.Byte[]
      Workload.AllocateGarbage(CancellationToken)
   <- Workload+<>c__DisplayClass0_0.<RunAsync>b__3()
   <- ExecutionContext.RunFromThreadPoolDispatchLoop(...)
   <- Task.ExecuteWithThreadLocal(...)
   <- ThreadPoolWorkQueue.Dispatch()
```

Each `AllocationCallStack` carries `AllocatedBytes`, `AllocatedBytesPercent`, `SampleCount`, the
dominant `TypeName` allocated through it, and `Frames` (allocating method first). Stacks are
aggregated by their full frame list, so two different paths into the same allocating method stay
separate.

The same flag works on the lower-level entry points:

```csharp
// drive the capture yourself
var trace  = await introspector.CollectTraceAsync(pid,
    AllocationTracing.CreateOptions(TimeSpan.FromSeconds(10), "alloc.nettrace", resolveCallStacks: true));
var report = trace.TopAllocatedTypes(count: 10, resolveCallStacks: true);

// or analyse a file captured earlier (its frames only resolve if it was captured with rundown)
var offline = introspector.ReportTopAllocatedTypes("alloc.nettrace", count: 10, resolveCallStacks: true);
```

It costs more at both ends, which is why it is opt-in:

  * **Capture** — rundown makes stopping the session slower and the trace larger.
  * **Analysis** — the per-type tally streams the trace and is stack-blind; resolving stacks
    needs `TraceLog`, which converts the trace to ETLX first. That is slow on a large capture.

Prefer short durations. `AllocationReport.HasCallStacks` tells you whether a given report
carries them; asking a rundown-less trace for stacks yields unnamed `?` frames rather than an
error.

### Profiling a process from inside itself

Every capture works against the current process — pass `Environment.ProcessId` and the library
connects to its own diagnostics endpoint:

```csharp
var report = await introspector.CollectAllocationReportAsync(Environment.ProcessId, TimeSpan.FromSeconds(10));
```

The tracing machinery does allocate a little while it runs (mostly the
`StreamCopyBufferSizeInBytes` pump buffer, which lands on the LOH at its 1 MB default), so it
shows up in its own report. Measured against an otherwise idle process it came to ~1.7 MiB over
6 seconds — around 0.03% of the same self-trace with a real workload running. Use
`SamplingExcludedModules` if you want the library's own frames kept out of method reports.

### Tracing very large processes

Both the runtime-side circular buffer and the client-side stream buffer are exposed, the same
way they are for `.gcdump` capture, so a big or very chatty process does not silently drop
events:

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration = TimeSpan.FromSeconds(60),

    // The runtime's in-memory circular buffer. Raise it when events are being dropped.
    // Defaults to MemoryIntrospectorOptions.CircularBufferSizeInMB (1024 MB).
    CircularBufferSizeInMB = 4096,

    // The buffer used to pump the EventPipe stream out to disk.
    StreamCopyBufferSizeInBytes = 32 * 1024 * 1024,

    // Rundown resolves jitted method names, but on a huge app it dominates both the
    // stop time and the file size — turn it off when you don't need symbolised stacks.
    Rundown = false,

    // Recording a stack for every event is the expensive part of event collection
    // (requires .NET 9+ on the target).
    RequestStackwalk = false,

    OutputPath = "huge-app.nettrace",
});
```

### Analyzing the Output

The resulting `.gcdump` and `.nettrace` files can be opened in:

  * **Visual Studio**
  * **[PerfView](https://github.com/microsoft/perfview)**

`.nettrace` files converted with `TraceFileFormat.Speedscope` open in
[speedscope.app](https://www.speedscope.app/); `TraceFileFormat.Chromium` output opens in
`chrome://tracing` and [Perfetto](https://ui.perfetto.dev/).
-----

## ⚙️ Configuration Options

When initializing the `Memory.Introspect`, you can pass a configuration object:

| Option | Type | Description |
| :--- | :--- | :--- |
| `Logger` | `ILogger` | Used to log the internal diagnostics protocol progress (Handshake, EventPipe setup, etc.). |
| `Verbose` | `bool` | If true, outputs detailed logs regarding the connection status and graph construction. |
| `Timeout` | `TimeSpan` | *(Optional)* Set a maximum duration for the collection process before cancelling. Minimum of 30s.|
| `CircularBufferSizeInMB` | `int` | The runtime's in-memory circular buffer, in MB (default 1024). Used by `.gcdump`, sampling and trace capture, and overridable per trace through `TraceCollectionOptions.CircularBufferSizeInMB`. |
| `DiagnosticPort` | `string` | *(Optional)* Connect through a diagnostic port instead of a process id. |

-----

## ⚠️ Requirements & Limitations

  * **Platform:** Works on Windows, Linux, and macOS.
  * **Privileges:** The process running the code must have sufficient privileges to access the target process via the Diagnostics Client. If capturing the **current** process, standard user privileges are usually sufficient.
  * **Runtime:** Requires .NET 6 or later.

-----

## ⚖️ License & Attribution

This project is licensed under the **MIT License**.

> **Note:** This library is heavily based on the source code of the official diagnostics tools provided by the .NET team.
>
> The core logic for EventPipe communication, graph construction, dump collection and trace
> collection is adapted from:
>
>   * [dotnet-gcdump](https://github.com/dotnet/diagnostics/tree/main/src/Tools/dotnet-gcdump)
>   * [dotnet-dump](https://github.com/dotnet/diagnostics/tree/main/src/Tools/dotnet-dump)
>   * [dotnet-trace](https://github.com/dotnet/diagnostics/tree/main/src/Tools/dotnet-trace)
>
> One `dotnet-trace` capability is intentionally not ported: `collect-linux`, which drives the
> Linux `perf_events` subsystem through an external collector rather than EventPipe, and so
> cannot be done from inside a managed library. Its two profiles (`cpu-sampling`,
> `thread-time`) are therefore absent from `ListTraceProfiles()`.
>
> We are grateful to the .NET Diagnostics team for their open-source contributions.
