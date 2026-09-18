---
name: allocation-tracing
description: Report which types a .NET process allocated and how many bytes went to each, using Memory.Introspect's CollectAllocationReportAsync, AllocationReport, AllocatedType and AllocationTracing helpers. Covers the required CLR keywords, the SOH/LOH split and why ObjectCount is usually zero. Use when hunting allocation churn or GC pressure.
---

# Allocation tracing (which types are being allocated)

`CollectAllocationReportAsync` traces the CLR's allocation sampling events and tells you what
a process allocated over an interval, by type.

## Signature

```csharp
Task<AllocationReport> CollectAllocationReportAsync(
    int processId, TimeSpan duration, int count = 10,
    string outputPath = null, CancellationToken ct = default);
```

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

`duration` must be positive. If the capture itself threw, the exception is rethrown; if it
simply produced nothing, you get an empty report rather than an error.

## AllocationReport

```csharp
IReadOnlyList<AllocatedType> Types;          // ordered by allocated bytes, descending
long   TotalAllocatedBytes;                  // across all types, including ones trimmed by count
int    DistinctTypeCount;                    // before the top-N trim
long   SampleCount;                          // allocation events the report is based on
AllocationSampleSource Source;               // None | AllocationTick | SampledObjectAllocation
IReadOnlyList<AllocationCallStack> CallStacks;   // empty unless stacks were requested
bool   HasCallStacks;
bool   IsEmpty;                              // Source == None, or no types
```

## AllocatedType

```csharp
string TypeName;                 // "System.Byte[]"
long   AllocatedBytes;
double AllocatedBytesPercent;
long   SampleCount;              // allocation events attributed to this type
long   ObjectCount;              // 0 unless Source == SampledObjectAllocation
long   SmallObjectHeapBytes;
long   LargeObjectHeapBytes;     // > 0 means objects past the 85,000-byte LOH threshold
```

A type with bytes in the LOH column is worth a second look: LOH allocations are not compacted
by default and drive gen-2 collections.

```csharp
foreach (var t in report.Types.Where(t => t.LargeObjectHeapBytes > 0))
    Console.WriteLine($"{t.TypeName}: {t.LargeObjectHeapBytes:N0} LOH bytes");
```

## What the numbers actually are

- They come from **`GCAllocationTick`**, which the runtime emits once per ~100 KB allocated,
  naming the type of the object that crossed the threshold. That makes **allocated bytes per
  type accurate in aggregate**, but there are no per-object counts — `ObjectCount` stays `0`
  unless the runtime emitted per-object `GCSampledObjectAllocation` events instead
  (`Source == AllocationSampleSource.SampledObjectAllocation`).
- A type allocating many small objects and a type allocating few large ones look the same in
  the bytes column; that is usually the right question anyway.
- `AllocationSampleSource.None` means the trace had no allocation events at all.

## Keeping the trace, or driving the capture yourself

```csharp
// Keep the underlying .nettrace as well as the report
var report = await introspector.CollectAllocationReportAsync(
    pid, TimeSpan.FromSeconds(10), count: 10, outputPath: "allocations.nettrace");

// Or capture yourself and report afterwards
var trace  = await introspector.CollectTraceAsync(pid,
    AllocationTracing.CreateOptions(TimeSpan.FromSeconds(10), "allocations.nettrace"));
AllocationReport r = trace.TopAllocatedTypes(count: 20);
trace.WriteAllocationReport(Console.Out, count: 20);

// Or analyse a .nettrace captured earlier
var offline = introspector.ReportTopAllocatedTypes("allocations.nettrace", count: 20);
```

## What the capture must enable

```csharp
public const ClrEventKeywords RequiredClrEvents =
    ClrEventKeywords.Gc | ClrEventKeywords.Type | ClrEventKeywords.GcHeapAndTypeNames;

public const EventLevel RequiredClrEventLevel = EventLevel.Verbose;
```

`Gc` produces the allocation events; `Type` and `GcHeapAndTypeNames` supply the bookkeeping
that turns type ids into names. **The Verbose level is not optional** — at `Informational` the
runtime emits GC collection events but no allocation ticks.

`AllocationTracing.CreateOptions(TimeSpan duration, string outputPath = null, bool resolveCallStacks = false)`
builds exactly that configuration, and leaves rundown off (the per-type report resolves type
names from the events themselves and does not need jitted method symbols).

Running `TopAllocatedTypes` over a trace captured without those keywords returns
`IsEmpty == true` rather than throwing — check it:

```csharp
if (report.IsEmpty)
{
    logger.LogWarning("no allocation events: capture with AllocationTracing.CreateOptions, " +
                      "or set ClrEvents = AllocationTracing.RequiredClrEvents at Verbose");
}
```

## Cost

Allocation tracing is verbose. An allocation-heavy process can produce **tens of MB of events
per second**. So:

- prefer short intervals (5–15 seconds is usually plenty — the numbers are rates, not totals);
- stream to a file via `outputPath` rather than buffering, for anything but a quick look;
- raise `MemoryIntrospectorOptions.CircularBufferSizeInMB` (or the per-trace override) if
  events are being dropped.

## Printing

```csharp
AllocationTracing.Write(TextWriter output, AllocationReport report, bool verbose = false);
```

`verbose: true` prints full type names instead of truncating them. `trace.WriteAllocationReport(output, count, verbose)`
is the same thing straight off a `TraceResult`.

## Related

- `allocation-call-stacks.md` — *where* the allocations came from
- `gc-dump.md` — what is still alive, as opposed to what was allocated
- `troubleshooting.md` — empty reports and dropped events
