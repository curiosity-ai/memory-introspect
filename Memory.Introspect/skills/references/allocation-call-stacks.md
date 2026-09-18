---
name: allocation-call-stacks
description: Resolve the call stacks behind allocations with Memory.Introspect — the resolveCallStacks overload of CollectAllocationReportAsync, AllocationCallStack, WriteCallStacks, and the rundown/ETLX costs that make it opt-in. Use when a per-type allocation report has identified the type but not the code allocating it.
---

# Allocation call stacks (where the bytes came from)

A per-type report says `System.Byte[]` is 99% of your allocations. That is rarely actionable
on its own. EventPipe already records a call stack for every allocation event; what it cannot
do without **rundown** is give you the method names to resolve those stacks against. Asking
for call stacks turns rundown on.

## Signature

```csharp
Task<AllocationReport> CollectAllocationReportAsync(
    int processId, TimeSpan duration, int count, string outputPath,
    bool resolveCallStacks, CancellationToken ct = default);
```

Note this is the five-argument overload — `count` and `outputPath` are no longer optional once
you pass `resolveCallStacks`.

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

## AllocationCallStack

```csharp
long   AllocatedBytes;
double AllocatedBytesPercent;
long   SampleCount;
string TypeName;                      // the type contributing the most bytes through this stack
IReadOnlyList<string> Frames;         // allocating method first, walking out to the thread root
```

Stacks are aggregated by their **full frame list**, so two different paths into the same
allocating method stay separate — which is the whole point: it tells you which caller is
responsible.

A single stack can allocate more than one type (a helper that builds both a buffer and a
string); `TypeName` names the larger contributor.

```csharp
foreach (var s in report.CallStacks)
{
    Console.WriteLine($"{s.AllocatedBytesPercent,6:0.00}%  {s.TypeName}");
    foreach (var f in s.Frames.Take(5)) Console.WriteLine($"        {f}");
}
```

## Printing

```csharp
AllocationTracing.WriteCallStacks(TextWriter output, AllocationReport report, int maxFrames = 12);
```

`report.HasCallStacks` tells you whether a given report carries them at all.

## The same flag on the lower-level entry points

```csharp
// Drive the capture yourself
var trace  = await introspector.CollectTraceAsync(pid,
    AllocationTracing.CreateOptions(TimeSpan.FromSeconds(10), "alloc.nettrace", resolveCallStacks: true));
var report = trace.TopAllocatedTypes(count: 10, resolveCallStacks: true);

// Or analyse a file captured earlier
var offline = introspector.ReportTopAllocatedTypes("alloc.nettrace", count: 10, resolveCallStacks: true);
```

The `resolveCallStacks` flag appears in two distinct places and both have to be set:

| Where | What it does |
| --- | --- |
| `AllocationTracing.CreateOptions(..., resolveCallStacks: true)` (capture) | Turns **rundown** on, so jitted method names are recorded |
| `TopAllocatedTypes(..., resolveCallStacks: true)` / `ReportTopAllocatedTypes(...)` (analysis) | Runs the second, stack-resolving pass |

Asking a rundown-less trace for stacks does not error — it yields unnamed `?` frames. If your
output is full of `?`, the capture, not the analysis, is what needs fixing.

## Why it is opt-in

It costs more at both ends:

- **Capture** — rundown makes stopping the session noticeably slower and the trace larger, on
  a big app substantially so.
- **Analysis** — the per-type tally streams the trace and is stack-blind; resolving stacks
  needs `TraceLog`, which converts the trace to ETLX first. That is slow on a large capture
  and needs temp disk space.

So: prefer short durations, and reach for stacks only once the per-type report has told you
which type to chase.

## Opting back out

The per-type totals are identical with and without stack resolution, so a single capture made
with `resolveCallStacks: true` can be reported both ways:

```csharp
var withStacks = trace.TopAllocatedTypes(count: 10, resolveCallStacks: true);
var fastPath   = trace.TopAllocatedTypes(count: 10);   // same Types, no CallStacks, much quicker
```

## Related

- `allocation-tracing.md` — the per-type report and its required keywords
- `large-processes.md` — what rundown costs, and when to turn it off
