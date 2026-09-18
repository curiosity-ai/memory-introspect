---
name: formats-and-conversion
description: Convert Memory.Introspect .nettrace captures to speedscope or Chromium/Perfetto JSON with TraceFileFormat, the Format option, TraceResult.ConvertTo and ConvertTraceFile, and open the results in PerfView, Visual Studio, speedscope.app or Perfetto. Use when deciding what to do with a captured trace file.
---

# Output formats and conversion

A capture is always `.nettrace` on the wire. Conversion produces an additional file in a
format some other viewer understands — it never replaces the original.

```csharp
public enum TraceFileFormat { NetTrace = 1, Speedscope, Chromium }
```

| Format | Extension | Opens in |
| --- | --- | --- |
| `NetTrace` | `.nettrace` | PerfView, Visual Studio |
| `Speedscope` | `.speedscope.json` | [speedscope.app](https://www.speedscope.app/) |
| `Chromium` | `.chromium.json` | `chrome://tracing`, [Perfetto](https://ui.perfetto.dev/) |

**Both converted formats only carry CPU samples.** They are built from the sample profiler's
thread-time stacks, so a trace captured without `TraceProfileKind.DotNetSampledThreadTime`
converts to an empty flame graph. `EventSource` events are not included in the conversion — to
look at those, open the `.nettrace` in PerfView.

## Convert as part of the capture

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromSeconds(20),
    Profiles   = TraceProfileKind.DotNetSampledThreadTime,
    OutputPath = "app.nettrace",
    Format     = TraceFileFormat.Speedscope,
    // ConvertedOutputPath = "custom-name.json",   // derived from OutputPath when null
});

string speedscope = trace.ConvertedFilePath;   // "app.speedscope.json"
```

## Convert afterwards

```csharp
// From a result
string chromium = trace.ConvertTo(TraceFileFormat.Chromium);
string named    = trace.ConvertTo(TraceFileFormat.Speedscope, "reports/run-42.json");

// From any .nettrace file on disk (dotnet-trace convert)
string path = introspector.ConvertTraceFile("app.nettrace", TraceFileFormat.Chromium);
string to   = introspector.ConvertTraceFile("app.nettrace", TraceFileFormat.Chromium, "out/app.json");

// The low-level helpers, if you want to bypass MemoryIntrospector
string resolved = TraceFileFormatConverter.GetConvertedFilename("app.nettrace", null, TraceFileFormat.Speedscope);
TraceFileFormatConverter.ConvertToFormat(TraceFileFormat.Speedscope, "app.nettrace", resolved, Console.Out);
```

`ConvertTraceFile` throws `FileNotFoundException` for a missing input and returns the full path
it wrote.

**In-memory captures:** `ConvertTo` with no `outputPath` throws on a trace that was buffered in
memory — there is no `.nettrace` path to derive a name from. Pass an explicit path, or set
`OutputPath` when collecting.

## Naming

`GetConvertedFilename` replaces the extension: `app.nettrace` → `app.speedscope.json`. Passing
an `outputPath` replaces *its* extension, so `ConvertTo(Speedscope, "reports/run-42.nettrace")`
writes `reports/run-42.speedscope.json`. Pass the name you want with the right extension if
that surprises you.

## Broken and truncated traces

A trace cut short (process killed mid-capture, disk full) can fail conversion with
"Read past end of stream". That is detected and retried with best-effort conversion, which
logs a warning and produces a file with possibly-broken stacks rather than failing outright.
Pass a `Logger` to see the warning.

## Cost

Conversion runs `TraceLog.CreateFromEventPipeDataFile`, which rewrites the trace as ETLX in
the temp directory before generating stacks. On a large capture that is slow and needs real
temp disk space. Convert once and cache the result rather than converting per request. The
ETLX intermediate is deleted afterwards.

Symbol resolution uses the Microsoft symbol server path, so the first conversion on a machine
may reach the network.

## Related

- `cpu-sampling.md` — the samples that make a conversion meaningful
- `offline-reports.md` — reporting without converting
