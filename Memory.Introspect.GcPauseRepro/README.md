# GC pause repro

Can the EventPipe GC events Memory.Introspect already consumes tell us **why** a process froze
for seconds, cheaply enough to leave on, and capture more detail when it happens? This console
app reproduces long GC pauses and checks the `GcPauseMonitor` against them.

```bash
dotnet run -c Release --project Memory.Introspect.GcPauseRepro -- --duration 60 --monitor detailed
```

| Option | Default | |
| --- | --- | --- |
| `--nodes N` | 5,000,000 | Nodes in the `ConcurrentDictionary<long, Node>` |
| `--links K` | 4 | Random references per node (plus a `Self` reference) |
| `--compact-every S` | 10 | Period of the forced, blocking, compacting `GC.Collect(2)` + LOH compaction |
| `--threshold MS` | 1000 | Long-pause threshold, for the monitor and the heartbeat alike |
| `--monitor` | `detailed` | `none`, `basic` (always-on tier only), `detailed` (escalates on a long pause), `always-detailed` (detailed session for the whole run, to measure its cost) |
| `--gcdump` | off | Add a heap snapshot to the detailed capture |
| `--mutators N` | 2 | Threads rewiring the graph (~30k mutations/s in total) |
| `--duration S` / `--out DIR` / `--quiet` | 75 / temp / off | |

What it does:

- builds 5 M `Node` objects, each pointing at itself and at 4 random others, in a
  `ConcurrentDictionary` (~16 M objects, ~900 MB): a large, highly connected graph with no locality;
- two mutator threads replace nodes' link arrays and labels, so gen2 fragments and old objects
  keep getting pointed at new ones; a third thread churns LOH buffers; 512 small buffers stay pinned;
- a timer calls `GCSettings.LargeObjectHeapCompactionMode = CompactOnce; GC.Collect(2, Forced, blocking: true, compacting: true)`;
- a heartbeat thread sleeps 1 s in a loop and prints how long it actually slept, the way an app
  would notice a stall without tooling;
- the monitor watches the same process (`Environment.ProcessId`), and at the end every heartbeat
  stall is matched against the pauses the monitor reported.

## Results

Measured on a 4-core Linux container, .NET 10.0.12, workstation concurrent GC.

### Detection: every stall is explained

Across 8 monitored runs the heartbeat saw 42 stalls of 2.6–10.7 s. **40 were matched by GC
pauses the monitor reported.** The other 2 (one in each of two runs) were a bug in the repro:
the summary was computed ~20 ms after the last pause ended, before the monitor had read it. The
repro now stops the monitor, which drains the stream, before correlating, and the runs since
then matched every stall. The monitor's total pause time agrees with the runtime's own
`GC.GetTotalPauseDuration()` to within 0.01% (48,723 ms vs 48,722 ms; 38,043 vs 38,042).

A long pause is reported **~100 ms after it ends** (101–111 ms in every run; EventPipe flushes
on a 100 ms timer). When pauses come back to back, the report waits for the next pause too: the
monitor's thread is paused with everyone else. That happened when timer callbacks queued up
during a 7 s pause.

### Root cause: what the always-on tier says about one of these pauses

```
8,027 ms pause: GC #61 gen2 NonConcurrentGC reason=InducedCompacting compacting+LOH -> cause: Compaction
  phases: mark 1,849.0 ms (roots 1,849.0, ...), plan 195.4 ms, relocate 5,240.6 ms, compact 741.4 ms
  mark by root: overflow 1,042.3 ms/67.7 MB, stack 805.8 ms/833.1 MB, handles 0.8 ms/2.0 MB
  heap after 908.7 MB, promoted 906.0 MB, pinned objects 513, GC handles 643, heaps 1
    gen2: 844.7 MB -> 781.0 MB (fragmentation 38.1 MB -> 456 B)
    LOH : 219.1 MB -> 118.4 MB
  compacted because: compact_loh_forced
  * The GC was induced (InducedCompacting): something called GC.Collect asking for a compacting collection, on OS thread 1815. ...
  * Full blocking gen2 GC: the entire heap (844.7 MB in gen2) had to be marked with all threads stopped.
  * Compaction took 6,177 ms (plan 195, relocate 5,241, compact 741): every surviving object was moved ...
  * The mark stack overflowed: 1,042 ms went into recovering from it ...
  * The large object heap was compacted (710 ms). ...
  * Workstation GC: a single GC thread does the whole collection. ...
```

The phases are the GC's own timers (`GCGlobalHeapHistory_V4`). They add up to the pause
(1,849 + 195 + 5,241 + 741 = 8,026 ms of 8,027 ms), and the per-root mark times from
`GCMarkWithType` add up to the mark phase (1,042 + 806 = 1,848 ms). So the breakdown accounts
for the whole pause.

What the data says about this workload:

1. **The trigger is `GC.Collect`**: `reason=InducedCompacting`, and the detailed tier shows the
   exact caller (below).
2. **Relocation dominates**, not marking: ~2.5–5.2 s of a 4.7–8.5 s pause updates references to
   moved objects. With ~16 M small objects and 20 M random references, almost every reference
   in the heap points at something that moved.
3. **Marking is 1.8–2.4 s, and ~55% of it is mark-stack overflow.** The runtime starts with a
   1,024-entry mark stack (`MARK_STACK_INITIAL_LENGTH`, `gcinternal.h`). Each node pushes its
   link array, which pushes 4 random nodes, so the pending set of a random graph outgrows it.
   On overflow the GC records the address range and later rescans it
   (`process_mark_overflow`, `mark_phase.cpp`). The `overflow` root kind shows that cost directly.
4. **The ephemeral GCs are expensive too.** gen0/gen1 GCs take 200–560 ms, almost all spent
   scanning cards. Every mutation stores a new array into an old node, dirtying a card in an
   800 MB gen2, and a compacting ephemeral GC walks those cards twice (mark, then relocate). The
   monitor classifies those as `cause: CardScanning`.

### Who triggered it (detailed tier)

On the first long pause the monitor started a detailed session: GC events at Verbose, stacks and
rundown. That session recorded the next long pause and stopped 2 s later (8–9 s of trace, 1.7 MB).
Its report resolves the `GCTriggered` stacks; this excerpt is from an `always-detailed` run,
whose session covered all five induced GCs:

```
Who triggered the GCs (GCTriggered call stacks):
  5x InducedCompacting (GC #61, #63, #68, #72, #76)
      System.GC.Collect(int32,value class System.GCCollectionMode,bool,bool,bool)
      Program+<>c__DisplayClass0_0.<<Main>$>g__CompactorTick|6()
      System.Threading.TimerQueueTimer.Fire(bool)
      ...
  20x AllocSmall (GC #54, #55, #56, #57, #58)
      Graph.Mutate(int32,value class System.Threading.CancellationToken)
      ...
Allocated during the capture:
    325.9 MB   48.7%  System.Byte[]
    152.2 MB   22.8%  System.Int64[]
     93.9 MB   14.0%  Node[]
Pinned at GC time:
     5,426x     21.3 MB  System.Byte[]
```

With `--gcdump` it also takes a heap snapshot, which names the graph the GC had to trace:

```
Heap snapshot: 16,357,331 objects, 909.4 MB
     5,000,000 objects    267.0 MB  Node[]
     5,000,000 objects    228.9 MB  Node
     5,000,000 objects    228.9 MB  Node<System.Int64,Node>          (ConcurrentDictionary's nodes)
             1 objects     45.8 MB  VolatileNode<System.Int64,Node>[] (its bucket array)
```

The snapshot is expensive. It walks the heap inside its own induced gen2 GC, which here paused
the process for **9.6 s**, longer than the pauses under investigation. The monitor recognises that
pause: only 2.5 s of it is covered by GC phases, so it reports `cause: Unaccounted` ("74% of the
pause is not covered by the GC's phase timings ... heap snapshot ..."). The same signal would flag a
GC thread starved of CPU. The snapshot also needed `MaxNodeCount` raised above the default 10 M;
the report flags a truncated snapshot.

### Cost

Interleaved runs, 60 s each, same workload; "induced GC" is the median wall time of the
`GC.Collect` calls, measured by the app itself:

| Monitor | Run | Induced GC median | Mutations/s | Process CPU | Monitor events read |
| --- | --- | --- | --- | --- | --- |
| none | 1 | 5,521 ms | 33,160 | 17.8% | – |
| none | 2 | 5,897 ms | 28,749 | 19.9% | – |
| always-on (`basic`) | 1 | 5,836 ms | 30,873 | 18.3% | 551 |
| always-on (`basic`) | 2 | 6,013 ms | 30,426 | 18.8% | 455 |
| detailed session for the **whole** run | 1 | 5,864 ms | 31,568 | 18.7% | 447 |
| detailed session for the **whole** run | 2 | 5,721 ms | 30,545 | 18.9% | 455 |

The differences are within run-to-run noise: the two runs without a monitor differ from each
other by 7% in both throughput and pause time. The always-on tier reads ~17 events per GC (455–551
over a minute), allocates nothing per allocation, and dropped no events with a 16 MB buffer.
Even the detailed session, with Verbose GC events and stacks, cost nothing measurable *here*
because this workload allocates only ~10 MB/s. An allocation-heavy service emits many more
allocation ticks with stacks, which is why that tier only runs for a few seconds after a long
pause.

### Server GC, for comparison

`DOTNET_gcServer=1`, same workload, always-on monitor: induced pauses drop from ~5.8 s to
**~3.1 s** (2.8–3.9 s), and the total number of GCs drops from ~80 to 28 (bigger gen0 budgets),
so the process went from ~60% to 29% of wall clock paused. The monitor shows where that came from. Relocate parallelises
(1.0–1.7 s instead of 2.5–5.2 s), but marking does not (1.7–2.2 s). The whole graph hangs off
one root, so one heap does most of the tracing, the others pick up work by stealing
(`steal` ~500 ms) and the overflow rescans still cost ~1 s.

## Answers

- **Can the existing GC event flow detect long pauses?** Yes. The suspend/restart events give
  exact pause boundaries, and the totals match `GC.GetTotalPauseDuration()`.
- **Can it find the root cause with minimal impact?** Mostly yes, from the Informational events
  alone, at no measurable cost. Each pause is split into suspension time, mark (by root kind,
  including mark-stack overflow), plan, relocate and compact, along with why it ran, why it
  compacted, LOH compaction, fragmentation, pinning and the triggering thread. Phase timings need
  .NET 7+ on the target, and turning the stack walk off needs .NET 9+.
- **Can it capture more detail when a pause happens?** Yes, but only for the *next* pause.
  Events are read after the world restarts, so the pause that triggers escalation is already
  over. Its informational breakdown is complete; the detailed session (stacks, allocations,
  pinning, optional heap snapshot) records the next one. Long pauses usually recur, so a short
  capture has been enough.
- **What it cannot do:** run code *during* a pause (everything in the process is stopped,
  including the monitor), or name individual objects or fields that make marking slow. The
  type histogram of a heap snapshot is the closest it gets, and taking it costs a full pause of
  its own.
