using System.Diagnostics;
using System.Reflection;
using Memory.Introspect;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "child")
{
    // Busy-loop in a couple of named methods so the sampling profiler has something to capture,
    // plus a couple of threads parked on blocking primitives — without the blocked-thread
    // filter these would dominate the top-N report on a mostly-idle process.
    var stop  = new CancellationTokenSource();
    var mres  = new ManualResetEventSlim(false);
    AppDomain.CurrentDomain.ProcessExit += (_, _) => { stop.Cancel(); mres.Set(); };
    var t1 = Task.Run(() => SpinHot(stop.Token));
    var t2 = Task.Run(() => SpinWarm(stop.Token));
    var t3 = Task.Run(() => BlockedOnMres(mres, stop.Token));
    var t4 = Task.Run(() => BlockedOnMonitor(stop.Token));
    try { await Task.Delay(-1, stop.Token); } catch { }
    return;

    static void SpinHot(CancellationToken ct)
    {
        double x = 1.0001;
        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < 10_000; i++) x = Math.Sqrt(x + i);
        }
    }
    static void SpinWarm(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) Thread.SpinWait(1_000_000);
    }
    static void BlockedOnMres(ManualResetEventSlim mres, CancellationToken ct)
    {
        try { mres.Wait(ct); } catch (OperationCanceledException) { }
    }
    static void BlockedOnMonitor(CancellationToken ct)
    {
        var gate = new object();
        lock (gate)
        {
            while (!ct.IsCancellationRequested) Monitor.Wait(gate, 1000);
        }
    }
}

var childProcess = new Process
{
    StartInfo = new ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = $"{Assembly.GetExecutingAssembly().Location} child",
        UseShellExecute = false,
        CreateNoWindow = true
    }
};

childProcess.Start();

int currentPid    = childProcess.Id;

var loggerFactory = LoggerFactory.Create(f => f.AddConsole());
var logger = loggerFactory.CreateLogger("MemoryIntrospector");

logger.LogInformation("Starting creating gcdump file from process {0}", currentPid);

var introspector = MemoryIntrospector.Create(new() { Logger = logger, Verbose = true });

var result = await introspector.CollectMemoryGraphAsync(currentPid);

if (result.Success)
{
    var gcDumpFile =  $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{currentPid}.gcdump";
    logger.LogInformation("Writing .gcdump file to {0}", gcDumpFile);
    result.SaveToDisk(gcDumpFile);
}

logger.LogInformation("Finished creating gcdump file");

try
{
    logger.LogInformation("Starting creating memory dump file from process {0}", currentPid);

    var dumpFile = $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{currentPid}.dmp";
    await introspector.DumpAsync(currentPid, dumpFile, Memory.Introspect.Dumper.CollectionType.Mini);

    logger.LogInformation("Finished creating memory dump file: {0}", dumpFile);

    await ReportSampleAsync(introspector, currentPid, "child process", logger);

    // Demonstrate self-sampling: spin up some busy work in this process and sample it.
    int selfPid = Process.GetCurrentProcess().Id;
    using var selfStop = new CancellationTokenSource();
    var selfWork = Task.Run(() => SelfHotLoop(selfStop.Token));
    try
    {
        await ReportSampleAsync(introspector, selfPid, "self", logger);
    }
    finally
    {
        selfStop.Cancel();
        try { await selfWork; } catch { }
    }
}
finally
{
    childProcess.Kill();
}

static void SelfHotLoop(CancellationToken ct)
{
    double x = 1.0001;
    while (!ct.IsCancellationRequested)
    {
        for (int i = 0; i < 10_000; i++) x = Math.Sqrt(x + i);
    }
}

static async Task ReportSampleAsync(MemoryIntrospector introspector, int pid, string label, ILogger logger)
{
    logger.LogInformation("Starting sampling profile of {0} (pid {1})", label, pid);

    var sample = await introspector.CollectSamplingProfileAsync(pid, TimeSpan.FromSeconds(5));

    if (!sample.Success)
    {
        if (sample.Exception is not null)
        {
            logger.LogError(sample.Exception, "Sampling profile failed for {0}", label);
        }
        else
        {
            logger.LogWarning("Sampling profile produced no data for {0} (cancelled={1})", label, sample.Cancelled);
        }
        return;
    }

    var traceFile = $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-{label.Replace(' ', '-')}-{pid}.nettrace";
    sample.SaveToDisk(traceFile);
    logger.LogInformation("Wrote {0} bytes of trace data to {1}", sample.TraceSizeInBytes, traceFile);

    var top = sample.TopMethods(count: 10, inclusive: false);
    logger.LogInformation("Top {0} sampled methods for {1} (exclusive, excluding {2}):",
        top.Count, label, string.Join(", ", sample.DefaultExcludedModules));
    foreach (var m in top)
    {
        logger.LogInformation("  {0,6:0.00}%  {1}", m.ExclusiveMetricPercent, m.Name);
    }
}

await Task.Delay(1000); //Give time for the logger to flush