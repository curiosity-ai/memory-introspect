// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/CommandLine/Commands/ReportCommand.cs
// (the `dotnet-trace report topN` command), reshaped to return data instead of printing it.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Diagnostics.Tracing.StackSources;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// Builds the `dotnet-trace report topN` style report out of a captured .nettrace file:
    /// the methods that have been on the callstack the longest.
    /// </summary>
    public static class TraceReport
    {
        private static readonly string[] UnwantedMethodNames = { "ROOT", "Process" };

        /// <summary>
        /// Computes the top <paramref name="count"/> methods of a .nettrace file.
        /// </summary>
        /// <param name="traceFilePath">The .nettrace file to analyse.</param>
        /// <param name="count">How many methods to return.</param>
        /// <param name="inclusive">Rank by inclusive rather than exclusive time.</param>
        /// <param name="excludedModules">Assembly simple names whose frames are hidden from the
        /// result. Pass null or an empty list to keep every module.</param>
        /// <param name="blockingMethodPatterns">Regex patterns identifying methods that park a
        /// thread in a blocking wait; any sample whose stack contains a matching frame is
        /// dropped. Pass null or an empty list to keep blocked threads in the report.</param>
        /// <param name="log">Optional log for symbol resolution diagnostics.</param>
        public static IReadOnlyList<SampledMethod> TopMethodsFromFile(
            string traceFilePath,
            int count = 5,
            bool inclusive = false,
            IReadOnlyList<string> excludedModules = null,
            IReadOnlyList<string> blockingMethodPatterns = null,
            TextWriter log = null)
        {
            log ??= TextWriter.Null;

            if (string.IsNullOrEmpty(traceFilePath) || !File.Exists(traceFilePath))
            {
                throw new FileNotFoundException("Trace file not found.", traceFilePath);
            }

            if (count <= 0)
            {
                return Array.Empty<SampledMethod>();
            }

            // Build module prefixes once so we can do a cheap StartsWith check per frame. The frame
            // name format produced by SampleProfilerThreadTimeComputer is
            // "Module!Namespace.Type.Method(args)".
            string[] modulePrefixes = (excludedModules ?? Array.Empty<string>())
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.EndsWith("!") ? m : m + "!")
                .ToArray();

            string etlxFile = null;
            try
            {
                etlxFile = TraceLog.CreateFromEventPipeDataFile(traceFilePath);

                using SymbolReader symbolReader = new(log) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath };
                using TraceLog eventLog = new(etlxFile);

                MutableTraceEventStackSource stackSource = new(eventLog) { OnlyManagedCodeStacks = true };

                SampleProfilerThreadTimeComputer computer = new(eventLog, symbolReader);
                computer.GenerateThreadTimeStacks(stackSource);

                string excludeRegEx = null;
                if (blockingMethodPatterns is { Count: > 0 })
                {
                    // FilterStackSource treats ExcludeRegExs as semicolon-separated patterns; a sample
                    // is excluded if any frame in its stack matches any of them. Patterns get run
                    // through ToDotNetRegEx() which Regex.Escape's the input unless it starts with
                    // '@', so the '@' prefix is added here to keep raw .NET regex semantics.
                    excludeRegEx = string.Join(";", blockingMethodPatterns
                        .Where(p => !string.IsNullOrWhiteSpace(p))
                        .Select(p => p.StartsWith("@") ? p : "@" + p));
                }

                FilterParams filterParams = new()
                {
                    FoldRegExs = "CPU_TIME;UNMANAGED_CODE_TIME;{Thread (}",
                };

                // Leave FilterParams' own default in place when there is nothing to exclude:
                // FilterStackSource.ParseRegExList throws on a null pattern list.
                if (!string.IsNullOrEmpty(excludeRegEx))
                {
                    filterParams.ExcludeRegExs = excludeRegEx;
                }
                FilterStackSource filterStack = new(filterParams, stackSource, ScalingPolicyKind.ScaleToData);
                CallTree callTree = new(ScalingPolicyKind.ScaleToData) { StackSource = filterStack };

                List<CallTreeNodeBase> nodes = inclusive
                    ? callTree.ByID.OrderByDescending(n => Math.Abs(n.InclusiveMetric)).ToList()
                    : callTree.ByIDSortedExclusiveMetric();

                List<SampledMethod> output = new(count);
                foreach (CallTreeNodeBase node in nodes)
                {
                    if (output.Count >= count)
                    {
                        break;
                    }

                    string name = node.Name;
                    if (string.IsNullOrEmpty(name)) { continue; }
                    if (UnwantedMethodNames.Any(name.Contains)) { continue; }
                    if (IsFromExcludedModule(name, modulePrefixes)) { continue; }

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
                if (!string.IsNullOrEmpty(etlxFile))
                {
                    try { File.Delete(etlxFile); } catch { /* best-effort cleanup */ }
                }
            }
        }

        /// <summary>
        /// Writes a report in the same table layout `dotnet-trace report topN` prints.
        /// </summary>
        /// <param name="output">Where to write the table.</param>
        /// <param name="methods">The methods to report, in rank order.</param>
        /// <param name="inclusive">Whether the ranking used inclusive time (affects the header only).</param>
        /// <param name="verbose">Print full method signatures instead of truncating them.</param>
        public static void WriteTopMethodsReport(TextWriter output, IReadOnlyList<SampledMethod> methods, bool inclusive = false, bool verbose = false)
        {
            if (output is null)
            {
                throw new ArgumentNullException(nameof(output));
            }
            Microsoft.Diagnostics.Tools.Trace.CommandLine.PrintReportHelper.TopNWriteTo(output, methods, inclusive, verbose);
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
    }
}
