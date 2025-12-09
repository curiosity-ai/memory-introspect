# Memory Introspect

**Programmatic .gcdump capture for .NET applications.**

`MemoryIntrospector` is a lightweight C\# library that wraps the functionality of the official `dotnet-gcdump` tool. It allows developers to capture garbage collection (GC) dumps and memory graphs directly from within their code, without needing to shell out to the CLI or manage external processes.

## 🚀 Why use this?

Normally, capturing a `.gcdump` requires running the `dotnet-gcdump` command-line tool against a Process ID (PID). While effective for ad-hoc debugging, it is difficult to automate within an application.

**MemoryIntrospector allows you to:**

  * **Self-Monitor:** Have an application trigger its own memory dump to analyze memory leaks.
  * **Automate:** Integrate memory capturing into integration tests or CI/CD pipelines.
  * **Streamline:** Avoid parsing CLI text output; work with strong types and direct boolean results.

-----

## 📦 Installation

```bash
dotnet add package Memory.Introspect
```

-----

## 💻 Usage

The library exposes a simple `CollectMemoryGraphAsync` method that connects to the target process via the .NET Diagnostics Client (EventPipe).

### Basic Example

Here is how to capture the current process's memory graph and save it to a temporary file:

```csharp
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
```

### Analyzing the Output

The resulting `.gcdump` file can be opened in:

  * **Visual Studio+**
  * **[PerfView](https://github.com/microsoft/perfview)**
-----

## ⚙️ Configuration Options

When initializing the `Memory.Introspect`, you can pass a configuration object:

| Option | Type | Description |
| :--- | :--- | :--- |
| `Logger` | `ILogger` | Used to log the internal diagnostics protocol progress (Handshake, EventPipe setup, etc.). |
| `Verbose` | `bool` | If true, outputs detailed logs regarding the connection status and graph construction. |
| `Timeout` | `TimeSpan` | *(Optional)* Set a maximum duration for the collection process before cancelling. Minimum of 30s.|

-----

## ⚠️ Requirements & Limitations

  * **Platform:** Works on Windows, Linux, and macOS.
  * **Privileges:** The process running the code must have sufficient privileges to access the target process via the Diagnostics Client. If capturing the **current** process, standard user privileges are usually sufficient.
  * **Runtime:** Requires .NET 6 or later.

-----

## ⚖️ License & Attribution

This project is licensed under the **MIT License**.

> **Note:** This library is heavily based on the source code of the official `dotnet-gcdump` tool provided by the .NET team.
>
> The core logic for EventPipe communication and Graph construction is adapted from:
> [https://github.com/dotnet/diagnostics/tree/main/src/Tools/dotnet-gcdump](https://github.com/dotnet/diagnostics/tree/main/src/Tools/dotnet-gcdump)
>
> We are grateful to the .NET Diagnostics team for their open-source contributions.
