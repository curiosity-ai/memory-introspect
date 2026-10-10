using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Graphs;
using Memory.Introspect.Trace;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Memory.Introspect.GcPauses
{
    /// <summary>The code that asked for a GC: the call stack of a GCTriggered event.</summary>
    public sealed class GcTriggerSite
    {
        public GCReason Reason { get; internal set; }

        /// <summary>Number of GCs triggered from this stack during the capture.</summary>
        public int Count { get; internal set; }

        /// <summary>The GC numbers triggered from this stack.</summary>
        public IReadOnlyList<int> GcNumbers { get; internal set; } = Array.Empty<int>();

        /// <summary>Frames, innermost first.</summary>
        public IReadOnlyList<string> Frames { get; internal set; } = Array.Empty<string>();

        public override string ToString() => $"{Count}x {Reason}: {string.Join(" <- ", Frames.Take(6))}";
    }

    /// <summary>A type pinned at GC time (PinObjectAtGCTime, Verbose GC events).</summary>
    public sealed class GcPinnedType
    {
        public string TypeName { get; internal set; }
        public long Count { get; internal set; }
        public long Bytes { get; internal set; }
    }

    /// <summary>One row of the heap snapshot histogram.</summary>
    public sealed class GcHeapType
    {
        public string TypeName { get; internal set; }
        public long Count { get; internal set; }
        public long Bytes { get; internal set; }
    }

    /// <summary>What a detailed capture found.</summary>
    public sealed class GcPauseDetailedReport
    {
        /// <summary>The long pause that triggered the capture (null for a manual capture).</summary>
        public GcPause Trigger { get; internal set; }

        public string TraceFilePath { get; internal set; }
        public long TraceSizeInBytes { get; internal set; }
        public TimeSpan Elapsed { get; internal set; }

        /// <summary>Every pause recorded in the capture, re-analysed from the detailed trace.</summary>
        public IReadOnlyList<GcPause> Pauses { get; internal set; } = Array.Empty<GcPause>();

        /// <summary>The long pauses recorded in the capture.</summary>
        public IReadOnlyList<GcPause> LongPauses => Pauses.Where(p => p.IsLong).ToList();

        /// <summary>Who triggered the GCs during the capture, most frequent first (requires stacks).</summary>
        public IReadOnlyList<GcTriggerSite> TriggerSites { get; internal set; } = Array.Empty<GcTriggerSite>();

        /// <summary>What was being allocated during the capture (requires Verbose GC events).</summary>
        public AllocationReport Allocations { get; internal set; }

        /// <summary>Types pinned at GC time (requires Verbose GC events).</summary>
        public IReadOnlyList<GcPinnedType> PinnedTypes { get; internal set; } = Array.Empty<GcPinnedType>();

        /// <summary>The .gcdump taken after the trace, when enabled.</summary>
        public string GcDumpPath { get; internal set; }

        /// <summary>The largest types on the heap, by bytes, when a .gcdump was taken.</summary>
        public IReadOnlyList<GcHeapType> HeapTypes { get; internal set; } = Array.Empty<GcHeapType>();

        public long HeapObjectCount { get; internal set; }

        /// <summary>
        /// True when the heap had more objects than <see cref="MemoryIntrospectorOptions.MaxNodeCount"/>
        /// and the snapshot (and so <see cref="HeapTypes"/>) only covers part of it.
        /// </summary>
        public bool HeapSnapshotTruncated { get; internal set; }
        public long HeapBytes { get; internal set; }

        public Exception Exception { get; internal set; }

        public bool Success => Exception is null && TraceFilePath is not null;

        public override string ToString() => ToReport();

        public string ToReport()
        {
            StringBuilder sb = new();
            sb.Append("=== Detailed GC pause capture ===\n");
            if (Exception is not null)
            {
                sb.Append("Capture failed: ").Append(Exception.Message).Append('\n');
            }
            if (Trigger is not null)
            {
                sb.Append("Triggered by: ").Append(Trigger.Summary).Append('\n');
            }
            if (TraceFilePath is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Trace: {TraceFilePath} ({GcPause.FormatBytes(TraceSizeInBytes)}, {Elapsed.TotalSeconds:N1} s)\n");
            }

            sb.Append(CultureInfo.InvariantCulture, $"Pauses recorded: {Pauses.Count}, long: {LongPauses.Count}, max {(Pauses.Count == 0 ? 0 : Pauses.Max(p => p.PauseMSec)):N0} ms\n");
            foreach (GcPause pause in LongPauses)
            {
                sb.Append('\n').Append(pause.ToReport());
            }

            if (TriggerSites.Count > 0)
            {
                sb.Append("\nWho triggered the GCs (GCTriggered call stacks):\n");
                foreach (GcTriggerSite site in TriggerSites.Take(5))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  {site.Count}x {site.Reason} (GC #{string.Join(", #", site.GcNumbers.Take(5))})\n");
                    foreach (string frame in site.Frames.Take(12))
                    {
                        sb.Append("      ").Append(frame).Append('\n');
                    }
                }
            }

            if (Allocations is { IsEmpty: false })
            {
                sb.Append("\nAllocated during the capture:\n");
                foreach (AllocatedType type in Allocations.Types.Take(10))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  {GcPause.FormatBytes(type.AllocatedBytes),10}  {type.AllocatedBytesPercent,5:0.0}%  {type.TypeName}\n");
                }
            }

            if (PinnedTypes.Count > 0)
            {
                sb.Append("\nPinned at GC time:\n");
                foreach (GcPinnedType pinned in PinnedTypes.Take(10))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  {pinned.Count,8:N0}x  {GcPause.FormatBytes(pinned.Bytes),10}  {pinned.TypeName}\n");
                }
            }

            if (HeapTypes.Count > 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"\nHeap snapshot ({GcDumpPath}): {HeapObjectCount:N0} objects, {GcPause.FormatBytes(HeapBytes)}");
                sb.Append(HeapSnapshotTruncated ? " (truncated at MaxNodeCount: only part of the heap is shown; raise MemoryIntrospectorOptions.MaxNodeCount)\n" : "\n");
                foreach (GcHeapType type in HeapTypes)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"  {type.Count,12:N0} objects  {GcPause.FormatBytes(type.Bytes),10}  {type.TypeName}\n");
                }
            }

            return sb.ToString();
        }
    }

    internal static class GcPauseDetailedCapture
    {
        public static async Task<GcPauseDetailedReport> RunAsync(
            MemoryIntrospector introspector,
            int processId,
            string diagnosticPort,
            GcPauseDetailedCaptureOptions options,
            TimeSpan duration,
            GcPause trigger,
            double longPauseThresholdMSec,
            TextWriter log,
            CancellationToken stopTrace)
        {
            string directory = options.OutputDirectory ?? Path.Combine(Path.GetTempPath(), "memory-introspect-gc-pauses");
            Directory.CreateDirectory(directory);
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
            string tracePath = Path.Combine(directory, $"gc-pauses-{processId}-{stamp}.nettrace");

            ClrEventKeywords keywords = ClrEventKeywords.Gc;
            if (options.ObjectSurvivalAndMovement)
            {
                keywords |= ClrEventKeywords.GcHeapSurvivalAndMovement;
            }

            TraceCollectionOptions traceOptions = new()
            {
                ClrEvents = keywords,
                ClrEventLevel = options.VerboseGcEvents ? EventLevel.Verbose : EventLevel.Informational,
                // The private provider's GC keyword adds the background GC phase events.
                Providers = new[] { "Microsoft-Windows-DotNETRuntimePrivate:0x1:4" },
                Rundown = options.CollectStacks,
                RequestStackwalk = options.CollectStacks,
                Duration = duration,
                OutputPath = tracePath,
                CircularBufferSizeInMB = options.CircularBufferSizeInMB,
                DiagnosticPort = diagnosticPort,
            };

            TraceResult trace = await introspector.CollectTraceAsync(processId, traceOptions, stopTrace).ConfigureAwait(false);

            GcPauseDetailedReport report = new()
            {
                Trigger = trigger,
                Elapsed = trace.Elapsed,
                TraceSizeInBytes = trace.TraceSizeInBytes,
                Exception = trace.Exception,
            };

            if (!trace.Success || trace.TraceFilePath is null)
            {
                return report;
            }

            report.TraceFilePath = trace.TraceFilePath;
            log.WriteLine($"[gc-pauses] Detailed capture written to {trace.TraceFilePath} ({trace.TraceSizeInBytes:N0} bytes); analysing.");

            Analyze(report, longPauseThresholdMSec, options, log);

            if (options.CollectGcDump)
            {
                await CollectHeapSnapshotAsync(introspector, processId, directory, stamp, options.GcDumpTopTypes, report, log).ConfigureAwait(false);
            }

            return report;
        }

        /// <summary>Analyses a .nettrace captured with GC events, e.g. one written by a detailed capture.</summary>
        internal static void Analyze(GcPauseDetailedReport report, double longPauseThresholdMSec, GcPauseDetailedCaptureOptions options, TextWriter log)
        {
            string path = report.TraceFilePath;
            List<GcPause> pauses = new();
            Dictionary<string, GcPinnedType> pinned = new(StringComparer.Ordinal);

            using (EventPipeEventSource source = new(path))
            {
                GcPauseTracker tracker = new(longPauseThresholdMSec, pauses.Add);
                tracker.Subscribe(source.Clr);
                source.Clr.GCPinObjectAtGCTime += data =>
                {
                    string name = string.IsNullOrEmpty(data.TypeName) ? "?" : data.TypeName;
                    if (!pinned.TryGetValue(name, out GcPinnedType entry))
                    {
                        entry = new GcPinnedType { TypeName = name };
                        pinned[name] = entry;
                    }
                    entry.Count++;
                    entry.Bytes += data.ObjectSize;
                };
                source.Process();
            }

            report.Pauses = pauses;
            report.PinnedTypes = pinned.Values.OrderByDescending(p => p.Count).ToList();

            if (options.VerboseGcEvents)
            {
                try
                {
                    report.Allocations = AllocationTracing.FromFile(path, 10, log);
                }
                catch (Exception ex)
                {
                    log.WriteLine($"[gc-pauses] Allocation analysis failed: {ex.Message}");
                }
            }

            if (options.CollectStacks)
            {
                report.TriggerSites = ResolveTriggerSites(path, pauses, log);
            }
        }

        // GCTriggered fires on the thread that asked for the GC, right before the runtime
        // suspends, so its call stack names the code responsible: a GC.Collect caller, or the
        // allocation that ran the budget out.
        private static IReadOnlyList<GcTriggerSite> ResolveTriggerSites(string tracePath, List<GcPause> pauses, TextWriter log)
        {
            const int MaxFrames = 24;
            string etlxPath = null;

            try
            {
                etlxPath = TraceLog.CreateFromEventPipeDataFile(tracePath);
                using TraceLog traceLog = new(etlxPath);

                Dictionary<string, (GcTriggerSite Site, List<int> Numbers)> sites = new(StringComparer.Ordinal);
                (string Key, GCReason Reason, List<string> Frames)? pending = null;

                foreach (TraceEvent data in traceLog.Events)
                {
                    if (data is GCTriggeredTraceData triggered)
                    {
                        TraceCallStack stack = data.CallStack();
                        if (stack is null)
                        {
                            pending = null;
                            continue;
                        }

                        List<string> frames = new(MaxFrames);
                        for (TraceCallStack frame = stack; frame is not null && frames.Count < MaxFrames; frame = frame.Caller)
                        {
                            string name = frame.CodeAddress.FullMethodName;
                            if (string.IsNullOrEmpty(name))
                            {
                                // Unresolved native runtime frames; collapse runs of them.
                                name = "?";
                                if (frames.Count > 0 && frames[^1] == "?")
                                {
                                    continue;
                                }
                            }
                            frames.Add(name);
                        }

                        pending = (triggered.Reason + "\n" + string.Join("\n", frames), triggered.Reason, frames);
                    }
                    else if (data is GCStartTraceData start && pending is { } p)
                    {
                        if (!sites.TryGetValue(p.Key, out var entry))
                        {
                            entry = (new GcTriggerSite { Reason = p.Reason, Frames = p.Frames }, new List<int>());
                            sites[p.Key] = entry;
                        }
                        entry.Numbers.Add(start.Count);
                        pending = null;
                    }
                }

                // Long pauses first: the stacks behind the pauses we care about matter most.
                HashSet<int> longGcs = new(pauses.Where(x => x.IsLong && x.Gc is not null).Select(x => x.Gc.Number));

                return sites.Values
                    .Select(e =>
                    {
                        e.Site.Count = e.Numbers.Count;
                        e.Site.GcNumbers = e.Numbers;
                        return e.Site;
                    })
                    .OrderByDescending(s => s.GcNumbers.Count(longGcs.Contains))
                    .ThenByDescending(s => s.Count)
                    .ToList();
            }
            catch (Exception ex)
            {
                log.WriteLine($"[gc-pauses] Failed to resolve GC trigger stacks: {ex.Message}");
                return Array.Empty<GcTriggerSite>();
            }
            finally
            {
                if (!string.IsNullOrEmpty(etlxPath))
                {
                    try { File.Delete(etlxPath); } catch { /* best-effort cleanup */ }
                }
            }
        }

        private static async Task CollectHeapSnapshotAsync(MemoryIntrospector introspector, int processId, string directory, string stamp, int topTypes, GcPauseDetailedReport report, TextWriter log)
        {
            try
            {
                // The snapshot walks the whole heap inside an induced, blocking gen2 GC; on a large
                // graph that pause can be longer than the one being investigated.
                log.WriteLine("[gc-pauses] Taking a heap snapshot (.gcdump); this induces a full blocking GC that walks the heap.");
                MemoryGraphResult graph = await introspector.CollectMemoryGraphAsync(processId).ConfigureAwait(false);
                if (graph?.Graph is null)
                {
                    log.WriteLine("[gc-pauses] Heap snapshot failed.");
                    return;
                }

                string dumpPath = Path.Combine(directory, $"gc-pauses-{processId}-{stamp}.gcdump");
                graph.SaveToDisk(dumpPath);
                report.GcDumpPath = dumpPath;

                MemoryGraph memoryGraph = graph.Graph;
                Dictionary<NodeTypeIndex, (long Count, long Bytes)> histogram = new();
                Node node = memoryGraph.AllocNodeStorage();
                for (NodeIndex i = 0; i < memoryGraph.NodeIndexLimit; i++)
                {
                    memoryGraph.GetNode(i, node);
                    histogram.TryGetValue(node.TypeIndex, out var entry);
                    histogram[node.TypeIndex] = (entry.Count + 1, entry.Bytes + node.Size);
                }

                NodeType type = memoryGraph.AllocTypeNodeStorage();
                report.HeapTypes = histogram
                    .OrderByDescending(kv => kv.Value.Bytes)
                    .Take(topTypes)
                    .Select(kv => new GcHeapType
                    {
                        TypeName = memoryGraph.GetType(kv.Key, type).Name,
                        Count = kv.Value.Count,
                        Bytes = kv.Value.Bytes,
                    })
                    .ToList();
                report.HeapObjectCount = histogram.Values.Sum(v => v.Count);
                report.HeapSnapshotTruncated = report.HeapObjectCount >= introspector.MaxNodeCount;
                report.HeapBytes = histogram.Values.Sum(v => v.Bytes);
            }
            catch (Exception ex)
            {
                log.WriteLine($"[gc-pauses] Heap snapshot failed: {ex.Message}");
            }
        }
    }
}
