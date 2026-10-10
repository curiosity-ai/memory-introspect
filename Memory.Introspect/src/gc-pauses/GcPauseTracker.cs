using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Analysis.GC;
using Microsoft.Diagnostics.Tracing.Parsers;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Memory.Introspect.GcPauses
{
    /// <summary>
    /// Turns the stream of informational GC events into <see cref="GcPause"/> records. It keeps
    /// only the state of the GC in flight, so its memory use is bounded no matter how long it
    /// runs — unlike TraceEvent's <c>TraceLoadedDotNetRuntime</c>, which keeps every GC.
    /// </summary>
    /// <remarks>
    /// For a blocking GC the runtime emits, in order: GCTriggered (on the triggering thread),
    /// GCSuspendEEBegin, GCSuspendEEEnd, GCStart, GCMarkWithType (one per root kind and heap),
    /// GCGlobalHeapHistory (carries the per-phase times since .NET 7), GCPerHeapHistory,
    /// GCLOHCompact (when the LOH was compacted), GCEnd, GCHeapStats, GCRestartEEBegin and
    /// GCRestartEEEnd. Everything needed to explain the pause has arrived by GCRestartEEEnd,
    /// which is where a pause is completed.
    /// </remarks>
    internal sealed class GcPauseTracker
    {
        private readonly double _longPauseThresholdMSec;
        private readonly Action<GcPause> _onPause;

        private PendingPause _pause;
        private PendingGc _blockingGc;
        private PendingGc _backgroundGc;

        private int _lastTriggerThreadId;

        public GcPauseTracker(double longPauseThresholdMSec, Action<GcPause> onPause)
        {
            _longPauseThresholdMSec = longPauseThresholdMSec;
            _onPause = onPause;
        }

        public long EventCount { get; private set; }

        public void Subscribe(ClrTraceEventParser clr)
        {
            clr.GCTriggered += OnTriggered;
            clr.GCSuspendEEStart += OnSuspendStart;
            clr.GCSuspendEEStop += OnSuspendStop;
            clr.GCStart += OnGcStart;
            clr.GCMarkWithType += OnMarkWithType;
            clr.GCGlobalHeapHistory += OnGlobalHeapHistory;
            clr.GCPerHeapHistory += OnPerHeapHistory;
            clr.GCLOHCompact += OnLohCompact;
            clr.GCStop += OnGcEnd;
            clr.GCHeapStats += OnHeapStats;
            clr.GCRestartEEStart += OnRestartStart;
            clr.GCRestartEEStop += OnRestartStop;
        }

        private void OnTriggered(GCTriggeredTraceData data)
        {
            EventCount++;
            _lastTriggerThreadId = data.ThreadID;
        }

        private void OnSuspendStart(GCSuspendEETraceData data)
        {
            EventCount++;
            _pause = new PendingPause
            {
                Start = data.TimeStampRelativeMSec,
                StartUtc = data.TimeStamp.ToUniversalTime(),
                Reason = data.Reason,
            };
        }

        private void OnSuspendStop(TraceEvent data)
        {
            EventCount++;
            if (_pause is not null)
            {
                _pause.SuspendEnd = data.TimeStampRelativeMSec;
            }
        }

        private void OnGcStart(GCStartTraceData data)
        {
            EventCount++;
            PendingGc gc = new()
            {
                Start = data.TimeStampRelativeMSec,
                Info = new GcCollectionInfo
                {
                    Number = data.Count,
                    Generation = data.Depth,
                    Type = data.Type,
                    Reason = data.Reason,
                },
            };

            // GCTriggered fires on the thread that asked for the GC, before the suspension. It is
            // forgotten at the end of every pause, so it can only belong to this GC.
            if (_lastTriggerThreadId != 0)
            {
                gc.Info.TriggeringThreadId = _lastTriggerThreadId;
                _lastTriggerThreadId = 0;
            }

            if (data.Type == GCType.BackgroundGC)
            {
                _backgroundGc = gc;
            }
            else
            {
                _blockingGc = gc;
            }

            if (_pause is not null && _pause.Gc is null)
            {
                _pause.Gc = gc;
            }
        }

        // Mark events, heap histories and the LOH compact event belong to the blocking GC in
        // flight when there is one, otherwise to the background GC.
        private PendingGc Current => _blockingGc ?? _backgroundGc;

        private void OnMarkWithType(GCMarkWithTypeTraceData data)
        {
            EventCount++;
            PendingGc gc = Current;
            if (gc is null || data.Type < 0 || data.Type >= (int)MarkRootType.MarkMax)
            {
                return;
            }

            if (!gc.LastMarkTime.TryGetValue(data.HeapNum, out double last))
            {
                last = gc.Start;
            }

            gc.MarkTime[data.Type] += data.TimeStampRelativeMSec - last;
            gc.MarkPromoted[data.Type] += data.Promoted;
            gc.LastMarkTime[data.HeapNum] = data.TimeStampRelativeMSec;
            gc.MarkHeaps.Add(data.HeapNum);
        }

        private void OnGlobalHeapHistory(GCGlobalHeapHistoryTraceData data)
        {
            EventCount++;
            PendingGc gc = Current;
            if (gc is null)
            {
                return;
            }

            GcCollectionInfo info = gc.Info;
            info.GlobalMechanisms = data.GlobalMechanisms;
            info.Compacted = (data.GlobalMechanisms & GCGlobalMechanisms.Compaction) != 0;
            info.HeapCount = Math.Max(1, data.NumHeaps);
            if (data.HasPauseMode)
            {
                info.PauseMode = data.PauseMode;
            }
            if (data.HasMemoryPressure)
            {
                info.MemoryLoadPercent = data.MemoryPressure;
            }

            if (data.HasCondemnReasons0)
            {
                info.CondemnReasons = DecodeCondemnReasons(data.CondemnReasons0, data.HasCondemnReasons1 ? data.CondemnReasons1 : 0);
            }

            // GCGlobalHeapHistory_V4 (.NET 7+) carries the per-phase times, in microseconds:
            // [0] mark sizedref, [1] mark roots, [2] short weak, [3] finalization, [4] long weak,
            // then for blocking GCs [5] plan and either [6] relocate + [7] compact, or [6] sweep.
            int[] times = data.HasCount ? data.Times : null;
            if (times is { Length: >= 5 })
            {
                GcPhaseTimes phases = new()
                {
                    MarkRootsMSec = (times[0] + times[1]) / 1000.0,
                    MarkShortWeakMSec = times[2] / 1000.0,
                    MarkFinalizationMSec = times[3] / 1000.0,
                    MarkLongWeakMSec = times[4] / 1000.0,
                };

                if (times.Length > 5)
                {
                    phases.PlanMSec = times[5] / 1000.0;
                    if (info.Compacted && times.Length > 7)
                    {
                        phases.RelocateMSec = times[6] / 1000.0;
                        phases.CompactMSec = times[7] / 1000.0;
                    }
                    else if (times.Length > 6)
                    {
                        phases.SweepMSec = times[6] / 1000.0;
                    }
                }

                info.Phases = phases;
            }
        }

        private void OnPerHeapHistory(GCPerHeapHistoryTraceData data)
        {
            EventCount++;
            PendingGc gc = Current;
            if (gc is null || !data.VersionRecognized)
            {
                return;
            }

            if (data.CompactMechanisms != gc_heap_compact_reason.not_specified)
            {
                gc.CompactReasons.Add(data.CompactMechanisms);
            }

            int generations = Math.Min(data.HasCount ? data.Count : 4, (int)Gens.MaxGenCount);
            for (int g = 0; g < generations; g++)
            {
                GCPerHeapHistoryGenData gen = data.GenData((Gens)g);
                if (gen is null)
                {
                    continue;
                }

                if (!gc.Generations.TryGetValue(g, out GcGenerationInfo info))
                {
                    info = new GcGenerationInfo { Generation = (Gens)g };
                    gc.Generations[g] = info;
                }

                info.SizeBeforeBytes += gen.SizeBefore;
                info.SizeAfterBytes += gen.SizeAfter;
                info.FragmentationBeforeBytes += (gen.HasFreeListSpaceBefore ? gen.FreeListSpaceBefore : 0) + (gen.HasFreeObjSpaceBefore ? gen.FreeObjSpaceBefore : 0);
                info.FragmentationAfterBytes += gen.Fragmentation;
                info.SurvivedBytes += gen.Out;
            }
        }

        private void OnLohCompact(GCLOHCompactTraceData data)
        {
            EventCount++;
            PendingGc gc = Current;
            if (gc is null)
            {
                return;
            }

            gc.Info.CompactedLargeObjectHeap = true;
            double totalMicroseconds = 0;
            for (int i = 0; i < data.Count; i++)
            {
                GCLOHCompactInfo heap = data.Info(i);
                totalMicroseconds += heap.TimePlan + heap.TimeRelocate + heap.TimeCompact;
            }
            gc.Info.LohCompactMSec = totalMicroseconds / 1000.0;
        }

        private void OnGcEnd(GCEndTraceData data)
        {
            EventCount++;
            PendingGc gc = (_blockingGc is not null && _blockingGc.Info.Number == data.Count) ? _blockingGc
                         : (_backgroundGc is not null && _backgroundGc.Info.Number == data.Count) ? _backgroundGc
                         : null;
            if (gc is null)
            {
                return;
            }

            gc.Info.DurationMSec = data.TimeStampRelativeMSec - gc.Start;
            gc.Ended = true;
            Seal(gc);

            if (ReferenceEquals(gc, _backgroundGc))
            {
                _backgroundGc = null;
            }
            // The blocking GC is cleared once GCHeapStats (which follows GCEnd) has been seen.
        }

        private void OnHeapStats(GCHeapStatsTraceData data)
        {
            EventCount++;
            PendingGc gc = _blockingGc;
            if (gc is null)
            {
                return;
            }

            GcCollectionInfo info = gc.Info;
            info.PromotedBytes = data.TotalPromoted;
            info.HeapSizeAfterBytes = data.TotalHeapSize;
            info.PinnedObjectCount = data.PinnedObjectCount;
            info.GCHandleCount = data.GCHandleCount;
            info.SinkBlockCount = data.SinkBlockCount;
            info.FinalizationPromotedCount = data.FinalizationPromotedCount;
            info.FinalizationPromotedBytes = data.FinalizationPromotedSize;

            if (gc.Ended)
            {
                _blockingGc = null;
            }
        }

        private void OnRestartStart(TraceEvent data)
        {
            EventCount++;
            if (_pause is not null)
            {
                _pause.RestartStart = data.TimeStampRelativeMSec;
            }
        }

        private void OnRestartStop(TraceEvent data)
        {
            EventCount++;
            PendingPause pending = _pause;
            _pause = null;
            _lastTriggerThreadId = 0;
            if (pending is null)
            {
                return;
            }

            double end = data.TimeStampRelativeMSec;
            GcPause pause = new()
            {
                StartTimeUtc = pending.StartUtc,
                PauseMSec = end - pending.Start,
                SuspendMSec = pending.SuspendEnd > 0 ? pending.SuspendEnd - pending.Start : 0,
                SuspendReason = pending.Reason,
            };

            PendingGc gc = pending.Gc;
            if (gc is null && pending.Reason == GCSuspendEEReason.SuspendForGCPrep && _backgroundGc is not null)
            {
                // The final, stop-the-world part of a background GC.
                gc = _backgroundGc;
            }

            if (gc is not null)
            {
                Seal(gc);
                pause.Gc = gc.Info;
                double gcEnd = gc.Ended ? gc.Start + gc.Info.DurationMSec : (pending.RestartStart > 0 ? pending.RestartStart : end);
                pause.RestartMSec = Math.Max(0, end - gcEnd);
            }
            else if (pending.RestartStart > 0)
            {
                pause.RestartMSec = end - pending.RestartStart;
            }

            pause.Diagnose(_longPauseThresholdMSec);
            _onPause(pause);
        }

        private static void Seal(PendingGc gc)
        {
            GcCollectionInfo info = gc.Info;
            int heaps = Math.Max(1, gc.MarkHeaps.Count);

            List<GcMarkRoot> roots = new();
            for (int i = 0; i < gc.MarkTime.Length; i++)
            {
                if (gc.MarkTime[i] > 0 || gc.MarkPromoted[i] > 0)
                {
                    roots.Add(new GcMarkRoot((MarkRootType)i, gc.MarkTime[i] / heaps, gc.MarkPromoted[i]));
                }
            }
            info.MarkRoots = roots.OrderByDescending(r => r.MarkMSec).ToList();
            info.CompactReasons = gc.CompactReasons.ToList();
            info.Generations = gc.Generations.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
        }

        internal static IReadOnlyList<string> DecodeCondemnReasons(int reasons0, int reasons1)
        {
            GCCondemnedReasons decoded = new();
            decoded.EncodedReasons.Reasons = reasons0;
            decoded.EncodedReasons.ReasonsEx = reasons1;
            decoded.CondemnedReasonGroups = new byte[(int)CondemnedReasonGroup.Max];
            try
            {
                decoded.Decode(3);
            }
            catch
            {
                return Array.Empty<string>();
            }

            List<string> result = new();
            for (int i = 0; i < decoded.CondemnedReasonGroups.Length; i++)
            {
                byte value = decoded.CondemnedReasonGroups[i];
                CondemnedReasonGroup group = (CondemnedReasonGroup)i;
                if (value == 0)
                {
                    continue;
                }

                // The first four groups carry a generation number; the rest are flags.
                result.Add(group switch
                {
                    CondemnedReasonGroup.Initial_Generation or CondemnedReasonGroup.Final_Generation
                        or CondemnedReasonGroup.Alloc_Exceeded or CondemnedReasonGroup.Time_Tuning => $"{group}=gen{value}",
                    CondemnedReasonGroup.Induced => value == 1 ? "Induced(blocking)" : "Induced(not forced)",
                    _ => group.ToString(),
                });
            }
            return result;
        }

        private sealed class PendingPause
        {
            public double Start;
            public DateTime StartUtc;
            public double SuspendEnd;
            public double RestartStart;
            public GCSuspendEEReason Reason;
            public PendingGc Gc;
        }

        private sealed class PendingGc
        {
            public double Start;
            public bool Ended;
            public GcCollectionInfo Info;
            public readonly double[] MarkTime = new double[(int)MarkRootType.MarkMax];
            public readonly long[] MarkPromoted = new long[(int)MarkRootType.MarkMax];
            public readonly Dictionary<int, double> LastMarkTime = new();
            public readonly HashSet<int> MarkHeaps = new();
            public readonly HashSet<gc_heap_compact_reason> CompactReasons = new();
            public readonly SortedDictionary<int, GcGenerationInfo> Generations = new();
        }
    }
}
