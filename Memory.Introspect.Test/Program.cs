using System.Diagnostics;
using System.Reflection;
using Memory.Introspect;
using Microsoft.Extensions.Logging;

if (args.Length > 0 && args[0] == "child")
{
    // Wait indefinitely so the parent can dump this process.
    await Task.Delay(-1);
    return;
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
}
finally
{
    childProcess.Kill();
}

await Task.Delay(1000); //Give time for the logger to flush