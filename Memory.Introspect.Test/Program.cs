using System.Diagnostics;
using System.Reflection;
using Memory.Introspect;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "child")
{
    // Busy-loop in a couple of named methods so the sampling profiler has something to capture.
    var stop = new CancellationTokenSource();
    AppDomain.CurrentDomain.ProcessExit += (_, _) => stop.Cancel();
    var t1 = Task.Run(() => SpinHot(stop.Token));
    var t2 = Task.Run(() => SpinWarm(stop.Token));
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

    logger.LogInformation("Starting sampling profile of process {0}", currentPid);

    var sample = await introspector.CollectSamplingProfileAsync(currentPid, TimeSpan.FromSeconds(5));

    if (sample.Success)
    {
        var traceFile = $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{currentPid}.nettrace";
        sample.SaveToDisk(traceFile);
        logger.LogInformation("Wrote {0} bytes of trace data to {1}", sample.TraceSizeInBytes, traceFile);

        var top = sample.TopMethods(count: 10, inclusive: false);
        logger.LogInformation("Top {0} sampled methods (exclusive):", top.Count);
        foreach (var m in top)
        {
            logger.LogInformation("  {0,6:0.00}%  {1}", m.ExclusiveMetricPercent, m.Name);
        }
    }
    else if (sample.Exception is not null)
    {
        logger.LogError(sample.Exception, "Sampling profile failed");
    }
    else
    {
        logger.LogWarning("Sampling profile produced no data (cancelled={0})", sample.Cancelled);
    }
}
finally
{
    childProcess.Kill();
}

await Task.Delay(1000); //Give time for the logger to flush