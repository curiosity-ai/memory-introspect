---
name: profiles
description: The built-in Memory.Introspect tracing profiles — dotnet-common, dotnet-sampled-thread-time, gc-verbose, gc-collect and database — what each enables, how to combine them with TraceProfileKind flags, and how to list or look them up by name. Use when choosing what a trace should record.
---

# Trace profiles

A profile is a named, pre-defined set of providers — the same ones
`dotnet-trace list-profiles` prints. They are the fastest way to get a sensible trace without
hand-writing keyword masks.

## The flags enum

```csharp
[Flags] public enum TraceProfileKind
{
    None                    = 0,
    DotNetCommon            = 1 << 0,   // "dotnet-common"
    DotNetSampledThreadTime = 1 << 1,   // "dotnet-sampled-thread-time"
    GcVerbose               = 1 << 2,   // "gc-verbose"
    GcCollect               = 1 << 3,   // "gc-collect"
    Database                = 1 << 4,   // "database"
    Default = DotNetCommon | DotNetSampledThreadTime,
}
```

Combine with `|`, exactly as passing `--profile` more than once:

```csharp
Profiles = TraceProfileKind.DotNetCommon | TraceProfileKind.DotNetSampledThreadTime
```

Casting an unknown bit into the enum throws `DiagnosticToolException` at collection time
rather than silently tracing nothing.

## What each profile records

| Profile | Providers | Cost | Use it for |
| --- | --- | --- | --- |
| `DotNetCommon` | `Microsoft-Windows-DotNETRuntime:0x100003801D:Informational` — GC, AssemblyLoader, Loader, JIT, Exceptions, Threading, JittedMethodILToNativeMap, Compilation | Low | General "what is the runtime doing" traces. The backbone of a default capture. |
| `DotNetSampledThreadTime` | `Microsoft-DotNETCore-SampleProfiler:Informational` — managed thread stacks at ~100 Hz | Low–moderate | **Required** for `TopMethods` reports and for speedscope/Chromium output to mean anything. |
| `GcVerbose` | `Microsoft-Windows-DotNETRuntime:Verbose` with GC + GCHandle + Exception keywords | Moderate–high | GC collections plus sampled object allocations. |
| `GcCollect` | `Microsoft-Windows-DotNETRuntime` and `Microsoft-Windows-DotNETRuntimePrivate`, both GC keyword at Informational | Very low | Long GC-behaviour captures on production processes. Uses the GC rundown keyword (`0x1`) and falls back gracefully on older runtimes. |
| `Database` | `System.Threading.Tasks.TplEventSource` (activity ids) + `Microsoft-Diagnostics-DiagnosticSource` with a `FilterAndPayloadSpecs` argument for SqlClient and EF Core | Low | Capturing ADO.NET / EF Core commands, including `CommandText` and timing. |

`Default` (`DotNetCommon | DotNetSampledThreadTime`) is what you get when a
`TraceCollectionOptions` names no profile, provider or CLR event at all.

## Listing profiles

```csharp
foreach (TraceProfile profile in MemoryIntrospector.ListTraceProfiles())
{
    Console.WriteLine($"{profile.Name,-28} {profile.Kind}");
    Console.WriteLine($"    {profile.Description.Replace("\n", "\n    ")}");

    foreach (EventPipeProvider p in profile.Providers)
        Console.WriteLine($"    {p.Name,-45} keywords=0x{p.Keywords:X16} level={p.EventLevel}");
}
```

`TraceProfile` carries `Kind`, `Name`, `Description`, `Providers`, `RundownKeyword` and
`RetryStrategy`.

## Mapping names from configuration

When the profile comes from a config file or a command line rather than from code:

```csharp
TraceProfile profile = TraceProfiles.Find("gc-collect");        // case-insensitive, null if unknown
TraceProfileKind kind = profile?.Kind ?? TraceProfileKind.Default;

// And back the other way
TraceProfile byKind = TraceProfiles.Find(TraceProfileKind.GcCollect);
IEnumerable<TraceProfile> all = TraceProfiles.Expand(TraceProfileKind.Default);  // the two it names
```

## Example: low-overhead GC capture in production

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration   = TimeSpan.FromMinutes(5),
    Profiles   = TraceProfileKind.GcCollect,
    OutputPath = "gc.nettrace",
});

// gc-collect asks for the GC rundown keyword; older runtimes fall back to the default
// keyword or drop rundown, which the built-in retry strategy handles silently.
logger.LogInformation("rundown keyword resolved to 0x{0:X}", trace.RundownKeyword);
```

## Not available

`dotnet-trace`'s `cpu-sampling` and `thread-time` profiles belong to `collect-linux`, which
drives the Linux `perf_events` subsystem through an external collector and cannot be done from
inside a managed library. They are intentionally absent. `DotNetSampledThreadTime` is the
in-process equivalent.

## Related

- `providers-and-clrevents.md` — when a profile is not specific enough
- `trace-collect.md` — where `Profiles` is set
- `cpu-sampling.md` — the reports `DotNetSampledThreadTime` feeds
