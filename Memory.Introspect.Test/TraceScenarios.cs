using System.Diagnostics.Tracing;
using Memory.Introspect.Trace;
using Microsoft.Extensions.Logging;

namespace Memory.Introspect.Test;

/// <summary>
/// End-to-end exercises of the dotnet-trace equivalent surface against a live child process.
/// </summary>
internal static class TraceScenarios
{
    public static async Task RunAllAsync(MemoryIntrospector introspector, int pid, string outputDirectory, ILogger logger)
    {
        Directory.CreateDirectory(outputDirectory);

        ListProfiles(logger);
        ListProcesses(pid, logger);

        await CollectDefaultProfileToFileAsync(introspector, pid, outputDirectory, logger);
        await CollectWithExplicitProvidersInMemoryAsync(introspector, pid, logger);
        await CollectWithClrEventsAndNoRundownAsync(introspector, pid, logger);
        await CollectGcCollectProfileAsync(introspector, pid, outputDirectory, logger);
        await CollectAndConvertFormatsAsync(introspector, pid, outputDirectory, logger);
        await CollectUntilStoppingEventAsync(introspector, pid, outputDirectory, logger);
        await CollectWithLargeBuffersAsync(introspector, pid, outputDirectory, logger);
        await CollectCancelledAsync(introspector, pid, logger);
    }

    // ---- dotnet-trace list-profiles ------------------------------------------------------

    private static void ListProfiles(ILogger logger)
    {
        Section(logger, "list-profiles");
        foreach (var profile in MemoryIntrospector.ListTraceProfiles())
        {
            logger.LogInformation("  {0,-28} {1,-26} {2}", profile.Name, profile.Kind, profile.Description.Replace("\n", " "));
            foreach (var provider in profile.Providers)
            {
                logger.LogInformation("      {0,-45} keywords=0x{1:X16} level={2}", provider.Name, provider.Keywords, provider.EventLevel);
            }
        }
        var profiles = MemoryIntrospector.ListTraceProfiles();
        Assert(profiles.Count >= 5, "expected at least 5 built-in profiles");
        foreach (var profile in profiles)
        {
            Assert(ReferenceEquals(TraceProfiles.Find(profile.Kind), profile), $"{profile.Kind} does not round-trip through TraceProfiles.Find");
            Assert(ReferenceEquals(TraceProfiles.Find(profile.Name), profile), $"{profile.Name} does not round-trip through TraceProfiles.Find");
        }
        Assert(TraceProfiles.Expand(TraceProfileKind.Default).Count() == 2, "the default profile set should expand to 2 profiles");
        Assert(ProviderUtils.ParseClrEvents("gc+exception") == (ClrEventKeywords.Gc | ClrEventKeywords.Exception), "clrevents string parsing does not agree with the enum");
    }

    // ---- dotnet-trace ps ------------------------------------------------------------------

    private static void ListProcesses(int pid, ILogger logger)
    {
        Section(logger, "ps");
        var pids = MemoryIntrospector.GetTraceableProcesses();
        logger.LogInformation("  {0} traceable .NET processes: {1}", pids.Count, string.Join(", ", pids));
        Assert(pids.Contains(pid), $"expected the child process {pid} to publish a diagnostics endpoint");
    }

    // ---- dotnet-trace collect (defaults, streamed to a file) -------------------------------

    private static async Task CollectDefaultProfileToFileAsync(MemoryIntrospector introspector, int pid, string dir, ILogger logger)
    {
        Section(logger, "collect (default profiles -> file) + report topN");

        string output = Path.Combine(dir, "default-profile.nettrace");
        var progressReports = 0;

        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(6),
            OutputPath = output,
            Progress = new Progress<TraceProgress>(p =>
            {
                Interlocked.Increment(ref progressReports);
                logger.LogInformation("  progress {0}", p);
            }),
        });

        AssertSuccess(result, logger);
        Assert(File.Exists(output), "trace file was not written");
        Assert(new FileInfo(output).Length > 0, "trace file is empty");
        Assert(result.TraceFilePath == Path.GetFullPath(output), "TraceFilePath was not set");
        Assert(result.NetTraceData is null, "file-backed capture should not also buffer in memory");
        Assert(result.Providers.Any(p => p.Name == "Microsoft-Windows-DotNETRuntime"), "expected the dotnet-common provider");
        Assert(result.Providers.Any(p => p.Name == "Microsoft-DotNETCore-SampleProfiler"), "expected the sample profiler provider");
        Assert(result.Elapsed >= TimeSpan.FromSeconds(5.5), $"expected to record for ~6s, recorded for {result.Elapsed}");

        logger.LogInformation("  wrote {0:N0} bytes in {1:0.##}s ({2} progress reports)", result.TraceSizeInBytes, result.Elapsed.TotalSeconds, progressReports);

        // dotnet-trace report topN, both from the result and from the file on disk.
        var top = result.TopMethods(count: 10, excludedModules: new[] { "Memory.Introspect" });
        Assert(top.Count > 0, "expected the topN report to find methods");

        var report = new StringWriter();
        result.WriteTopMethodsReport(report, count: 10, excludedModules: new[] { "Memory.Introspect" });
        foreach (var line in report.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            logger.LogInformation("  {0}", line.TrimEnd());
        }

        var fromFile = introspector.ReportTopMethods(output, count: 10, excludedModules: new[] { "Memory.Introspect" });
        Assert(fromFile.Count == top.Count, "topN from the file should match topN from the result");
        Assert(fromFile.Any(m => m.Name.Contains("SpinHot") || m.Name.Contains("SpinWarm") || m.Name.Contains("AllocateGarbage")),
            $"expected a workload method in the topN, got: {string.Join(" | ", fromFile.Take(5).Select(m => m.Name))}");
    }

    // ---- dotnet-trace collect --providers (buffered in memory) -----------------------------

    private static async Task CollectWithExplicitProvidersInMemoryAsync(MemoryIntrospector introspector, int pid, ILogger logger)
    {
        Section(logger, "collect (--providers, in memory)");

        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(4),
            // Runtime GC + Exception keywords at verbose, plus the workload's own EventSource.
            Providers = new[] { "Microsoft-Windows-DotNETRuntime:0x8001:5", $"{WorkloadEventSource.ProviderName}:0x0:4" },
        });

        AssertSuccess(result, logger);
        Assert(result.NetTraceData is { Length: > 0 }, "expected in-memory trace data");
        Assert(result.TraceFilePath is null, "in-memory capture should not write a file");
        Assert(result.Providers.Count == 2, $"expected exactly the 2 requested providers, got {result.Providers.Count}");

        var runtime = result.Providers.Single(p => p.Name == "Microsoft-Windows-DotNETRuntime");
        Assert(runtime.Keywords == 0x8001, $"keyword mask was not parsed, got 0x{runtime.Keywords:X}");
        Assert(runtime.EventLevel == EventLevel.Verbose, $"event level was not parsed, got {runtime.EventLevel}");

        logger.LogInformation("  captured {0:N0} bytes in memory from {1} providers", result.TraceSizeInBytes, result.Providers.Count);
    }

    // ---- dotnet-trace collect --clrevents --rundown:false ----------------------------------

    private static async Task CollectWithClrEventsAndNoRundownAsync(MemoryIntrospector introspector, int pid, ILogger logger)
    {
        Section(logger, "collect (--clrevents gc+exception+contention, rundown off)");

        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(4),
            ClrEvents = ClrEventKeywords.Gc | ClrEventKeywords.Exception | ClrEventKeywords.Contention,
            ClrEventLevel = EventLevel.Verbose,
            Rundown = false,
        });

        AssertSuccess(result, logger);
        Assert(result.RundownKeyword == 0, $"expected rundown to be disabled, keyword was 0x{result.RundownKeyword:X}");

        var provider = result.Providers.Single();
        Assert(provider.Name == "Microsoft-Windows-DotNETRuntime", "clrevents should map onto the runtime provider");
        Assert(provider.Keywords == (long)(ClrEventKeywords.Gc | ClrEventKeywords.Exception | ClrEventKeywords.Contention), $"clrevents keyword mask was 0x{provider.Keywords:X}");
        Assert(provider.EventLevel == EventLevel.Verbose, $"clreventlevel was {provider.EventLevel}");

        logger.LogInformation("  captured {0:N0} bytes with keywords 0x{1:X} and no rundown", result.TraceSizeInBytes, provider.Keywords);
    }

    // ---- dotnet-trace collect --profile gc-collect -----------------------------------------

    private static async Task CollectGcCollectProfileAsync(MemoryIntrospector introspector, int pid, string dir, ILogger logger)
    {
        Section(logger, "collect (--profile gc-collect, custom rundown keyword)");

        string output = Path.Combine(dir, "gc-collect.nettrace");
        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(4),
            Profiles = TraceProfileKind.GcCollect,
            OutputPath = output,
        });

        AssertSuccess(result, logger);
        Assert(result.Providers.Count == 2, $"gc-collect enables 2 providers, got {result.Providers.Count}");
        // 0x1 is the GC keyword the profile asks for; older runtimes fall back to the default
        // rundown keyword or drop rundown entirely, which the retry strategy handles.
        logger.LogInformation("  rundown keyword resolved to 0x{0:X}, captured {1:N0} bytes", result.RundownKeyword, result.TraceSizeInBytes);
        Assert(result.RundownKeyword is 0x1 or 0 or 0x80020139, $"unexpected rundown keyword 0x{result.RundownKeyword:X}");
    }

    // ---- dotnet-trace collect --format / dotnet-trace convert ------------------------------

    private static async Task CollectAndConvertFormatsAsync(MemoryIntrospector introspector, int pid, string dir, ILogger logger)
    {
        Section(logger, "collect --format speedscope + convert to chromium");

        string output = Path.Combine(dir, "converted.nettrace");
        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(5),
            Profiles = TraceProfileKind.DotNetSampledThreadTime,
            OutputPath = output,
            Format = TraceFileFormat.Speedscope,
        });

        AssertSuccess(result, logger);
        Assert(result.ConvertedFilePath is not null, "expected a speedscope file to be produced");
        Assert(File.Exists(result.ConvertedFilePath), $"speedscope file missing: {result.ConvertedFilePath}");
        Assert(new FileInfo(result.ConvertedFilePath!).Length > 0, "speedscope file is empty");
        Assert(File.ReadAllText(result.ConvertedFilePath!).Contains("speedscope"), "speedscope file does not look like speedscope JSON");
        logger.LogInformation("  speedscope: {0} ({1:N0} bytes)", result.ConvertedFilePath, new FileInfo(result.ConvertedFilePath!).Length);

        string chromium = introspector.ConvertTraceFile(output, TraceFileFormat.Chromium, Path.Combine(dir, "converted-chromium.nettrace"));
        Assert(File.Exists(chromium), $"chromium file missing: {chromium}");
        Assert(new FileInfo(chromium).Length > 0, "chromium file is empty");
        Assert(File.ReadAllText(chromium).Contains("traceEvents"), "chromium file does not look like chrome tracing JSON");
        logger.LogInformation("  chromium:   {0} ({1:N0} bytes)", chromium, new FileInfo(chromium).Length);
    }

    // ---- dotnet-trace collect --stopping-event-* --------------------------------------------

    private static async Task CollectUntilStoppingEventAsync(MemoryIntrospector introspector, int pid, string dir, ILogger logger)
    {
        Section(logger, "collect (--stopping-event-provider-name / -event-name / -payload-filter)");

        string output = Path.Combine(dir, "stopping-event.nettrace");
        var started = DateTime.UtcNow;

        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            // A generous duration: the stopping event should cut it short long before this.
            Duration = TimeSpan.FromSeconds(60),
            Providers = new[] { $"{WorkloadEventSource.ProviderName}:0x0:4" },
            OutputPath = output,
            StoppingEventProviderName = WorkloadEventSource.ProviderName,
            StoppingEventEventName = "Milestone",
            StoppingEventPayloadFilter = new Dictionary<string, string> { ["phase"] = "working" },
        });

        var elapsed = DateTime.UtcNow - started;
        AssertSuccess(result, logger);
        Assert(result.StoppedByStoppingEvent, "expected the trace to be stopped by the stopping event");
        Assert(!result.StoppingEventPayloadFilterMismatched, "the payload filter should have matched the event");
        Assert(elapsed < TimeSpan.FromSeconds(30), $"stopping event did not cut the 60s duration short (took {elapsed})");
        logger.LogInformation("  stopped after {0:0.##}s on the first matching event, {1:N0} bytes", result.Elapsed.TotalSeconds, result.TraceSizeInBytes);

        // A payload filter naming a field the event does not have must be reported rather than
        // silently hanging until the duration expires.
        var mismatch = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(4),
            Providers = new[] { $"{WorkloadEventSource.ProviderName}:0x0:4" },
            StoppingEventProviderName = WorkloadEventSource.ProviderName,
            StoppingEventEventName = "Milestone",
            StoppingEventPayloadFilter = new Dictionary<string, string> { ["nosuchfield"] = "x" },
        });

        Assert(mismatch.StoppingEventPayloadFilterMismatched, "expected the payload filter mismatch to be reported");
        Assert(!mismatch.StoppedByStoppingEvent, "an unmatchable filter must not stop the trace");
        logger.LogInformation("  payload filter mismatch correctly reported, ran the full {0:0.##}s", mismatch.Elapsed.TotalSeconds);
    }

    // ---- large-process buffer settings -------------------------------------------------------

    private static async Task CollectWithLargeBuffersAsync(MemoryIntrospector introspector, int pid, string dir, ILogger logger)
    {
        Section(logger, "collect (large circular buffer + large stream copy buffer)");

        string output = Path.Combine(dir, "large-buffers.nettrace");
        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(5),
            // What you would use against a big, chatty process so the runtime does not drop events.
            CircularBufferSizeInMB = 2048,
            StreamCopyBufferSizeInBytes = 16 * 1024 * 1024,
            Profiles = TraceProfileKind.DotNetCommon | TraceProfileKind.DotNetSampledThreadTime,
            OutputPath = output,
        });

        AssertSuccess(result, logger);
        Assert(result.CircularBufferSizeInMB == 2048, $"expected the 2048 MB buffer to be used, got {result.CircularBufferSizeInMB}");
        logger.LogInformation("  captured {0:N0} bytes with a {1} MB runtime buffer", result.TraceSizeInBytes, result.CircularBufferSizeInMB);

        // And the introspector-level default flows through when the per-call value is not set.
        var defaulted = MemoryIntrospector.Create(new MemoryIntrospectorOptions { CircularBufferSizeInMB = 512, Logger = null });
        var inherited = await defaulted.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromSeconds(2),
            Profiles = TraceProfileKind.DotNetSampledThreadTime,
        });
        AssertSuccess(inherited, logger);
        Assert(inherited.CircularBufferSizeInMB == 512, $"expected the introspector default of 512 MB, got {inherited.CircularBufferSizeInMB}");
        logger.LogInformation("  introspector-level CircularBufferSizeInMB default honoured ({0} MB)", inherited.CircularBufferSizeInMB);
    }

    // ---- cancellation ------------------------------------------------------------------------

    private static async Task CollectCancelledAsync(MemoryIntrospector introspector, int pid, ILogger logger)
    {
        Section(logger, "collect (cancelled mid-flight)");

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
        {
            Duration = TimeSpan.FromMinutes(5),
            Profiles = TraceProfileKind.DotNetSampledThreadTime,
        }, cts.Token);

        Assert(result.Cancelled, "expected the result to be marked as cancelled");
        Assert(result.Success, "a cancelled capture should still return the data collected so far");
        Assert(result.Elapsed < TimeSpan.FromSeconds(30), $"cancellation did not take effect (ran for {result.Elapsed})");
        logger.LogInformation("  cancelled after {0:0.##}s keeping {1:N0} bytes", result.Elapsed.TotalSeconds, result.TraceSizeInBytes);
    }

    // ---- helpers -----------------------------------------------------------------------------

    private static void Section(ILogger logger, string title)
    {
        logger.LogInformation("");
        logger.LogInformation("=== {0} ===", title);
    }

    private static void AssertSuccess(TraceResult result, ILogger logger)
    {
        if (result.Exception is not null)
        {
            logger.LogError(result.Exception, "trace collection threw");
        }
        Assert(result.Exception is null, $"trace collection threw: {result.Exception?.Message}");
        Assert(result.Success, "trace collection reported no data");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"ASSERTION FAILED: {message}");
        }
    }
}
