// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
//
// Adapted from https://github.com/dotnet/diagnostics/blob/main/src/Tools/dotnet-trace/TraceFileFormatConverter.cs

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Stacks;
using Microsoft.Diagnostics.Tracing.Stacks.Formats;

namespace Memory.Introspect.Trace
{
    /// <summary>
    /// The output formats supported when converting a captured .nettrace file, mirroring the
    /// `--format` option of `dotnet-trace collect` / `dotnet-trace convert`.
    /// </summary>
    public enum TraceFileFormat
    {
        /// <summary>The raw EventPipe format, readable by PerfView and Visual Studio.</summary>
        NetTrace = 1,

        /// <summary>The speedscope.app JSON format.</summary>
        Speedscope,

        /// <summary>The Chromium (chrome://tracing / Perfetto) JSON format.</summary>
        Chromium
    }

    /// <summary>
    /// Converts a .nettrace file into one of the other supported <see cref="TraceFileFormat"/>s.
    /// </summary>
    public static class TraceFileFormatConverter
    {
        private static readonly IReadOnlyDictionary<TraceFileFormat, string> TraceFileFormatExtensions = new Dictionary<TraceFileFormat, string>
        {
            { TraceFileFormat.NetTrace,   "nettrace" },
            { TraceFileFormat.Speedscope, "speedscope.json" },
            { TraceFileFormat.Chromium,   "chromium.json" }
        };

        /// <summary>
        /// Produces the conventional output filename for <paramref name="format"/>, i.e. the
        /// input path with the format's extension. When <paramref name="outputFile"/> is given
        /// its extension is replaced instead.
        /// </summary>
        public static string GetConvertedFilename(string fileToConvert, string outputFile, TraceFileFormat format)
        {
            if (string.IsNullOrWhiteSpace(outputFile))
            {
                outputFile = fileToConvert;
            }

            return Path.ChangeExtension(outputFile, TraceFileFormatExtensions[format]);
        }

        /// <summary>
        /// Converts <paramref name="fileToConvert"/> into <paramref name="outputFilename"/>
        /// using <paramref name="format"/>. Converting to <see cref="TraceFileFormat.NetTrace"/>
        /// is a no-op since that is the capture format.
        /// </summary>
        public static void ConvertToFormat(TraceFileFormat format, string fileToConvert, string outputFilename, TextWriter log = null)
        {
            log ??= TextWriter.Null;

            switch (format)
            {
                case TraceFileFormat.NetTrace:
                    break;

                case TraceFileFormat.Speedscope:
                case TraceFileFormat.Chromium:
                    log.WriteLine($"Processing trace data file '{fileToConvert}' to create a new {format} file '{outputFilename}'.");
                    try
                    {
                        Convert(format, fileToConvert, outputFilename);
                    }
                    // On a broken/truncated trace, the exception coming out of TraceEvent is a plain
                    // System.Exception because it gets caught and rethrown inside TraceEvent.
                    catch (Exception ex) when (ex.ToString().Contains("Read past end of stream."))
                    {
                        log.WriteLine("Detected a potentially broken trace. Continuing with best-effort conversion, but the resulting file may contain broken stacks as a result.");
                        Convert(format, fileToConvert, outputFilename, continueOnError: true);
                    }
                    break;

                default:
                    throw new DiagnosticToolException($"Invalid TraceFileFormat \"{format}\"");
            }

            log.WriteLine("Conversion complete");
        }

        private static void Convert(TraceFileFormat format, string fileToConvert, string outputFilename, bool continueOnError = false)
        {
            string etlxFilePath = TraceLog.CreateFromEventPipeDataFile(fileToConvert, null, new TraceLogOptions { ContinueOnError = continueOnError });
            try
            {
                using SymbolReader symbolReader = new(TextWriter.Null) { SymbolPath = SymbolPath.MicrosoftSymbolServerPath };
                using TraceLog eventLog = new(etlxFilePath);

                MutableTraceEventStackSource stackSource = new(eventLog)
                {
                    OnlyManagedCodeStacks = true // EventPipe currently only has managed code stacks.
                };

                SampleProfilerThreadTimeComputer computer = new(eventLog, symbolReader)
                {
                    IncludeEventSourceEvents = false // Speedscope handles only CPU samples, events are not supported
                };
                computer.GenerateThreadTimeStacks(stackSource);

                switch (format)
                {
                    case TraceFileFormat.Speedscope:
                        SpeedScopeStackSourceWriter.WriteStackViewAsJson(stackSource, outputFilename);
                        break;
                    case TraceFileFormat.Chromium:
                        ChromiumStackSourceWriter.WriteStackViewAsJson(stackSource, outputFilename, compress: false);
                        break;
                    default:
                        throw new DiagnosticToolException($"Invalid TraceFileFormat \"{format}\"");
                }
            }
            finally
            {
                if (File.Exists(etlxFilePath))
                {
                    try { File.Delete(etlxFilePath); } catch { /* best-effort cleanup */ }
                }
            }
        }
    }
}
