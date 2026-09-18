---
name: providers-and-clrevents
description: Specify exactly what a Memory.Introspect trace records — EventPipe provider spec strings, strongly typed EventPipeProvider objects, the ClrEventKeywords flags enum, event levels, and ProviderUtils parsing helpers for values coming from configuration. Use when a built-in profile is not specific enough.
---

# Providers and CLR event keywords

A trace records events from one or more **providers**. A profile is just a named bundle of
them; when you need something a profile does not cover, name the providers yourself.

There are four ways to say what to record, and they all merge into one provider set:

| Option | Shape | CLI equivalent |
| --- | --- | --- |
| `Profiles` | `TraceProfileKind` flags | `--profile` |
| `Providers` | strings, `Name[:Keywords[:Level[:KeyValueArgs]]]` | `--providers` |
| `ProviderConfigurations` | `EventPipeProvider` objects | (none — API only) |
| `ClrEvents` + `ClrEventLevel` | `ClrEventKeywords` flags + `EventLevel` | `--clrevents` / `--clreventlevel` |

`TraceResult.Providers` tells you what was actually enabled after the merge.

## Provider spec strings

```
Name[:Keywords[:Level[:KeyValueArgs]]]
```

- **Keywords** — a hexadecimal mask (`0x8001`), or `0x0` for "the provider's default".
- **Level** — an `EventLevel` number: `1` Critical, `2` Error, `3` Warning, `4` Informational,
  `5` Verbose.
- **KeyValueArgs** — `[key1=value1][;key2=value2]`, for providers that take arguments.

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration  = TimeSpan.FromSeconds(4),
    Providers = new[]
    {
        "Microsoft-Windows-DotNETRuntime:0x8001:5",   // GC | Exception, verbose
        "MyCompany-MyApp:0x0:4",                      // your EventSource, informational
    },
});

var runtime = trace.Providers.Single(p => p.Name == "Microsoft-Windows-DotNETRuntime");
// runtime.Keywords == 0x8001, runtime.EventLevel == EventLevel.Verbose
```

## Strongly typed providers

```csharp
using Memory.Introspect.Diagnostics.NETCore.Client;
using System.Diagnostics.Tracing;

var options = new TraceCollectionOptions
{
    Duration = TimeSpan.FromSeconds(10),
    ProviderConfigurations = new[]
    {
        new EventPipeProvider("Microsoft-Windows-DotNETRuntime", EventLevel.Verbose, keywords: 0x8001),
        new EventPipeProvider("Microsoft-Diagnostics-DiagnosticSource", EventLevel.Verbose,
            keywords: 0x3,
            arguments: new Dictionary<string, string> { ["FilterAndPayloadSpecs"] = spec }),
    },
};
```

`EventPipeProvider(string name, EventLevel eventLevel, long keywords = 0xF00000000000, IDictionary<string,string> arguments = null)`.

## ClrEventKeywords

For the runtime provider specifically, the `[Flags]` enum is easier to read than a hex mask.
It mirrors the runtime's own keyword table (the names `dotnet-trace --clrevents` accepts):

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration      = TimeSpan.FromSeconds(4),
    ClrEvents     = ClrEventKeywords.Gc | ClrEventKeywords.Exception | ClrEventKeywords.Contention,
    ClrEventLevel = EventLevel.Verbose,
    Rundown       = false,
});
```

The ones you will reach for most:

| Keyword | Value | Records |
| --- | --- | --- |
| `Gc` | `0x1` | GC start/stop, heap stats, and (at Verbose) allocation ticks |
| `GcHandle` | `0x2` | GC handle create/destroy |
| `AssemblyLoader` (alias `Fusion`) | `0x4` | Assembly load resolution |
| `Loader` | `0x8` | Module/assembly load and unload |
| `Jit` | `0x10` | Method JIT start/stop |
| `Contention` | `0x4000` | Lock contention |
| `Exception` | `0x8000` | Managed exceptions thrown |
| `Threading` | `0x10000` | Threads and thread pool |
| `JittedMethodILToNativeMap` | `0x20000` | IL↔native maps, needed to symbolise jitted frames |
| `Type` | `0x80000` | Type bookkeeping (needed for allocation type names) |
| `GcHeapAndTypeNames` | `0x1000000` | Type names in GC events |
| `GcSampledObjectAllocationHigh` / `…Low` | `0x200000` / `0x2000000` | Per-object allocation sampling |
| `WaitHandle` | `0x40000000000` | `WaitHandle` waits |
| `AllocationSampling` | `0x80000000000` | Allocation sampling events |

The full set also covers `NGen`, `StartEnumeration`, `EndEnumeration`, `Security`,
`AppDomainResourceManagement`, `JitTracing`, `Interop`, `OverrideAndSuppressNGenEvents`,
`GcHeapDump`, `GcHeapSurvivalAndMovement`, `GcHeapCollect` (alias `ManagedHeapCollect`),
`PerfTrack`, `Stack`, `ThreadTransfer`, `Debugger`, `Monitoring`, `CodeSymbols`,
`EventSource`, `Compilation`, `CompilationDiagnostic`, `MethodDiagnostic`, `TypeDiagnostic`,
`JitInstrumentationData` and `Profiler`.

**Level matters as much as the keyword.** Allocation ticks are Verbose-level: at
`Informational` the `Gc` keyword gives you collections but no allocations at all.

## Parsing values from configuration

When the keywords, level or profile come from a config file, an environment variable or a
command line, `ProviderUtils` maps the CLI spellings onto the typed values:

```csharp
ClrEventKeywords events = ProviderUtils.ParseClrEvents("gc+exception+contention");
EventLevel       level  = ProviderUtils.ParseEventLevel("verbose");   // or "5"
EventPipeProvider single = ProviderUtils.ToProvider("MyCompany-MyApp:0xF:5");
EventPipeProvider clr    = ProviderUtils.ToCLREventPipeProvider(events, level);

IReadOnlyCollection<string> known = ProviderUtils.KnownCLREventKeywords;  // for validation/help text
```

`ProviderUtils.ComputeProviderConfig(...)` performs the same merge the collector does, if you
want to inspect or log the resulting provider set before capturing.

## Related

- `profiles.md` — the pre-built bundles
- `custom-eventsource.md` — tracing your own `EventSource`
- `allocation-tracing.md` — the keyword/level combination allocation reports need
