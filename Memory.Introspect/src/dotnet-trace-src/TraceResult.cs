// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Memory.Introspect.Diagnostics.NETCore.Client;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// The outcome of a <c>dotnet-trace collect</c> equivalent capture.
    /// </summary>
    public sealed class TraceResult
    {
        /// <summary>True when trace data was captured.</summary>
        public bool Success { get; internal set; }

        /// <summary>True when the capture ended because the cancellation token fired.</summary>
        public bool Cancelled { get; internal set; }

        /// <summary>The failure, when the capture threw.</summary>
        public Exception Exception { get; internal set; }

        /// <summary>The process the trace was taken from.</summary>
        public int ProcessId { get; internal set; }

        /// <summary>The duration that was requested, if any.</summary>
        public TimeSpan? RequestedDuration { get; internal set; }

        /// <summary>How long the capture actually ran for.</summary>
        public TimeSpan Elapsed { get; internal set; }

        /// <summary>True when the capture ended because the configured stopping event was seen.</summary>
        public bool StoppedByStoppingEvent { get; internal set; }

        /// <summary>
        /// True when the configured stopping event payload filter named fields that the event
        /// does not have, meaning the stopping event could never be matched.
        /// </summary>
        public bool StoppingEventPayloadFilterMismatched { get; internal set; }

        /// <summary>The providers that were actually enabled for this session.</summary>
        public IReadOnlyList<EventPipeProvider> Providers { get; internal set; } = Array.Empty<EventPipeProvider>();

        /// <summary>The rundown keyword the session was started with (0 when rundown was off).</summary>
        public long RundownKeyword { get; internal set; }

        /// <summary>The runtime circular buffer size, in MB, used for this session.</summary>
        public int CircularBufferSizeInMB { get; internal set; }

        /// <summary>The .nettrace file that was written, when the capture streamed to disk.</summary>
        public string TraceFilePath { get; internal set; }

        /// <summary>The converted file that was written, when a non-nettrace format was requested.</summary>
        public string ConvertedFilePath { get; internal set; }

        /// <summary>
        /// The captured .nettrace bytes, when the capture was buffered in memory. Null when the
        /// trace was streamed to <see cref="TraceFilePath"/> — use that file instead, since a
        /// large capture is exactly the case where you don't want it all in memory.
        /// </summary>
        public byte[] NetTraceData { get; internal set; }

        /// <summary>How much trace data was captured.</summary>
        public long TraceSizeInBytes { get; internal set; }

        /// <summary>Writes the captured .nettrace data to <paramref name="fileName"/>.</summary>
        public void SaveToDisk(string fileName)
        {
            if (string.IsNullOrEmpty(fileName))
            {
                throw new ArgumentNullException(nameof(fileName));
            }

            if (NetTraceData is { Length: > 0 })
            {
                File.WriteAllBytes(fileName, NetTraceData);
                return;
            }

            if (!string.IsNullOrEmpty(TraceFilePath) && File.Exists(TraceFilePath))
            {
                if (!string.Equals(Path.GetFullPath(TraceFilePath), Path.GetFullPath(fileName), StringComparison.Ordinal))
                {
                    File.Copy(TraceFilePath, fileName, overwrite: true);
                }
                return;
            }

            throw new InvalidOperationException("No trace data was captured.");
        }

        /// <summary>
        /// Converts the captured trace to <paramref name="format"/>, the equivalent of
        /// <c>dotnet-trace convert</c>, and returns the path that was written.
        /// </summary>
        /// <param name="format">The target format.</param>
        /// <param name="outputPath">Where to write. When null it is derived from the .nettrace path.</param>
        /// <param name="log">Optional log.</param>
        public string ConvertTo(TraceFileFormat format, string outputPath = null, TextWriter log = null)
        {
            if (format == TraceFileFormat.NetTrace)
            {
                if (string.IsNullOrEmpty(outputPath))
                {
                    return TraceFilePath;
                }
                SaveToDisk(outputPath);
                return Path.GetFullPath(outputPath);
            }

            return WithTraceFile(traceFile =>
            {
                string resolved = TraceFileFormatConverter.GetConvertedFilename(traceFile, outputPath, format);
                TraceFileFormatConverter.ConvertToFormat(format, traceFile, resolved, log);
                return Path.GetFullPath(resolved);
            }, requireStableName: string.IsNullOrEmpty(outputPath));
        }

        /// <summary>
        /// Computes the top <paramref name="count"/> methods of this trace, the equivalent of
        /// <c>dotnet-trace report topN</c>. Only meaningful when the trace contains sample
        /// profiler events (the "dotnet-sampled-thread-time" profile).
        /// </summary>
        /// <param name="count">How many methods to return.</param>
        /// <param name="inclusive">Rank by inclusive rather than exclusive time.</param>
        /// <param name="excludedModules">Assembly simple names to hide from the report.</param>
        /// <param name="blockingMethodPatterns">Regex patterns identifying blocking waits; any
        /// sample whose stack contains a matching frame is dropped.</param>
        /// <param name="log">Optional log.</param>
        public IReadOnlyList<SampledMethod> TopMethods(
            int count = 5,
            bool inclusive = false,
            IEnumerable<string> excludedModules = null,
            IEnumerable<string> blockingMethodPatterns = null,
            TextWriter log = null)
        {
            IReadOnlyList<string> modules = excludedModules is null ? null : (excludedModules as IReadOnlyList<string>) ?? excludedModules.ToList();
            IReadOnlyList<string> blocking = blockingMethodPatterns is null ? null : (blockingMethodPatterns as IReadOnlyList<string>) ?? blockingMethodPatterns.ToList();

            return WithTraceFile(traceFile => TraceReport.TopMethodsFromFile(traceFile, count, inclusive, modules, blocking, log), requireStableName: false);
        }

        /// <summary>
        /// Writes a `dotnet-trace report topN` style table for this trace.
        /// </summary>
        public void WriteTopMethodsReport(TextWriter output, int count = 5, bool inclusive = false, bool verbose = false,
            IEnumerable<string> excludedModules = null, IEnumerable<string> blockingMethodPatterns = null)
        {
            TraceReport.WriteTopMethodsReport(output, TopMethods(count, inclusive, excludedModules, blockingMethodPatterns), inclusive, verbose);
        }

        /// <summary>
        /// Reports which objects were allocated during the traced interval and how many bytes
        /// went to each type, ordered by allocated bytes descending.
        /// </summary>
        /// <param name="count">How many types to keep.</param>
        /// <param name="log">Optional log.</param>
        /// <remarks>
        /// Requires the capture to have enabled <see cref="AllocationTracing.RequiredClrEvents"/>
        /// at <see cref="AllocationTracing.RequiredClrEventLevel"/> — use
        /// <see cref="AllocationTracing.CreateOptions"/> to get that configuration. When the
        /// trace has no allocation events the returned report is
        /// <see cref="AllocationReport.IsEmpty"/> rather than an error.
        /// </remarks>
        public AllocationReport TopAllocatedTypes(int count = 10, TextWriter log = null)
        {
            return WithTraceFile(traceFile => AllocationTracing.FromFile(traceFile, count, log), requireStableName: false);
        }

        /// <summary>
        /// Writes a per-type allocation table for this trace. See <see cref="TopAllocatedTypes"/>
        /// for what the capture needs to have enabled.
        /// </summary>
        public void WriteAllocationReport(TextWriter output, int count = 10, bool verbose = false)
        {
            AllocationTracing.Write(output, TopAllocatedTypes(count), verbose);
        }

        // Runs an action against a .nettrace file on disk, materialising an in-memory capture to
        // a temporary file first (and cleaning it up afterwards) when that is what we have.
        private T WithTraceFile<T>(Func<string, T> action, bool requireStableName)
        {
            if (!string.IsNullOrEmpty(TraceFilePath) && File.Exists(TraceFilePath))
            {
                return action(TraceFilePath);
            }

            if (NetTraceData is not { Length: > 0 })
            {
                throw new InvalidOperationException("No trace data was captured.");
            }

            if (requireStableName)
            {
                // The caller wants the output named after the .nettrace file, but there isn't one:
                // deriving it from a temporary file would put the result somewhere unusable.
                throw new InvalidOperationException(
                    "An output path is required to convert a trace that was captured in memory. Pass an explicit outputPath, or set TraceCollectionOptions.OutputPath when collecting.");
            }

            string tempFile = Path.Combine(Path.GetTempPath(), $"memory-introspect-trace-{Guid.NewGuid():N}.nettrace");
            File.WriteAllBytes(tempFile, NetTraceData);
            try
            {
                return action(tempFile);
            }
            finally
            {
                try { File.Delete(tempFile); } catch { /* best-effort cleanup */ }
            }
        }
    }
}
