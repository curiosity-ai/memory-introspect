---
name: getting-started
description: Install Memory.Introspect, create a MemoryIntrospector, and configure it. Covers every MemoryIntrospectorOptions field, target-framework/platform/privilege requirements, and how logging works. Use when setting up the library in a project for the first time.
---

# Getting Started

## Install

```bash
dotnet add package Memory.Introspect
```

The package targets `net6.0`–`net10.0` and works on Windows, Linux and macOS. It brings in
`Microsoft.Diagnostics.Tracing.TraceEvent`, which does the heavy lifting for trace parsing and
format conversion.

## Namespaces

```csharp
using Memory.Introspect;         // MemoryIntrospector, MemoryIntrospectorOptions,
                                 // MemoryGraphResult, Dumper
using Memory.Introspect.Trace;   // everything tracing: TraceCollectionOptions, TraceResult,
                                 // TraceProfileKind, ClrEventKeywords, AllocationTracing, …
using Memory.Introspect.Diagnostics.NETCore.Client;  // EventPipeProvider (only needed when
                                                     // building providers by hand)
```

## Create the introspector

```csharp
var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger  = logger,
    Verbose = true,
});
```

`Create(null)` is valid and gives you the defaults. The returned object holds no session
state — create one per app (or per request) as you prefer; it is not disposable.

## MemoryIntrospectorOptions

| Option | Type | Default | What it does |
| --- | --- | --- | --- |
| `Logger` | `ILogger` | `null` | Where the internal diagnostics protocol narrates itself: handshake, EventPipe session setup, provider table, rundown retries, conversion progress. Null means every internal log line is dropped. |
| `LogLevel` | `LogLevel` | `Information` | The level the internal lines are logged at. Set to `Debug` or `Trace` to keep them out of production logs while still having them. |
| `Verbose` | `bool` | `true` | Extra detail from the `.gcdump` and `.dmp` paths (connection status, graph construction). |
| `Timeout` | `TimeSpan` | 30s | Upper bound on a `.gcdump` collection. **Clamped to a minimum of 30 seconds** by `Create` — a smaller value is silently raised. Does not apply to traces, which are bounded by `Duration`/cancellation instead. |
| `CircularBufferSizeInMB` | `int` | 1024 | The runtime-side in-memory circular buffer for every EventPipe session this introspector starts. Raise it if events are dropped. Overridable per trace via `TraceCollectionOptions.CircularBufferSizeInMB`. |
| `ExpectLargeGraph` | `bool` | `false` | Build the `.gcdump` memory graph in very-large-graph mode. Needed for heaps with more nodes than a 32-bit index can address. |
| `MaxNodeCount` | `int` | 10,000,000 | Cap on nodes captured into a `.gcdump`. |
| `DiagnosticPort` | `string` | `null` | Connect through a diagnostic port instead of a process id — see `diagnostic-ports.md`. |
| `SamplingExcludedModules` | `IReadOnlyList<string>` | `["Memory.Introspect"]` | Assembly simple names hidden from sampling `TopMethods` reports by default. The default keeps this library's own frames out of your report. Empty list disables the filter. |
| `SamplingBlockingMethodPatterns` | `IReadOnlyList<string>` | ~30 regexes | Patterns identifying blocking waits (`Monitor.Wait`, `SemaphoreSlim.Wait`, `Task.Wait`, `LowLevelLifoSemaphore.Wait`, …). Any sample whose stack contains a match is dropped, so parked threads do not masquerade as hot code. Empty list disables the filter. |

## Logging

The library writes through a `TextWriter` adapter over your `ILogger`. Wiring one up is the
difference between "the capture produced 0 bytes" and seeing exactly which provider the
runtime rejected:

```csharp
using var loggerFactory = LoggerFactory.Create(f => f.AddConsole());
var logger = loggerFactory.CreateLogger("Memory.Introspect");

var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger   = logger,
    LogLevel = LogLevel.Debug,   // keep the protocol chatter out of Information
});
```

Report *output* (the `topN` and allocation tables) is separate — those go to a `TextWriter`
you pass explicitly, e.g. `Console.Out`.

## Finding a process to capture

```csharp
// dotnet-trace ps — every .NET process on this machine publishing a diagnostics endpoint
IReadOnlyList<int> pids = MemoryIntrospector.GetTraceableProcesses();

int self = Environment.ProcessId;      // the current process is always traceable
```

A process that does not appear in `GetTraceableProcesses()` either is not a .NET (Core)
process, is running as a different user, or has diagnostics disabled
(`DOTNET_EnableDiagnostics=0`).

## Requirements and limitations

- **Runtime:** .NET 6 or later on the *target* process. `TraceCollectionOptions.RequestStackwalk = false`
  additionally requires .NET 9+ on the target.
- **Privileges:** capturing the **current** process needs nothing special. Capturing another
  process requires the same user (or root/Administrator) — the diagnostics IPC channel is a
  named pipe on Windows and a Unix domain socket in `TMPDIR` elsewhere, and both are owned by
  the target's user.
- **Containers:** the target must share the PID namespace and the `TMPDIR` holding the socket.
  Capturing across containers generally needs a diagnostic port (`diagnostic-ports.md`).
- **Not ported:** `dotnet-trace collect-linux`, which drives Linux `perf_events` through an
  external collector. Its `cpu-sampling` and `thread-time` profiles are therefore absent from
  `ListTraceProfiles()`; use `TraceProfileKind.DotNetSampledThreadTime` instead.

## Related

- `trace-collect.md` — the main tracing entry point
- `diagnostic-ports.md` — connecting without a PID
- `troubleshooting.md` — when a capture comes back empty
