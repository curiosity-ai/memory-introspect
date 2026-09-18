---
name: gc-dump
description: Capture .gcdump GC heap graphs with Memory.Introspect's CollectMemoryGraphAsync — MemoryGraphResult, SaveToDisk, ExpectLargeGraph and MaxNodeCount, the pause it causes, and how to use two dumps to find a leak. Use when investigating what is alive on the managed heap and what is keeping it alive.
---

# GC heap dumps (`.gcdump`)

A `.gcdump` is a snapshot of the **live** managed heap: every reachable object, its type, its
size, and the references between them. It answers "what is alive and who is holding it" —
which is the leak question. Allocation tracing answers the different question of what was
*allocated* (`allocation-tracing.md`).

## Signature

```csharp
Task<MemoryGraphResult> CollectMemoryGraphAsync(int processId, CancellationToken ct = default);
```

```csharp
using Memory.Introspect;
using Microsoft.Extensions.Logging;

int pid = Environment.ProcessId;

var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger  = logger,
    Verbose = true,
});

var result = await introspector.CollectMemoryGraphAsync(pid);

if (result.Success)
{
    var file = $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{pid}.gcdump";
    logger.LogInformation("Writing .gcdump file to {0}", file);
    result.SaveToDisk(file);
}
```

## MemoryGraphResult

```csharp
bool        Success;
bool        Timeouted;      // the collection exceeded MemoryIntrospectorOptions.Timeout
bool        Cancelled;
bool        NoHeapFound;    // connected, but the target reported no managed heap
Exception   Exception;
MemoryGraph Graph;          // the graph itself (Graphs.MemoryGraph, from TraceEvent)
void        SaveToDisk(string fileName);
```

The four failure flags are worth distinguishing — they point at different problems:

```csharp
if (!result.Success)
{
    if (result.Timeouted)   logger.LogError("heap walk exceeded the timeout — raise Timeout");
    else if (result.Cancelled)   logger.LogWarning("cancelled");
    else if (result.NoHeapFound) logger.LogError("no managed heap — wrong pid, or the process exited");
    else logger.LogError(result.Exception, "gcdump failed");
}
```

## Relevant options

| Option | Default | Why you would change it |
| --- | --- | --- |
| `Timeout` | 30s (minimum, enforced by `Create`) | A multi-GB heap can take minutes to walk. Raise it — a timeout means no dump at all. |
| `CircularBufferSizeInMB` | 1024 | The heap-dump events go through the same EventPipe buffer. Raise it for very large heaps. |
| `ExpectLargeGraph` | `false` | Set for heaps with more nodes than a 32-bit index can address. The collection fails without it on very large heaps. |
| `MaxNodeCount` | 10,000,000 | Caps how much of the heap is captured. Raise for completeness, lower to bound the cost. |
| `Verbose` | `true` | Logs connection status and graph construction — useful, since this is the slowest capture. |

```csharp
var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger                 = logger,
    Timeout                = TimeSpan.FromMinutes(5),
    ExpectLargeGraph       = true,
    MaxNodeCount           = 50_000_000,
    CircularBufferSizeInMB = 2048,
});
```

## Cost — read this before putting it in a service

Collecting a `.gcdump` **forces a GC and walks the entire live heap**. The target process is
effectively paused for the duration of the walk: seconds on a multi-GB heap, longer under
memory pressure. The resulting file is roughly proportional to the number of live objects.

So: on demand, yes. On a timer in a latency-sensitive service, no. If you need continuous
memory insight at low cost, trace with `TraceProfileKind.GcCollect` and dump only when a
threshold is crossed.

## Finding a leak with two dumps

One dump tells you what is big. Two dumps tell you what is *growing*, which is the actual
leak signal:

```csharp
var baseline = await introspector.CollectMemoryGraphAsync(pid);
baseline.SaveToDisk("baseline.gcdump");

await RunSuspectWorkloadAsync();      // and let a gen-2 GC happen
GC.Collect(2, GCCollectionMode.Forced, blocking: true);   // only when tracing yourself

var after = await introspector.CollectMemoryGraphAsync(pid);
after.SaveToDisk("after.gcdump");
```

Open both in PerfView and use **Diff** to get per-type deltas. A type whose count grows across
dumps taken at the same logical point in a workload is the leak; the reference path in the
viewer tells you who is holding it.

## Analysing

Open the `.gcdump` in **PerfView** (best reference-path tooling) or **Visual Studio**
(Debug → Memory Usage → open the file). The `Graph` property is a `Graphs.MemoryGraph` from
TraceEvent if you want to walk it programmatically — `SaveToDisk` writes the same graph in the
standard format via `GCHeapDump.WriteMemoryGraph`.

## Related

- `allocation-tracing.md` — what was allocated vs what is alive
- `process-dump.md` — a full debugger-grade snapshot instead
- `self-tracing.md` — dumping the current process
