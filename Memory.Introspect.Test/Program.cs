using System.Diagnostics;
using Memory.Introspect;
using Microsoft.Extensions.Logging;

int currentPid    = Process.GetCurrentProcess().Id;

var loggerFactory = LoggerFactory.Create(f => f.AddConsole());
var logger = loggerFactory.CreateLogger("MemoryIntrospector");

logger.LogInformation("Starting creating gcdump file from process {0}", currentPid);

var result = await MemoryIntrospector.Create(new() { Logger = logger, Verbose = true }).CollectMemoryGraphAsync(currentPid);

if (result.Success)
{
    var gcDumpFile =  $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{currentPid}.gcdump";
    logger.LogInformation("Writing .gcdump file to {0}", gcDumpFile);
    result.SaveToDisk(gcDumpFile);
}

logger.LogInformation("Finished creating gcdump file");

await Task.Delay(1000); //Give time for the logger to flush