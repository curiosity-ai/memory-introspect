// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// A per-type allocation report built from the CLR's allocation sampling events. This is the
// programmatic form of what PerfView shows in its "GC Heap Alloc Ignore Free" view; there is
// no `dotnet-trace report` subcommand for it, so this is an addition on top of the ported
// dotnet-trace surface rather than an adaptation of it.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers.Clr;

namespace Memory.Introspect.Trace
{
    /// <summary>Which CLR event the allocation numbers were derived from.</summary>
    public enum AllocationSampleSource
    {
        /// <summary>No allocation events were present in the trace.</summary>
        None = 0,

        /// <summary>
        /// <c>GCAllocationTick</c>: the runtime emits one event per ~100 KB allocated, carrying
        /// the type of the object that crossed the threshold. Gives accurate allocated-bytes
        /// totals per type; object counts are not available.
        /// </summary>
        AllocationTick = 1,

        /// <summary>
        /// <c>GCSampledObjectAllocation</c>: per-object allocation sampling, which also yields
        /// object counts. Not emitted by all runtime versions.
        /// </summary>
        SampledObjectAllocation = 2,
    }

    /// <summary>One type's share of the allocations observed during a trace.</summary>
    public sealed class AllocatedType
    {
        /// <summary>The allocated type's name, e.g. <c>System.Byte[]</c>.</summary>
        public string TypeName { get; internal set; }

        /// <summary>Total bytes attributed to this type over the traced interval.</summary>
        public long AllocatedBytes { get; internal set; }

        /// <summary>This type's share of all allocated bytes, as a percentage.</summary>
        public double AllocatedBytesPercent { get; internal set; }

        /// <summary>How many allocation sampling events were attributed to this type.</summary>
        public long SampleCount { get; internal set; }

        /// <summary>
        /// Objects allocated, when the trace carries per-object samples
        /// (<see cref="AllocationSampleSource.SampledObjectAllocation"/>); 0 otherwise.
        /// </summary>
        public long ObjectCount { get; internal set; }

        /// <summary>Bytes allocated on the small object heap.</summary>
        public long SmallObjectHeapBytes { get; internal set; }

        /// <summary>
        /// Bytes allocated on the large object heap. A type showing up here is allocating
        /// objects over the 85,000 byte LOH threshold.
        /// </summary>
        public long LargeObjectHeapBytes { get; internal set; }

        public override string ToString() => $"{TypeName}: {AllocatedBytes:N0} bytes ({AllocatedBytesPercent:0.##}%)";
    }

    /// <summary>
    /// A per-type breakdown of what a process allocated during a traced interval.
    /// </summary>
    public sealed class AllocationReport
    {
        /// <summary>The types, ordered by allocated bytes descending.</summary>
        public IReadOnlyList<AllocatedType> Types { get; internal set; } = Array.Empty<AllocatedType>();

        /// <summary>Total bytes allocated across all types, including ones trimmed from <see cref="Types"/>.</summary>
        public long TotalAllocatedBytes { get; internal set; }

        /// <summary>How many distinct types were observed allocating, before any top-N trim.</summary>
        public int DistinctTypeCount { get; internal set; }

        /// <summary>How many allocation sampling events the report is based on.</summary>
        public long SampleCount { get; internal set; }

        /// <summary>Which CLR event supplied the numbers.</summary>
        public AllocationSampleSource Source { get; internal set; }

        /// <summary>
        /// True when the trace contained no allocation events at all — usually because the
        /// capture did not enable <see cref="AllocationTracing.RequiredClrEvents"/> at
        /// <see cref="AllocationTracing.RequiredClrEventLevel"/>.
        /// </summary>
        public bool IsEmpty => Source == AllocationSampleSource.None || Types.Count == 0;
    }

    /// <summary>
    /// Captures and analyses per-type allocation data: which objects a process allocated over a
    /// traced interval, and how many bytes went to each.
    /// </summary>
    public static class AllocationTracing
    {
        /// <summary>
        /// The CLR event keywords a capture must enable for <see cref="FromFile"/> to have
        /// anything to report. <c>Gc</c> produces the allocation events themselves; <c>Type</c>
        /// and <c>GcHeapAndTypeNames</c> supply the bookkeeping that turns type ids into names.
        /// </summary>
        public const ClrEventKeywords RequiredClrEvents =
            ClrEventKeywords.Gc | ClrEventKeywords.Type | ClrEventKeywords.GcHeapAndTypeNames;

        /// <summary>
        /// Allocation events are Verbose-level; at Informational the runtime emits GC
        /// collection events but no allocation ticks.
        /// </summary>
        public const EventLevel RequiredClrEventLevel = EventLevel.Verbose;

        /// <summary>
        /// Builds a <see cref="TraceCollectionOptions"/> preconfigured to capture per-type
        /// allocation data, the input <see cref="FromFile"/> expects.
        /// </summary>
        /// <param name="duration">How long to record for.</param>
        /// <param name="outputPath">Where to stream the .nettrace. Null buffers it in memory.</param>
        /// <remarks>
        /// Allocation tracing is verbose: an allocation-heavy process can produce tens of MB per
        /// second. Rundown is left off because an allocation report resolves type names from the
        /// events themselves and does not need jitted method symbols.
        /// </remarks>
        public static TraceCollectionOptions CreateOptions(TimeSpan duration, string outputPath = null)
        {
            return new TraceCollectionOptions
            {
                Duration = duration,
                ClrEvents = RequiredClrEvents,
                ClrEventLevel = RequiredClrEventLevel,
                OutputPath = outputPath,
                Rundown = false,
            };
        }

        /// <summary>
        /// Reads the allocation events out of a .nettrace file and reports the top
        /// <paramref name="count"/> allocated types.
        /// </summary>
        /// <param name="traceFilePath">The .nettrace file to analyse.</param>
        /// <param name="count">How many types to keep. Pass <see cref="int.MaxValue"/> for all of them.</param>
        /// <param name="log">Optional log.</param>
        public static AllocationReport FromFile(string traceFilePath, int count = 10, TextWriter log = null)
        {
            log ??= TextWriter.Null;

            if (string.IsNullOrEmpty(traceFilePath) || !File.Exists(traceFilePath))
            {
                throw new FileNotFoundException("Trace file not found.", traceFilePath);
            }

            Dictionary<ulong, string> typeNames = new();
            Dictionary<string, AllocatedType> ticks = new(StringComparer.Ordinal);
            Dictionary<string, AllocatedType> sampled = new(StringComparer.Ordinal);
            long tickEvents = 0;
            long sampledEvents = 0;

            // EventPipeEventSource reads the nettrace stream directly. Unlike the top-N method
            // report there is no need to convert to ETLX first: allocation events carry their own
            // type information, so no symbol or stack resolution is involved.
            using (EventPipeEventSource source = new(traceFilePath))
            {
                source.Clr.TypeBulkType += data =>
                {
                    for (int i = 0; i < data.Count; i++)
                    {
                        GCBulkTypeValues value = data.Values(i);
                        typeNames[value.TypeID] = value.TypeName;
                    }
                };

                source.Clr.GCAllocationTick += data =>
                {
                    tickEvents++;
                    string name = !string.IsNullOrEmpty(data.TypeName)
                        ? data.TypeName
                        : ResolveTypeName(typeNames, (ulong)data.TypeID);

                    AllocatedType entry = GetOrAdd(ticks, name);
                    long amount = data.AllocationAmount64;
                    entry.AllocatedBytes += amount;
                    entry.SampleCount++;
                    if (data.AllocationKind == GCAllocationKind.Large)
                    {
                        entry.LargeObjectHeapBytes += amount;
                    }
                    else
                    {
                        entry.SmallObjectHeapBytes += amount;
                    }
                };

                source.Clr.GCSampledObjectAllocation += data =>
                {
                    sampledEvents++;
                    string name = ResolveTypeName(typeNames, (ulong)data.TypeID);

                    AllocatedType entry = GetOrAdd(sampled, name);
                    entry.AllocatedBytes += data.TotalSizeForTypeSample;
                    entry.ObjectCount += data.ObjectCountForTypeSample;
                    entry.SampleCount++;
                    entry.SmallObjectHeapBytes += data.TotalSizeForTypeSample;
                };

                source.Process();
            }

            // Prefer per-object sampling when the runtime emitted it, since it also gives object
            // counts; otherwise fall back to the allocation ticks, which every runtime emits.
            Dictionary<string, AllocatedType> chosen;
            AllocationSampleSource chosenSource;
            long chosenEvents;

            if (sampled.Count > 0)
            {
                chosen = sampled;
                chosenSource = AllocationSampleSource.SampledObjectAllocation;
                chosenEvents = sampledEvents;
            }
            else if (ticks.Count > 0)
            {
                chosen = ticks;
                chosenSource = AllocationSampleSource.AllocationTick;
                chosenEvents = tickEvents;
            }
            else
            {
                log.WriteLine("[alloc] No allocation events found in the trace. Was it captured with AllocationTracing.RequiredClrEvents at Verbose level?");
                return new AllocationReport { Source = AllocationSampleSource.None };
            }

            long total = chosen.Values.Sum(t => t.AllocatedBytes);
            foreach (AllocatedType entry in chosen.Values)
            {
                entry.AllocatedBytesPercent = total > 0 ? entry.AllocatedBytes * 100.0 / total : 0;
            }

            List<AllocatedType> top = chosen.Values
                .OrderByDescending(t => t.AllocatedBytes)
                .Take(count < 0 ? 0 : count)
                .ToList();

            log.WriteLine($"[alloc] {chosenEvents:N0} {chosenSource} events over {chosen.Count:N0} types, {total:N0} bytes total");

            return new AllocationReport
            {
                Types = top,
                TotalAllocatedBytes = total,
                DistinctTypeCount = chosen.Count,
                SampleCount = chosenEvents,
                Source = chosenSource,
            };
        }

        /// <summary>
        /// Writes <paramref name="report"/> as a table, in the same spirit as the top-N method
        /// report.
        /// </summary>
        /// <param name="output">Where to write the table.</param>
        /// <param name="report">The report to render.</param>
        /// <param name="verbose">Print full type names instead of truncating them.</param>
        public static void Write(TextWriter output, AllocationReport report, bool verbose = false)
        {
            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            if (report is null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            if (report.IsEmpty)
            {
                output.WriteLine("[WARNING] No allocation events found. Capture with AllocationTracing.CreateOptions(...) to collect them.");
                return;
            }

            const int typeColumnWidth = 64;

            output.WriteLine($"Top {report.Types.Count} Allocated Types of {report.DistinctTypeCount} " +
                             $"({FormatBytes(report.TotalAllocatedBytes)} total, {report.SampleCount:N0} {report.Source} events)");
            output.WriteLine(
                Pad("Type", typeColumnWidth) +
                PadLeft("Bytes", 14) +
                PadLeft("%", 9) +
                PadLeft("LOH", 14) +
                PadLeft("Objects", 12));

            int rank = 1;
            foreach (AllocatedType type in report.Types)
            {
                string name = $"{rank++}. {type.TypeName}";
                string firstColumn = verbose ? name : Truncate(name, typeColumnWidth - 1);

                output.WriteLine(
                    Pad(firstColumn, typeColumnWidth) +
                    PadLeft(FormatBytes(type.AllocatedBytes), 14) +
                    PadLeft($"{type.AllocatedBytesPercent:0.##}%", 9) +
                    PadLeft(type.LargeObjectHeapBytes > 0 ? FormatBytes(type.LargeObjectHeapBytes) : "-", 14) +
                    PadLeft(type.ObjectCount > 0 ? $"{type.ObjectCount:N0}" : "-", 12));
            }
        }

        private static AllocatedType GetOrAdd(Dictionary<string, AllocatedType> map, string typeName)
        {
            if (!map.TryGetValue(typeName, out AllocatedType entry))
            {
                entry = new AllocatedType { TypeName = typeName };
                map[typeName] = entry;
            }
            return entry;
        }

        private static string ResolveTypeName(Dictionary<ulong, string> typeNames, ulong typeId) =>
            typeNames.TryGetValue(typeId, out string name) && !string.IsNullOrEmpty(name)
                ? name
                : $"TypeID 0x{typeId:X}";

        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024) { return $"{bytes / (1024.0 * 1024 * 1024):0.00} GiB"; }
            if (bytes >= 1024L * 1024) { return $"{bytes / (1024.0 * 1024):0.00} MiB"; }
            if (bytes >= 1024L) { return $"{bytes / 1024.0:0.00} KiB"; }
            return $"{bytes} B";
        }

        private static string Truncate(string text, int width) =>
            text.Length <= width ? text : text.Substring(0, width - 1) + "…";

        private static string Pad(string text, int width) =>
            text.Length >= width ? text + " " : text + new string(' ', width - text.Length);

        private static string PadLeft(string text, int width) =>
            text.Length >= width ? " " + text : new string(' ', width - text.Length) + text;
    }
}
