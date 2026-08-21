using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Graphs;
using Memory.Introspect.Trace;
using Microsoft.Diagnostics.Tools.GCDump;
using Microsoft.Extensions.Logging;

namespace Memory.Introspect
{
    public sealed class MemoryIntrospector
    {
        private readonly MemoryIntrospectorOptions _options;

        public Task<int> DumpAsync(int pid, string targetPath, Memory.Introspect.Dumper.CollectionType collectionType)
        {
            var task = Task.Run(() =>
            {
                var dumper = new Memory.Introspect.Dumper();
                var stdOut = GetTextWriter();
                var stdErr = GetTextWriter();

                return dumper.Collect(stdOut, stdErr, pid, targetPath, _options.Verbose, false, collectionType, null, _options.DiagnosticPort);
            });

            return task;
        }

        private MemoryIntrospector(MemoryIntrospectorOptions options)
        {
            _options = options;
        }

        public static MemoryIntrospector Create(MemoryIntrospectorOptions options = null)
        {
            options ??= new();

            if (options.Timeout.TotalSeconds < 30)
            {
                options.Timeout = TimeSpan.FromSeconds(30);
            }

            return new MemoryIntrospector(options);
        }

        public Task<SamplingProfileResult> CollectSamplingProfileAsync(int processId, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
            }

            return SamplingProfiler.CollectAsync(
                processId,
                duration,
                _options.CircularBufferSizeInMB,
                _options.DiagnosticPort,
                _options.SamplingExcludedModules,
                _options.SamplingBlockingMethodPatterns,
                GetTextWriter(),
                cancellationToken);
        }

        /// <summary>
        /// Collects an EventPipe trace from <paramref name="processId"/> for
        /// <paramref name="duration"/> using the default profiles ("dotnet-common" +
        /// "dotnet-sampled-thread-time"), buffering it in memory. The programmatic equivalent of
        /// <c>dotnet-trace collect --duration ...</c>.
        /// </summary>
        public Task<TraceResult> CollectTraceAsync(int processId, TimeSpan duration, CancellationToken cancellationToken = default)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
            }

            return CollectTraceAsync(processId, new TraceCollectionOptions { Duration = duration }, cancellationToken);
        }

        /// <summary>
        /// Collects an EventPipe trace from <paramref name="processId"/>. The programmatic
        /// equivalent of <c>dotnet-trace collect</c>: providers, profiles, CLR event keywords,
        /// rundown control, stopping events, buffer sizing and format conversion are all
        /// configured through <paramref name="options"/>.
        /// </summary>
        /// <remarks>
        /// Set <see cref="TraceCollectionOptions.OutputPath"/> to stream straight to disk, and
        /// raise <see cref="TraceCollectionOptions.CircularBufferSizeInMB"/> (it defaults to
        /// <see cref="MemoryIntrospectorOptions.CircularBufferSizeInMB"/>) when tracing a very
        /// large or very chatty process, otherwise the runtime will drop events.
        /// </remarks>
        public Task<TraceResult> CollectTraceAsync(int processId, TraceCollectionOptions options, CancellationToken cancellationToken = default)
        {
            return TraceCollector.CollectAsync(
                processId,
                options ?? new TraceCollectionOptions(),
                _options.DiagnosticPort,
                _options.CircularBufferSizeInMB,
                GetTextWriter(),
                cancellationToken);
        }

        /// <summary>
        /// The built-in tracing profiles, the equivalent of <c>dotnet-trace list-profiles</c>.
        /// </summary>
        public static IReadOnlyList<TraceProfile> ListTraceProfiles() => TraceProfiles.All;

        /// <summary>
        /// Converts a .nettrace file to another format, the equivalent of
        /// <c>dotnet-trace convert</c>. Returns the path that was written.
        /// </summary>
        /// <param name="traceFilePath">The .nettrace file to convert.</param>
        /// <param name="format">The target format.</param>
        /// <param name="outputPath">Where to write. When null it is derived from the input path.</param>
        public string ConvertTraceFile(string traceFilePath, TraceFileFormat format, string outputPath = null)
        {
            if (string.IsNullOrEmpty(traceFilePath))
            {
                throw new ArgumentNullException(nameof(traceFilePath));
            }

            if (!File.Exists(traceFilePath))
            {
                throw new FileNotFoundException("Trace file not found.", traceFilePath);
            }

            string resolved = TraceFileFormatConverter.GetConvertedFilename(traceFilePath, outputPath, format);
            TraceFileFormatConverter.ConvertToFormat(format, traceFilePath, resolved, GetTextWriter());
            return Path.GetFullPath(resolved);
        }

        /// <summary>
        /// Computes the top N methods of a previously captured .nettrace file, the equivalent of
        /// <c>dotnet-trace report topN</c>.
        /// </summary>
        public IReadOnlyList<SampledMethod> ReportTopMethods(
            string traceFilePath,
            int count = 5,
            bool inclusive = false,
            IEnumerable<string> excludedModules = null,
            IEnumerable<string> blockingMethodPatterns = null)
        {
            IReadOnlyList<string> modules = excludedModules is null ? null : (excludedModules as IReadOnlyList<string>) ?? new List<string>(excludedModules);
            IReadOnlyList<string> blocking = blockingMethodPatterns is null ? null : (blockingMethodPatterns as IReadOnlyList<string>) ?? new List<string>(blockingMethodPatterns);
            return TraceReport.TopMethodsFromFile(traceFilePath, count, inclusive, modules, blocking, GetTextWriter());
        }

        /// <summary>
        /// Collects a trace configured for allocation profiling and reports which objects the
        /// process allocated over <paramref name="duration"/>, ordered by allocated bytes.
        /// </summary>
        /// <param name="processId">The process to trace.</param>
        /// <param name="duration">How long to record for.</param>
        /// <param name="count">How many types to report.</param>
        /// <param name="outputPath">Optionally keep the underlying .nettrace at this path.</param>
        /// <param name="cancellationToken">Cancels the capture, keeping whatever was collected.</param>
        /// <remarks>
        /// Allocation tracing is verbose — an allocation-heavy process can emit tens of MB of
        /// events per second — so prefer short intervals, and raise
        /// <see cref="MemoryIntrospectorOptions.CircularBufferSizeInMB"/> if events are dropped.
        /// </remarks>
        public async Task<AllocationReport> CollectAllocationReportAsync(
            int processId,
            TimeSpan duration,
            int count = 10,
            string outputPath = null,
            CancellationToken cancellationToken = default)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
            }

            TraceResult trace = await CollectTraceAsync(processId, AllocationTracing.CreateOptions(duration, outputPath), cancellationToken).ConfigureAwait(false);

            if (trace.Exception is not null)
            {
                throw trace.Exception;
            }

            if (!trace.Success)
            {
                return new AllocationReport();
            }

            return trace.TopAllocatedTypes(count, GetTextWriter());
        }

        /// <summary>
        /// Collects an allocation report that also resolves the call stacks the allocations came
        /// from, answering "where in the code did these bytes come from".
        /// </summary>
        /// <remarks>
        /// This costs more than <see cref="CollectAllocationReportAsync(int, TimeSpan, int, string, CancellationToken)"/>
        /// at both ends: the capture turns rundown on so jitted frames can be named, which makes
        /// stopping slower and the trace larger, and the analysis converts the trace to ETLX,
        /// which is slow on a large capture. Prefer short durations.
        /// </remarks>
        public async Task<AllocationReport> CollectAllocationReportAsync(
            int processId,
            TimeSpan duration,
            int count,
            string outputPath,
            bool resolveCallStacks,
            CancellationToken cancellationToken = default)
        {
            if (duration <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(nameof(duration), "Duration must be positive.");
            }

            TraceResult trace = await CollectTraceAsync(
                processId,
                AllocationTracing.CreateOptions(duration, outputPath, resolveCallStacks),
                cancellationToken).ConfigureAwait(false);

            if (trace.Exception is not null)
            {
                throw trace.Exception;
            }

            if (!trace.Success)
            {
                return new AllocationReport();
            }

            return trace.TopAllocatedTypes(count, GetTextWriter(), resolveCallStacks);
        }

        /// <summary>
        /// Reports the top allocated types of a previously captured .nettrace file.
        /// </summary>
        /// <param name="traceFilePath">The .nettrace file to analyse.</param>
        /// <param name="count">How many types — and, when resolved, call stacks — to report.</param>
        /// <param name="resolveCallStacks">
        /// Also report the call stacks the allocations came from. Only produces named frames if
        /// the trace was captured with rundown.
        /// </param>
        public AllocationReport ReportTopAllocatedTypes(string traceFilePath, int count = 10, bool resolveCallStacks = false)
        {
            return AllocationTracing.FromFile(traceFilePath, count, GetTextWriter(), resolveCallStacks);
        }

        /// <summary>
        /// The process ids of all .NET processes on this machine that publish a diagnostics
        /// endpoint and can therefore be traced or dumped. The equivalent of
        /// <c>dotnet-trace ps</c>.
        /// </summary>
        public static IReadOnlyList<int> GetTraceableProcesses()
        {
            return new List<int>(Memory.Introspect.Diagnostics.NETCore.Client.DiagnosticsClient.GetPublishedProcesses());
        }

        public async Task<MemoryGraphResult> CollectMemoryGraphAsync(int processId, CancellationToken cancellationToken = default)
        {
            DotNetHeapInfo heapInfo = new();

            var memoryGraph = new MemoryGraph(50_000, isVeryLargeGraph: _options.ExpectLargeGraph);

            var response = new MemoryGraphResult()
            {
                Graph = memoryGraph,
            };

            var task = Task.Run(async () =>
            {
                await Task.Yield();

                if (!EventPipeDotNetHeapDumper.DumpFromEventPipe(cancellationToken, processId, _options.DiagnosticPort, memoryGraph, GetTextWriter(), (int)_options.Timeout.TotalSeconds, heapInfo, _options.MaxNodeCount, _options.CircularBufferSizeInMB, response))
                {
                    memoryGraph = null;
                }
            });

            await task;

            if (memoryGraph is null) return MemoryGraphResult.Fail();

            memoryGraph.AllowReading();

            return response;
        }

        private TextWriter GetTextWriter()
        {
            if (_options.Logger is null) return TextWriter.Null;
            return new LoggerTextWriter(_options.Logger, _options.LogLevel);
        }
    }

    internal class LoggerTextWriter : TextWriter
    {
        private readonly ILogger _logger;
        private readonly LogLevel _logLevel;

        public LoggerTextWriter(ILogger logger, LogLevel logLevel)
        {
            _logger = logger;
            _logLevel = logLevel;
        }

        public override void WriteLine(string line) //Only write line is used, so we can get by by just overriding it
        {
            _logger.Log(_logLevel, line);
        }

        public override Encoding Encoding => Encoding.UTF8;
    }

    public class MemoryIntrospectorOptions
    {
        public string DiagnosticPort { get; set; }
        public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(30);
        public bool Verbose { get; set; } = true;
        public bool ExpectLargeGraph { get; set; } = false;
        public int MaxNodeCount { get; set; } = 10_000_000;
        public int CircularBufferSizeInMB { get; set; } = 1024;
        public ILogger Logger { get; set;  }
        public LogLevel LogLevel { get; set; } = LogLevel.Information;

        /// <summary>
        /// Module names (assembly simple names) whose frames are excluded from
        /// <see cref="SamplingProfileResult.TopMethods"/> by default. Useful when running
        /// the sampling profile against the current process so that the library's own
        /// frames don't pollute the report. Set to an empty list to disable the default
        /// filtering.
        /// </summary>
        public IReadOnlyList<string> SamplingExcludedModules { get; set; } = SamplingProfiler.DefaultExcludedModules;

        /// <summary>
        /// Regex patterns that identify methods which park a thread in a blocking wait
        /// (locks, ManualResetEventSlim.Wait, Monitor.Wait, Task.Wait, etc.). Any sample
        /// whose stack contains a matching frame is dropped from
        /// <see cref="SamplingProfileResult.TopMethods"/>, so threads sitting in those
        /// waits do not show up as hot CPU methods. Set to an empty list to disable
        /// blocked-thread filtering entirely.
        /// </summary>
        public IReadOnlyList<string> SamplingBlockingMethodPatterns { get; set; } = SamplingProfiler.DefaultBlockingMethodPatterns;
    }

    public class MemoryGraphResult
    {
        public bool Success { get; internal set;  }
        public bool Timeouted { get; internal set;  }
        public MemoryGraph Graph { get; internal set;  }
        public bool Cancelled { get; internal set; }
        public bool NoHeapFound { get; internal set; }
        public Exception Exception { get; internal set; }

        internal static MemoryGraphResult Fail()
        {
            return new MemoryGraphResult()
            {
                Success = false
            };
        }

        public void SaveToDisk(string fileName)
        {
            GCHeapDump.WriteMemoryGraph(Graph, fileName, "dotnet-gcdump");
        }
    }
}
