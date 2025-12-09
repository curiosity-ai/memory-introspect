using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Graphs;
using Microsoft.Diagnostics.Tools.GCDump;
using Microsoft.Extensions.Logging;

namespace Memory.Introspect
{
    public sealed class MemoryIntrospector
    {
        private readonly MemoryIntrospectorOptions _options;

        private MemoryIntrospector(MemoryIntrospectorOptions options)
        {
            _options = options;
        }

        public static MemoryIntrospector Create(MemoryIntrospectorOptions options = null)
        {
            options ??= new MemoryIntrospectorOptions();

            if (options.Timeout.TotalSeconds < 30)
            {
                options.Timeout = TimeSpan.FromSeconds(30);
            }

            return new MemoryIntrospector(options);
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

                if (!EventPipeDotNetHeapDumper.DumpFromEventPipe(cancellationToken, processId, _options.DiagnosticPort, memoryGraph, GetTextWriter(), (int)_options.Timeout.TotalSeconds, heapInfo, _options.MaxNodeCount, response))
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
        public TimeSpan Timeout { get; set; }
        public bool Verbose { get; set; }
        public bool ExpectLargeGraph { get; set; }
        public int MaxNodeCount { get; set; }
        public ILogger Logger { get; set;  }
        public LogLevel LogLevel { get; set; } = LogLevel.Information;
        public MemoryIntrospectorOptions Default()
        {
            return new MemoryIntrospectorOptions()
            {
                MaxNodeCount = 10_000_000,
                ExpectLargeGraph = false,
                Verbose = true,
                DiagnosticPort = null,
                Timeout = TimeSpan.FromSeconds(30)
            };
        }
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
