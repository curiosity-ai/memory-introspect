---
name: large-processes
description: Keep Memory.Introspect captures affordable and lossless on big or chatty .NET processes — CircularBufferSizeInMB, StreamCopyBufferSizeInBytes, Rundown, RundownKeyword, RequestStackwalk and RetryOnUnsupportedConfiguration. Use when a trace drops events, takes too long to stop, or produces an unmanageable file.
---

# Tracing large or chatty processes

Four things determine what a trace costs and whether it loses data. All are exposed.

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

## CircularBufferSizeInMB — dropped events

The runtime writes events into a circular buffer inside the **target** process; the collector
drains it. If the target produces events faster than they are drained, the buffer wraps and
events are **silently lost** — the capture still reports success.

- Default: 1024 MB, from `MemoryIntrospectorOptions.CircularBufferSizeInMB`, overridable per
  trace. `TraceResult.CircularBufferSizeInMB` reports what was used.
- Raise it for allocation tracing, verbose CLR events, or anything on a many-core box.
- It is memory *in the traced process*. On a memory-constrained host, reducing the event rate
  (fewer keywords, lower level, `RequestStackwalk = false`) beats raising the buffer.

Symptoms of drops: sample counts far lower than the workload implies, percentages that do not
add up, or gaps in the timeline in PerfView.

## StreamCopyBufferSizeInBytes — the client-side pump

The buffer used to copy the EventPipe stream to your output. Default 1 MB. Larger buffers
reduce syscall overhead when events arrive faster than they can be written.

Note the 1 MB default allocates on the LOH — visible in self-traces (`self-tracing.md`). Lower
it when tracing yourself under memory pressure, raise it when writing a fast stream to a slow
disk.

## Rundown — the stop cost

Rundown is a burst of events at the **end** of the session that names every jitted method, so
stacks can be symbolised. It is why a trace can take many seconds to stop, and a large part of
why files get big.

```csharp
Rundown = false,           // fastest stop, smallest file, unnamed jitted frames
Rundown = true,            // force it on regardless of what the profiles ask for
Rundown = null,            // (default) whatever the selected profiles want
RundownKeyword = 0x1,      // an explicit keyword, overriding both
```

Turn it off when you do not need method names: GC-behaviour captures, event-timeline captures,
and any capture you will analyse by event rather than by stack. Keep it on for CPU profiles
and allocation call stacks — without it, frames resolve to `?`.

`TraceResult.RundownKeyword` reports what the session actually ran with (`0` = off).

## RequestStackwalk — the per-event cost

By default a stack is captured for **every** event. On a chatty provider set that is the
dominant cost inside the target.

```csharp
RequestStackwalk = false,   // much cheaper; requires .NET 9+ on the target
```

Turn it off for event-count or event-timeline analysis. Leave it on when you need to know
where events came from. On a pre-.NET 9 target the option is not supported.

## RetryOnUnsupportedConfiguration

Older runtimes reject some rundown configurations. By default (`true`) the collector retries
with a progressively simpler configuration, mirroring the CLI tool — which is why a
`gc-collect` capture can come back with rundown keyword `0x1`, the default keyword, or `0`
depending on the target. Set to `false` to fail loudly instead.

## A checklist for a big process

1. **Always** set `OutputPath`. Never buffer a large trace in memory.
2. Narrow the providers first — a keyword you do not need costs more than any buffer setting.
3. `Rundown = false` unless you need symbolised stacks.
4. `RequestStackwalk = false` if the target is .NET 9+ and you are counting events, not
   attributing them.
5. Only then raise `CircularBufferSizeInMB`.
6. Prefer several short captures to one long one; analysis cost grows worse than linearly.
7. Analyse off-box (`offline-reports.md`) — conversion and stack resolution are the expensive
   half and need not run on the traced host.

## Related

- `trace-collect.md` — where all these options live
- `troubleshooting.md` — telling a dropped-event problem from a wrong-provider problem
