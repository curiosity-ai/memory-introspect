using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Memory.Introspect.GcPauses
{
    /// <summary>
    /// The broad category a long pause is attributed to. A pause usually has one dominant cause;
    /// the full reasoning is in <see cref="GcPause.Findings"/>.
    /// </summary>
    public enum GcPauseCause
    {
        /// <summary>Not enough information to attribute the pause.</summary>
        Unknown = 0,

        /// <summary>Most of the pause was spent waiting for threads to reach a GC safe point.</summary>
        TimeToSafePoint,

        /// <summary>Most of the GC was spent marking: tracing the live object graph.</summary>
        Marking,

        /// <summary>Most of the GC was spent planning, relocating and compacting the heap.</summary>
        Compaction,

        /// <summary>Most of the GC was spent sweeping (building free lists) without compacting.</summary>
        Sweeping,

        /// <summary>
        /// An ephemeral (gen0/gen1) GC dominated by scanning cards: references from gen2 objects into
        /// the young generations, created by storing new objects into long-lived ones.
        /// </summary>
        CardScanning,

        /// <summary>
        /// Most of the pause is not covered by the GC's own phase timings: the GC thread was doing
        /// something else (a heap walk for a .gcdump / heap snapshot) or was not running at all
        /// (CPU starvation, container CPU throttling).
        /// </summary>
        Unaccounted,

        /// <summary>The execution engine was suspended for something other than a GC (debugger, shutdown, ...).</summary>
        NonGcSuspension,

        /// <summary>
        /// Part of a background GC. Background GCs only stop the world briefly, so a long one
        /// usually means the GC thread was starved or the final mark had a lot of work left.
        /// </summary>
        BackgroundGc,
    }

    /// <summary>Time spent in each phase of a blocking GC, as reported by the GC itself.</summary>
    public sealed class GcPhaseTimes
    {
        /// <summary>Marking from roots: stacks, handles, cards (older generations) and the mark stack drain.</summary>
        public double MarkRootsMSec { get; internal set; }

        /// <summary>Scanning short weak handles.</summary>
        public double MarkShortWeakMSec { get; internal set; }

        /// <summary>Scanning the finalization queue and marking what finalizable objects keep alive.</summary>
        public double MarkFinalizationMSec { get; internal set; }

        /// <summary>Scanning long weak handles.</summary>
        public double MarkLongWeakMSec { get; internal set; }

        /// <summary>Planning: walking the condemned generations to decide between sweeping and compacting.</summary>
        public double PlanMSec { get; internal set; }

        /// <summary>Updating every reference to point at the objects' new locations (compacting GCs only).</summary>
        public double RelocateMSec { get; internal set; }

        /// <summary>Copying the surviving objects (compacting GCs only).</summary>
        public double CompactMSec { get; internal set; }

        /// <summary>Building free lists out of dead space (sweeping GCs only).</summary>
        public double SweepMSec { get; internal set; }

        /// <summary>Total time spent marking.</summary>
        public double MarkMSec => MarkRootsMSec + MarkShortWeakMSec + MarkFinalizationMSec + MarkLongWeakMSec;

        /// <summary>Total time spent after marking: plan + relocate + compact, or plan + sweep.</summary>
        public double PostMarkMSec => PlanMSec + RelocateMSec + CompactMSec + SweepMSec;
    }

    /// <summary>How much marking each kind of root cost, from the GCMarkWithType events.</summary>
    public sealed class GcMarkRoot
    {
        public GcMarkRoot(MarkRootType kind, double markMSec, long promotedBytes)
        {
            Kind = kind;
            MarkMSec = markMSec;
            PromotedBytes = promotedBytes;
        }

        /// <summary>The root kind: stack, handles, older generation (cards), finalization queue, overflow, ...</summary>
        public MarkRootType Kind { get; }

        /// <summary>Time spent marking from this root kind, averaged over heaps.</summary>
        public double MarkMSec { get; }

        /// <summary>Bytes found live through this root kind.</summary>
        public long PromotedBytes { get; }

        public override string ToString() => $"{GcPause.RootName(Kind)}: {MarkMSec:0.0} ms, {GcPause.FormatBytes(PromotedBytes)} promoted";
    }

    /// <summary>A generation's size and fragmentation before and after a GC, summed over heaps.</summary>
    public sealed class GcGenerationInfo
    {
        public Gens Generation { get; internal set; }
        public long SizeBeforeBytes { get; internal set; }
        public long SizeAfterBytes { get; internal set; }
        public long FragmentationBeforeBytes { get; internal set; }
        public long FragmentationAfterBytes { get; internal set; }

        /// <summary>Bytes that survived out of this generation.</summary>
        public long SurvivedBytes { get; internal set; }

        public override string ToString() =>
            $"{GcPause.GenName(Generation)}: {GcPause.FormatBytes(SizeBeforeBytes)} -> {GcPause.FormatBytes(SizeAfterBytes)} " +
            $"(fragmentation {GcPause.FormatBytes(FragmentationBeforeBytes)} -> {GcPause.FormatBytes(FragmentationAfterBytes)})";
    }

    /// <summary>
    /// Everything the GC told us about one collection: why it happened, what kind it was, and
    /// how long each phase took.
    /// </summary>
    public sealed class GcCollectionInfo
    {
        /// <summary>The GC index (GC.CollectionCount-style, process wide).</summary>
        public int Number { get; internal set; }

        /// <summary>The condemned generation.</summary>
        public int Generation { get; internal set; }

        public GCType Type { get; internal set; }
        public GCReason Reason { get; internal set; }

        /// <summary>Whether this GC compacted (from the global mechanisms).</summary>
        public bool Compacted { get; internal set; }

        /// <summary>Whether the large object heap was compacted too.</summary>
        public bool CompactedLargeObjectHeap { get; internal set; }

        public GCPauseMode PauseMode { get; internal set; } = GCPauseMode.Invalid;

        /// <summary>The memory load the GC observed, in percent, or -1 when not reported.</summary>
        public int MemoryLoadPercent { get; internal set; } = -1;

        public GCGlobalMechanisms GlobalMechanisms { get; internal set; }

        /// <summary>Why the GC chose to compact, when it did (combined over heaps).</summary>
        public IReadOnlyList<gc_heap_compact_reason> CompactReasons { get; internal set; } = Array.Empty<gc_heap_compact_reason>();

        /// <summary>The decoded condemned reasons: why this generation was picked.</summary>
        public IReadOnlyList<string> CondemnReasons { get; internal set; } = Array.Empty<string>();

        /// <summary>Phase timings; null when the runtime did not report them (pre .NET 7, or a BGC).</summary>
        public GcPhaseTimes Phases { get; internal set; }

        /// <summary>Per-root-kind mark cost, largest first; empty when not reported.</summary>
        public IReadOnlyList<GcMarkRoot> MarkRoots { get; internal set; } = Array.Empty<GcMarkRoot>();

        /// <summary>Per-generation sizes and fragmentation before and after this GC.</summary>
        public IReadOnlyList<GcGenerationInfo> Generations { get; internal set; } = Array.Empty<GcGenerationInfo>();

        /// <summary>Time from GCStart to GCEnd.</summary>
        public double DurationMSec { get; internal set; }

        /// <summary>Bytes found live by this GC.</summary>
        public long PromotedBytes { get; internal set; }

        /// <summary>Total managed heap size after the GC.</summary>
        public long HeapSizeAfterBytes { get; internal set; }

        public int PinnedObjectCount { get; internal set; }
        public int GCHandleCount { get; internal set; }
        public int SinkBlockCount { get; internal set; }
        public long FinalizationPromotedCount { get; internal set; }
        public long FinalizationPromotedBytes { get; internal set; }

        /// <summary>LOH compaction phase timings in ms (plan, relocate, compact), summed over heaps.</summary>
        public double LohCompactMSec { get; internal set; }

        /// <summary>Number of heaps (1 for workstation GC).</summary>
        public int HeapCount { get; internal set; } = 1;

        /// <summary>The OS thread that triggered the GC, from the GCTriggered event (0 if unknown).</summary>
        public int TriggeringThreadId { get; internal set; }

        public bool IsInduced => Reason is GCReason.Induced or GCReason.InducedCompacting or GCReason.InducedNotForced or GCReason.InducedLowMemory;

        public bool IsBlocking => Type != GCType.BackgroundGC;
    }

    /// <summary>
    /// One period during which the runtime had managed threads stopped (SuspendEE → RestartEE),
    /// together with the GC that ran inside it and an attribution of where the time went.
    /// </summary>
    public sealed class GcPause
    {
        /// <summary>When the suspension started (UTC).</summary>
        public DateTime StartTimeUtc { get; internal set; }

        /// <summary>From the start of suspension until all threads were resumed.</summary>
        public double PauseMSec { get; internal set; }

        /// <summary>Time to suspend: how long it took all managed threads to reach a safe point.</summary>
        public double SuspendMSec { get; internal set; }

        /// <summary>Time from GC end until threads were resumed.</summary>
        public double RestartMSec { get; internal set; }

        /// <summary>Why the execution engine was suspended.</summary>
        public GCSuspendEEReason SuspendReason { get; internal set; }

        /// <summary>The GC that ran during this pause, or null for a non-GC suspension.</summary>
        public GcCollectionInfo Gc { get; internal set; }

        /// <summary>Whether <see cref="PauseMSec"/> exceeded the configured threshold.</summary>
        public bool IsLong { get; internal set; }

        /// <summary>
        /// True when the pause happened while the monitor was running a detailed capture, whose
        /// stack walking, rundown or heap snapshot may have made it longer.
        /// </summary>
        public bool DuringDetailedCapture { get; internal set; }

        /// <summary>The dominant cause.</summary>
        public GcPauseCause Cause { get; internal set; }

        /// <summary>Human readable root-cause findings, most important first.</summary>
        public IReadOnlyList<string> Findings { get; internal set; } = Array.Empty<string>();

        /// <summary>A one line summary.</summary>
        public string Summary
        {
            get
            {
                if (Gc is null)
                {
                    return $"{PauseMSec:N0} ms pause: execution engine suspended for {SuspendReason} (no GC)";
                }

                return string.Create(CultureInfo.InvariantCulture,
                    $"{PauseMSec:N0} ms pause: GC #{Gc.Number} gen{Gc.Generation} {Gc.Type} reason={Gc.Reason}" +
                    $"{(Gc.Compacted ? " compacting" : string.Empty)}{(Gc.CompactedLargeObjectHeap ? "+LOH" : string.Empty)} -> cause: {Cause}");
            }
        }

        public override string ToString() => ToReport();

        /// <summary>A multi-line report with the timing breakdown and the findings.</summary>
        public string ToReport()
        {
            StringBuilder sb = new();
            sb.Append(CultureInfo.InvariantCulture, $"{Summary}\n");
            sb.Append(CultureInfo.InvariantCulture, $"  started {StartTimeUtc:HH:mm:ss.fff}Z  suspend {SuspendMSec:N1} ms  restart {RestartMSec:N1} ms");
            sb.Append(DuringDetailedCapture ? "  (while a detailed capture was running)\n" : "\n");

            if (Gc is not null)
            {
                if (Gc.Phases is not null)
                {
                    GcPhaseTimes p = Gc.Phases;
                    sb.Append(CultureInfo.InvariantCulture, $"  phases: mark {p.MarkMSec:N1} ms (roots {p.MarkRootsMSec:N1}, short weak {p.MarkShortWeakMSec:N1}, finalization {p.MarkFinalizationMSec:N1}, long weak {p.MarkLongWeakMSec:N1})");
                    sb.Append(CultureInfo.InvariantCulture, $", plan {p.PlanMSec:N1} ms");
                    if (Gc.Compacted)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $", relocate {p.RelocateMSec:N1} ms, compact {p.CompactMSec:N1} ms\n");
                    }
                    else
                    {
                        sb.Append(CultureInfo.InvariantCulture, $", sweep {p.SweepMSec:N1} ms\n");
                    }
                }

                if (Gc.MarkRoots.Count > 0)
                {
                    sb.Append("  mark by root: ");
                    sb.Append(string.Join(", ", Gc.MarkRoots
                        .Where(r => r.MarkMSec >= 0.05 || r.PromotedBytes >= 1024)
                        .Select(r => $"{RootName(r.Kind)} {r.MarkMSec:N1} ms/{FormatBytes(r.PromotedBytes)}")));
                    sb.Append('\n');
                }

                sb.Append(CultureInfo.InvariantCulture, $"  heap after {FormatBytes(Gc.HeapSizeAfterBytes)}, promoted {FormatBytes(Gc.PromotedBytes)}, pinned objects {Gc.PinnedObjectCount:N0}, GC handles {Gc.GCHandleCount:N0}, heaps {Gc.HeapCount}");
                if (Gc.MemoryLoadPercent >= 0)
                {
                    sb.Append(CultureInfo.InvariantCulture, $", memory load {Gc.MemoryLoadPercent}%");
                }
                sb.Append('\n');

                foreach (GcGenerationInfo gen in Gc.Generations)
                {
                    if (gen.SizeBeforeBytes == 0 && gen.SizeAfterBytes == 0)
                    {
                        continue;
                    }
                    sb.Append("    ").Append(gen).Append('\n');
                }

                if (Gc.CondemnReasons.Count > 0)
                {
                    sb.Append("  condemned because: ").Append(string.Join(", ", Gc.CondemnReasons)).Append('\n');
                }

                if (Gc.CompactReasons.Count > 0)
                {
                    sb.Append("  compacted because: ").Append(string.Join(", ", Gc.CompactReasons)).Append('\n');
                }
            }

            foreach (string finding in Findings)
            {
                sb.Append("  * ").Append(finding).Append('\n');
            }

            return sb.ToString();
        }

        // ---- root cause attribution ------------------------------------------------------------

        internal void Diagnose(double longPauseThresholdMSec)
        {
            IsLong = PauseMSec >= longPauseThresholdMSec;

            List<string> findings = new();
            GcPauseCause cause = GcPauseCause.Unknown;

            if (Gc is null)
            {
                cause = GcPauseCause.NonGcSuspension;
                findings.Add($"The runtime stopped all managed threads for {SuspendReason} and no GC ran. " +
                             "Typical sources: an attached debugger, profiler ReJIT/attach, or a thread dump.");
            }

            // Time to safe point: how long threads took to stop. This is independent of the GC.
            if (SuspendMSec >= 20 && SuspendMSec >= 0.3 * PauseMSec)
            {
                cause = GcPauseCause.TimeToSafePoint;
                findings.Add($"Suspension took {SuspendMSec:N0} ms ({Pct(SuspendMSec, PauseMSec)} of the pause): some thread did not reach a GC safe point promptly. " +
                             "Look for long-running loops without calls, long native calls made in cooperative mode, or CPU starvation (more runnable threads than cores).");
            }

            if (Gc is not null)
            {
                GcCollectionInfo gc = Gc;

                if (gc.Type == GCType.BackgroundGC)
                {
                    cause = cause == GcPauseCause.Unknown ? GcPauseCause.BackgroundGc : cause;
                    findings.Add("This is the stop-the-world part of a background GC; most of a BGC runs concurrently. A long BGC pause usually means the final mark had a lot left to do.");
                }

                if (gc.IsInduced)
                {
                    findings.Add($"The GC was induced ({gc.Reason}): something called GC.Collect" +
                                 (gc.Reason == GCReason.InducedCompacting || gc.CompactedLargeObjectHeap ? " asking for a compacting collection" : string.Empty) +
                                 (gc.TriggeringThreadId != 0 ? $", on OS thread {gc.TriggeringThreadId}" : string.Empty) +
                                 ". Remove the call or switch to GCCollectionMode.Optimized / blocking: false to let the GC run a background collection instead.");
                }
                else if (gc.Reason is GCReason.LowMemory or GCReason.LowMemoryHost or GCReason.LowMemoryHostBlocking)
                {
                    findings.Add($"The GC was triggered by low memory ({gc.Reason}); low memory GCs are full, blocking and usually compacting.");
                }

                if (gc.Generation >= 2 && gc.Type == GCType.NonConcurrentGC)
                {
                    long gen2 = gc.Generations.Where(g => g.Generation == Gens.Gen2).Sum(g => g.SizeBeforeBytes);
                    findings.Add($"Full blocking gen2 GC: the entire heap ({FormatBytes(gen2)} in gen2) had to be marked with all threads stopped." +
                                 (gc.IsInduced ? string.Empty : " A background GC was not used here; the condemned reasons below say why."));
                }

                if (gc.Phases is not null)
                {
                    GcPhaseTimes p = gc.Phases;
                    double gcTime = Math.Max(p.MarkMSec + p.PostMarkMSec, 0.001);

                    // Phases are measured by the GC itself; time in the pause they do not cover was
                    // spent outside the collection proper.
                    double unaccounted = PauseMSec - SuspendMSec - RestartMSec - (p.MarkMSec + p.PostMarkMSec);
                    if (gc.Type != GCType.BackgroundGC && unaccounted >= 50 && unaccounted >= 0.3 * PauseMSec)
                    {
                        if (cause == GcPauseCause.Unknown && unaccounted >= Math.Max(p.MarkMSec, p.PostMarkMSec))
                        {
                            cause = GcPauseCause.Unaccounted;
                        }
                        findings.Add($"{unaccounted:N0} ms ({Pct(unaccounted, PauseMSec)}) of the pause is not covered by the GC's phase timings. " +
                                     "The GC was doing something besides collecting — typically walking the heap for a heap snapshot (.gcdump, a profiler, the GCHeapDump keyword) — " +
                                     "or its thread was not getting CPU (starvation, container CPU throttling).");
                    }

                    if (cause == GcPauseCause.Unknown && gc.Type != GCType.BackgroundGC)
                    {
                        if (p.MarkMSec >= p.PostMarkMSec)
                        {
                            cause = GcPauseCause.Marking;
                        }
                        else if (gc.Compacted)
                        {
                            cause = GcPauseCause.Compaction;
                        }
                        else
                        {
                            cause = GcPauseCause.Sweeping;
                        }
                    }

                    GcMarkRoot older = gc.MarkRoots.FirstOrDefault(r => r.Kind == MarkRootType.MarkOlder);
                    bool cardBound = gc.Generation < 2 && older is not null && older.MarkMSec >= 0.5 * p.MarkMSec && older.MarkMSec >= 10;

                    if (cardBound)
                    {
                        if (cause is GcPauseCause.Marking or GcPauseCause.Compaction or GcPauseCause.Sweeping)
                        {
                            cause = GcPauseCause.CardScanning;
                        }

                        long gen2 = gc.Generations.Where(g => g.Generation == Gens.Gen2).Sum(g => g.SizeBeforeBytes);
                        findings.Add($"This gen{gc.Generation} GC spent {older.MarkMSec:N0} ms of its {p.MarkMSec:N0} ms mark scanning cards: references stored into old (gen2) objects that point at young ones" +
                                     (gc.Compacted ? $", and relocate ({p.RelocateMSec:N0} ms) walks the same cards again to fix those references up" : string.Empty) + ". " +
                                     $"Every write of a newly allocated object into a long-lived one dirties a card, and with {FormatBytes(gen2)} of gen2 written to at random the ephemeral GCs pay for it. " +
                                     "Mutate in place, reuse arrays, or allocate the replacements in bulk so fewer old objects get pointed at new ones.");
                    }
                    else if (p.MarkMSec >= 0.4 * gcTime && p.MarkMSec >= 10)
                    {
                        double mbPerSec = gc.PromotedBytes / 1024.0 / 1024.0 / Math.Max(p.MarkMSec / 1000.0, 0.000001);
                        findings.Add($"Marking took {p.MarkMSec:N0} ms ({Pct(p.MarkMSec, gcTime)} of the GC) to trace {FormatBytes(gc.PromotedBytes)} of live objects ({mbPerSec:N0} MB/s). " +
                                     "Mark cost grows with the number of live objects and references rather than their size; a large, highly connected graph of small objects " +
                                     "(e.g. millions of entries in a dictionary pointing at each other) is the classic cause. Fewer objects (structs, arrays of indices instead of references), " +
                                     "or keeping the graph off the GC heap, reduces it.");
                    }

                    if (gc.Compacted && (p.PlanMSec + p.RelocateMSec + p.CompactMSec) >= 0.4 * gcTime && (p.PlanMSec + p.RelocateMSec + p.CompactMSec) >= 10)
                    {
                        findings.Add($"Compaction took {p.PlanMSec + p.RelocateMSec + p.CompactMSec:N0} ms (plan {p.PlanMSec:N0}, relocate {p.RelocateMSec:N0}, compact {p.CompactMSec:N0}): " +
                                     "every surviving object was moved and every reference to it updated" +
                                     (gc.Generation >= 2 ? $" — {FormatBytes(gc.PromotedBytes)} of survivors here." : ".") +
                                     (gc.CompactReasons.Count > 0 ? $" The GC compacted because of: {string.Join(", ", gc.CompactReasons.Select(DescribeCompactReason))}." : string.Empty));
                    }
                    else if (!gc.Compacted && p.SweepMSec + p.PlanMSec >= 0.4 * gcTime && p.SweepMSec + p.PlanMSec >= 10)
                    {
                        findings.Add($"Plan + sweep took {p.PlanMSec + p.SweepMSec:N0} ms: the GC walked a large number of surviving and dead objects to build free lists.");
                    }
                }

                // Per-root mark breakdown, from GCMarkWithType.
                GcMarkRoot overflow = gc.MarkRoots.FirstOrDefault(r => r.Kind == MarkRootType.MarkOverflow);
                if (overflow is not null && overflow.PromotedBytes > 0)
                {
                    findings.Add($"The mark stack overflowed: {overflow.MarkMSec:N0} ms went into recovering from it ({FormatBytes(overflow.PromotedBytes)} marked that way). " +
                                 "The graph is so deep or wide that the GC's mark stack filled up, lost track of pending objects and had to rescan heap ranges for them. " +
                                 "Long reference chains and objects with huge fan-out (big arrays of references, dictionary buckets) cause this.");
                }

                GcMarkRoot top = gc.MarkRoots.Count > 0 ? gc.MarkRoots[0] : null;
                if (top is not null && top.MarkMSec >= 10 && top.Kind != MarkRootType.MarkOverflow && !(top.Kind == MarkRootType.MarkOlder && gc.Generation < 2))
                {
                    string why = top.Kind switch
                    {
                        MarkRootType.MarkStack => "objects reachable from locals on thread stacks",
                        MarkRootType.MarkHandles => $"objects reachable from GC handles ({gc.GCHandleCount:N0} handles): static fields, strong/pinned handles, timers, ...",
                        MarkRootType.MarkOlder => "references from older generations into the condemned ones (card marking)",
                        MarkRootType.MarkFQ or MarkRootType.MarkNewFQ => $"objects kept alive by finalizable objects ({gc.FinalizationPromotedCount:N0} finalizable objects promoted)",
                        MarkRootType.MarkDependentHandles => "dependent handles (ConditionalWeakTable entries)",
                        MarkRootType.MarkSizedRef => "sized-ref handles",
                        MarkRootType.MarkSteal => "work stealing between server GC heaps",
                        MarkRootType.MarkBGCRoots => "background GC roots",
                        _ => top.Kind.ToString(),
                    };
                    findings.Add($"Most marking time came from {RootName(top.Kind)} roots ({top.MarkMSec:N0} ms, {FormatBytes(top.PromotedBytes)} live): {why}.");
                }

                if (gc.CompactedLargeObjectHeap)
                {
                    findings.Add($"The large object heap was compacted ({(gc.LohCompactMSec > 0 ? $"{gc.LohCompactMSec:N0} ms" : "time not reported")}). " +
                                 "LOH compaction only happens when requested (GCSettings.LargeObjectHeapCompactionMode = CompactOnce) or under memory pressure / hard limits.");
                }

                if (gc.PinnedObjectCount >= 1000)
                {
                    findings.Add($"{gc.PinnedObjectCount:N0} objects were pinned during the GC; pinning fragments the heap and makes plan/compact slower.");
                }

                if (gc.MemoryLoadPercent >= 90)
                {
                    findings.Add($"Memory load was {gc.MemoryLoadPercent}%: under high memory load the GC compacts more aggressively and favours full blocking GCs.");
                }

                if (gc.HeapCount == 1 && gc.Generation >= 2 && gc.Type == GCType.NonConcurrentGC && PauseMSec >= 100)
                {
                    findings.Add("Workstation GC: a single GC thread does the whole collection. Server GC (<ServerGarbageCollector>true</ServerGarbageCollector>) parallelises mark and compact across cores.");
                }
            }

            Cause = cause;
            Findings = findings;
        }

        private static string DescribeCompactReason(gc_heap_compact_reason reason) => reason switch
        {
            gc_heap_compact_reason.compact_induced_compacting => "induced compacting GC (GC.Collect(..., compacting: true))",
            gc_heap_compact_reason.compact_loh_forced => "LOH compaction was requested",
            gc_heap_compact_reason.compact_high_frag => "high fragmentation",
            gc_heap_compact_reason.compact_high_mem_frag => "high memory load and fragmentation",
            gc_heap_compact_reason.compact_vhigh_mem_frag => "very high memory load and fragmentation",
            gc_heap_compact_reason.compact_high_mem_load => "high memory load",
            gc_heap_compact_reason.compact_low_ephemeral => "not enough ephemeral space",
            gc_heap_compact_reason.compact_no_gaps => "no gaps to sweep into",
            gc_heap_compact_reason.compact_last_gc => "last GC before OOM",
            gc_heap_compact_reason.compact_fragmented_gen0 => "fragmented gen0",
            gc_heap_compact_reason.compact_no_gc_mode => "entering a no-GC region",
            _ => reason.ToString(),
        };

        internal static string RootName(MarkRootType kind) => kind switch
        {
            MarkRootType.MarkStack => "stack",
            MarkRootType.MarkFQ => "finalize-queue",
            MarkRootType.MarkHandles => "handles",
            MarkRootType.MarkOlder => "older-gen",
            MarkRootType.MarkSizedRef => "sizedref",
            MarkRootType.MarkOverflow => "overflow",
            MarkRootType.MarkDependentHandles => "dependent-handles",
            MarkRootType.MarkNewFQ => "new-finalize-queue",
            MarkRootType.MarkSteal => "steal",
            MarkRootType.MarkBGCRoots => "bgc-roots",
            _ => kind.ToString(),
        };

        internal static string GenName(Gens gen) => gen switch
        {
            Gens.Gen0 => "gen0",
            Gens.Gen1 => "gen1",
            Gens.Gen2 => "gen2",
            Gens.GenLargeObj => "LOH ",
            Gens.GenPinObj => "POH ",
            _ => gen.ToString(),
        };

        private static string Pct(double part, double whole) => whole <= 0 ? "?" : (part * 100.0 / whole).ToString("0", CultureInfo.InvariantCulture) + "%";

        internal static string FormatBytes(long bytes)
        {
            double b = bytes;
            if (Math.Abs(b) >= 1024L * 1024 * 1024) return (b / (1024.0 * 1024 * 1024)).ToString("0.00", CultureInfo.InvariantCulture) + " GB";
            if (Math.Abs(b) >= 1024 * 1024) return (b / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB";
            if (Math.Abs(b) >= 1024) return (b / 1024.0).ToString("0.0", CultureInfo.InvariantCulture) + " KB";
            return bytes.ToString(CultureInfo.InvariantCulture) + " B";
        }
    }
}
