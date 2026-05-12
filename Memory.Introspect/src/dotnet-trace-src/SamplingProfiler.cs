// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/tree/main/src/Tools/dotnet-trace
// CollectCommand + ReportCommand logic, distilled to a single sampling profile
// (Microsoft-DotNETCore-SampleProfiler) and a top-N method report.

using System;
using System.Collections.Generic;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Diagnostics.Tracing.StackSources;
using Memory.Introspect.Diagnostics.NETCore.Client;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace Memory.Introspect.Trace
{
    internal static class SamplingProfiler
    {
        public const string SampleProfilerProviderName = "Microsoft-DotNETCore-SampleProfiler";

        public static async Task<SamplingProfileResult> CollectAsync(
            int processId,
            TimeSpan duration,
            int circularBufferSizeInMB,
            string diagnosticPort,
            IReadOnlyList<string> defaultExcludedModules,
            TextWriter log,
            CancellationToken cancellationToken)
        {
            log ??= TextWriter.Null;

            var result = new SamplingProfileResult
            {
                ProcessId = processId,
                Duration = duration,
                DefaultExcludedModules = defaultExcludedModules ?? DefaultExcludedModules,
            };

            var providers = new List<EventPipeProvider>
            {
                new EventPipeProvider(SampleProfilerProviderName, EventLevel.Informational),
            };

            try
            {
                DiagnosticsClient client = CreateClient(processId, diagnosticPort);

                log.WriteLine($"[sampling] Starting EventPipe session against pid {processId} for {duration.TotalSeconds:0.##}s");
                EventPipeSession session = await client.StartEventPipeSessionAsync(providers, requestRundown: true, circularBufferSizeInMB, cancellationToken).ConfigureAwait(false);

                using var memoryStream = new MemoryStream();
                using (session)
                {
                    Task copyTask = session.EventStream.CopyToAsync(memoryStream, cancellationToken);

                    try
                    {
                        await Task.Delay(duration, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Fall through to stop the session below so we still return any data we have.
                    }

                    log.WriteLine("[sampling] Stopping EventPipe session");
                    try
                    {
                        await session.StopAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        log.WriteLine($"[sampling] StopAsync threw: {ex.Message}");
                    }

                    try
                    {
                        await copyTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                result.NetTraceData = memoryStream.ToArray();
                result.Success = result.NetTraceData.Length > 0;
                result.Cancelled = cancellationToken.IsCancellationRequested;
                log.WriteLine($"[sampling] Captured {result.NetTraceData.Length:N0} bytes of nettrace data");
            }
            catch (OperationCanceledException)
            {
                result.Cancelled = true;
            }
            catch (Exception ex)
            {
                result.Exception = ex;
                log.WriteLine($"[sampling] Failed: {ex}");
            }

            return result;
        }

        private static DiagnosticsClient CreateClient(int processId, string diagnosticPort)
        {
            if (!string.IsNullOrEmpty(diagnosticPort))
            {
                IpcEndpointConfig endpoint = IpcEndpointConfig.Parse(diagnosticPort);
                return new DiagnosticsClient(endpoint);
            }
            return new DiagnosticsClient(processId);
        }

        // Module names that always get excluded from the top-N report. The frame name format
        // produced by SampleProfilerThreadTimeComputer is "Module!Namespace.Type.Method(args)",
        // so excluding "Memory.Introspect" filters out this library's own frames — which is
        // particularly useful when sampling the current process.
        public static readonly IReadOnlyList<string> DefaultExcludedModules = new[]
        {
            "Memory.Introspect",
        };

        public static IReadOnlyList<SampledMethod> ComputeTopMethods(
            byte[] netTraceData,
            int count,
            bool inclusive,
            IReadOnlyList<string> excludedModules,
            TextWriter log)
        {
            log ??= TextWriter.Null;
            if (netTraceData is null || netTraceData.Length == 0)
            {
                return Array.Empty<SampledMethod>();
            }

            // Build module prefixes once so we can do a cheap StartsWith check per frame.
            string[] modulePrefixes = (excludedModules ?? DefaultExcludedModules)
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.EndsWith("!") ? m : m + "!")
                .ToArray();

            string traceFile = Path.Combine(Path.GetTempPath(), $"memory-introspect-sample-{Guid.NewGuid():N}.nettrace");
            File.WriteAllBytes(traceFile, netTraceData);

            string etlxFile = null;
            try
            {
                etlxFile = TraceLog.CreateFromEventPipeDataFile(traceFile);

                using SymbolReader symbolReader = new(log) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath };
                using TraceLog eventLog = new(etlxFile);

                MutableTraceEventStackSource stackSource = new(eventLog) { OnlyManagedCodeStacks = true };

                SampleProfilerThreadTimeComputer computer = new(eventLog, symbolReader);
                computer.GenerateThreadTimeStacks(stackSource);

                FilterParams filterParams = new()
                {
                    FoldRegExs = "CPU_TIME;UNMANAGED_CODE_TIME;{Thread (}",
                };
                FilterStackSource filterStack = new(filterParams, stackSource, ScalingPolicyKind.ScaleToData);
                CallTree callTree = new(ScalingPolicyKind.ScaleToData) { StackSource = filterStack };

                List<CallTreeNodeBase> nodes = inclusive
                    ? callTree.ByID.OrderByDescending(n => Math.Abs(n.InclusiveMetric)).ToList()
                    : callTree.ByIDSortedExclusiveMetric();

                var unwanted = new[] { "ROOT", "Process" };

                var output = new List<SampledMethod>(count);
                foreach (CallTreeNodeBase node in nodes)
                {
                    if (output.Count >= count) break;
                    string name = node.Name;
                    if (string.IsNullOrEmpty(name)) continue;
                    if (unwanted.Any(u => name.Contains(u))) continue;
                    if (IsFromExcludedModule(name, modulePrefixes)) continue;

                    output.Add(new SampledMethod
                    {
                        Name = name,
                        InclusiveMetric = node.InclusiveMetric,
                        ExclusiveMetric = node.ExclusiveMetric,
                        InclusiveMetricPercent = node.InclusiveMetricPercent,
                        ExclusiveMetricPercent = node.ExclusiveMetricPercent,
                    });
                }
                return output;
            }
            finally
            {
                TryDelete(etlxFile);
                TryDelete(traceFile);
            }
        }

        private static bool IsFromExcludedModule(string frameName, string[] modulePrefixes)
        {
            for (int i = 0; i < modulePrefixes.Length; i++)
            {
                if (frameName.StartsWith(modulePrefixes[i], StringComparison.Ordinal))
                {
                    return true;
                }
            }
            return false;
        }

        private static void TryDelete(string path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { File.Delete(path); } catch { /* best-effort cleanup */ }
        }
    }

    public sealed class SampledMethod
    {
        public string Name { get; internal set; }
        public float InclusiveMetric { get; internal set; }
        public float ExclusiveMetric { get; internal set; }
        public float InclusiveMetricPercent { get; internal set; }
        public float ExclusiveMetricPercent { get; internal set; }
    }

    public sealed class SamplingProfileResult
    {
        public bool Success { get; internal set; }
        public bool Cancelled { get; internal set; }
        public Exception Exception { get; internal set; }
        public int ProcessId { get; internal set; }
        public TimeSpan Duration { get; internal set; }
        internal byte[] NetTraceData { get; set; }

        /// <summary>
        /// Module names whose frames will be hidden from <see cref="TopMethods"/> unless an
        /// explicit list is passed in. Carries the value configured on
        /// <c>MemoryIntrospectorOptions.SamplingExcludedModules</c> at capture time.
        /// </summary>
        public IReadOnlyList<string> DefaultExcludedModules { get; internal set; } = SamplingProfiler.DefaultExcludedModules;

        public int TraceSizeInBytes => NetTraceData?.Length ?? 0;

        public void SaveToDisk(string fileName)
        {
            if (NetTraceData is null || NetTraceData.Length == 0)
            {
                throw new InvalidOperationException("No trace data was captured.");
            }
            File.WriteAllBytes(fileName, NetTraceData);
        }

        /// <summary>
        /// Computes the top <paramref name="count"/> sampled methods.
        /// </summary>
        /// <param name="excludedModules">Modules (assembly names) to hide from the report.
        /// Pass null to use <see cref="DefaultExcludedModules"/>; pass an empty list to disable
        /// module filtering entirely.</param>
        public IReadOnlyList<SampledMethod> TopMethods(
            int count = 5,
            bool inclusive = false,
            IEnumerable<string> excludedModules = null,
            TextWriter log = null)
        {
            IReadOnlyList<string> effective = excludedModules is null
                ? DefaultExcludedModules
                : (excludedModules as IReadOnlyList<string>) ?? excludedModules.ToList();
            return SamplingProfiler.ComputeTopMethods(NetTraceData, count, inclusive, effective, log);
        }
    }
}
