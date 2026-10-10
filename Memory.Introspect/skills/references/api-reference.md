---
name: api-reference
description: Complete public API surface of Memory.Introspect — every type and member of MemoryIntrospector, MemoryIntrospectorOptions, TraceCollectionOptions, TraceResult, SamplingProfileResult, AllocationReport, TraceProfiles, ClrEventKeywords, ProviderUtils, TraceReport, TraceFileFormatConverter, Dumper and the GcPauses types (GcPauseMonitor, GcPause, GcPauseDetailedReport), grouped by namespace. Use when looking up a signature or checking what a type exposes.
---

# API reference

## Namespaces

| Namespace | Contains |
| --- | --- |
| `Memory.Introspect` | `MemoryIntrospector`, `MemoryIntrospectorOptions`, `MemoryGraphResult`, `Dumper` |
| `Memory.Introspect.Trace` | Everything tracing: options, results, reports, profiles, keywords, helpers |
| `Memory.Introspect.GcPauses` | `GcPauseMonitor`, its options, `GcPause` and the detailed capture report |
| `Memory.Introspect.Diagnostics.NETCore.Client` | `EventPipeProvider` (and the IPC client internals) |

## `MemoryIntrospector`

```csharp
// Construction
static MemoryIntrospector Create(MemoryIntrospectorOptions options = null);

// Traces
Task<TraceResult> CollectTraceAsync(int processId, TimeSpan duration, CancellationToken ct = default);
Task<TraceResult> CollectTraceAsync(int processId, TraceCollectionOptions options, CancellationToken ct = default);

// CPU sampling
Task<SamplingProfileResult> CollectSamplingProfileAsync(int processId, TimeSpan duration, CancellationToken ct = default);

// Allocations
Task<AllocationReport> CollectAllocationReportAsync(int processId, TimeSpan duration,
        int count = 10, string outputPath = null, CancellationToken ct = default);
Task<AllocationReport> CollectAllocationReportAsync(int processId, TimeSpan duration,
        int count, string outputPath, bool resolveCallStacks, CancellationToken ct = default);

// Heap and process dumps
Task<MemoryGraphResult> CollectMemoryGraphAsync(int processId, CancellationToken ct = default);
Task<int> DumpAsync(int pid, string targetPath, Dumper.CollectionType collectionType);   // 0 = ok, -1 = failed

// Offline analysis
IReadOnlyList<SampledMethod> ReportTopMethods(string traceFilePath, int count = 5, bool inclusive = false,
        IEnumerable<string> excludedModules = null, IEnumerable<string> blockingMethodPatterns = null);
AllocationReport ReportTopAllocatedTypes(string traceFilePath, int count = 10, bool resolveCallStacks = false);
string ConvertTraceFile(string traceFilePath, TraceFileFormat format, string outputPath = null);

// GC pause monitoring
Task<GcPauseMonitor> StartGcPauseMonitorAsync(int processId, GcPauseMonitorOptions options = null, CancellationToken ct = default);
GcPauseDetailedReport ReportGcPauses(string traceFilePath, TimeSpan longPauseThreshold, bool resolveTriggerStacks = false);

// Discovery
static IReadOnlyList<TraceProfile> ListTraceProfiles();
static IReadOnlyList<int> GetTraceableProcesses();
```

`CollectTraceAsync`, `CollectSamplingProfileAsync` and `CollectAllocationReportAsync` throw
`ArgumentOutOfRangeException` for a non-positive duration. `ConvertTraceFile` throws
`ArgumentNullException` / `FileNotFoundException`. `CollectAllocationReportAsync` rethrows a
capture exception; a capture that merely produced nothing returns an empty report.

## `MemoryIntrospectorOptions`

```csharp
string    DiagnosticPort { get; set; }                  // null
TimeSpan  Timeout { get; set; }                         // 30s, clamped to >= 30s by Create
bool      Verbose { get; set; }                         // true
bool      ExpectLargeGraph { get; set; }                // false
int       MaxNodeCount { get; set; }                    // 10_000_000
int       CircularBufferSizeInMB { get; set; }          // 1024
ILogger   Logger { get; set; }                          // null
LogLevel  LogLevel { get; set; }                        // Information
IReadOnlyList<string> SamplingExcludedModules { get; set; }         // ["Memory.Introspect"]
IReadOnlyList<string> SamplingBlockingMethodPatterns { get; set; }  // ~30 blocking-wait regexes
```

## `TraceCollectionOptions`

```csharp
IReadOnlyList<string>            Providers { get; set; }
IReadOnlyList<EventPipeProvider> ProviderConfigurations { get; set; }
TraceProfileKind                 Profiles { get; set; }                  // None
ClrEventKeywords                 ClrEvents { get; set; }                 // None
EventLevel?                      ClrEventLevel { get; set; }             // null -> Informational
TimeSpan?                        Duration { get; set; }
int?                             CircularBufferSizeInMB { get; set; }
int                              StreamCopyBufferSizeInBytes { get; set; }  // 1 MB
bool?                            Rundown { get; set; }
long?                            RundownKeyword { get; set; }
bool                             RequestStackwalk { get; set; }          // true; false needs .NET 9+
bool                             ResumeRuntime { get; set; }             // false
string                           OutputPath { get; set; }
TraceFileFormat                  Format { get; set; }                    // NetTrace
string                           ConvertedOutputPath { get; set; }
string                           StoppingEventProviderName { get; set; }
string                           StoppingEventEventName { get; set; }
IReadOnlyDictionary<string,string> StoppingEventPayloadFilter { get; set; }
string                           DiagnosticPort { get; set; }
bool                             RetryOnUnsupportedConfiguration { get; set; }  // true
IProgress<TraceProgress>         Progress { get; set; }
```

## `TraceResult`

```csharp
bool Success; bool Cancelled; Exception Exception;
int ProcessId; TimeSpan? RequestedDuration; TimeSpan Elapsed;
bool StoppedByStoppingEvent; bool StoppingEventPayloadFilterMismatched;
IReadOnlyList<EventPipeProvider> Providers;
long RundownKeyword; int CircularBufferSizeInMB;
string TraceFilePath; string ConvertedFilePath;
byte[] NetTraceData; long TraceSizeInBytes;

void   SaveToDisk(string fileName);
string ConvertTo(TraceFileFormat format, string outputPath = null, TextWriter log = null);
IReadOnlyList<SampledMethod> TopMethods(int count = 5, bool inclusive = false,
        IEnumerable<string> excludedModules = null, IEnumerable<string> blockingMethodPatterns = null,
        TextWriter log = null);
void   WriteTopMethodsReport(TextWriter output, int count = 5, bool inclusive = false, bool verbose = false,
        IEnumerable<string> excludedModules = null, IEnumerable<string> blockingMethodPatterns = null);
AllocationReport TopAllocatedTypes(int count = 10, TextWriter log = null, bool resolveCallStacks = false);
void   WriteAllocationReport(TextWriter output, int count = 10, bool verbose = false);
```

## `TraceProgress`

```csharp
readonly struct TraceProgress { TimeSpan Elapsed { get; } long SizeInBytes { get; } }
```

`ToString()` → `"[dd:hh:mm:ss] 1,234,567 bytes"`. Reported about once per second.

## `SamplingProfileResult` / `SampledMethod`

```csharp
bool Success; bool Cancelled; Exception Exception;
int ProcessId; TimeSpan Duration;
byte[] NetTraceData; int TraceSizeInBytes;
IReadOnlyList<string> DefaultExcludedModules;
IReadOnlyList<string> DefaultBlockingMethodPatterns;
void SaveToDisk(string fileName);
IReadOnlyList<SampledMethod> TopMethods(int count = 5, bool inclusive = false,
        IEnumerable<string> excludedModules = null, IEnumerable<string> blockingMethodPatterns = null,
        TextWriter log = null);

sealed class SampledMethod
{
    string Name;                    // "Module!Namespace.Type.Method(args)"
    float  InclusiveMetric, ExclusiveMetric;
    float  InclusiveMetricPercent, ExclusiveMetricPercent;
}
```

On `SamplingProfileResult.TopMethods`, a null filter argument means "use the configured
defaults"; an empty list disables that filter.

## Allocation types

```csharp
enum AllocationSampleSource { None = 0, AllocationTick = 1, SampledObjectAllocation = 2 }

sealed class AllocatedType
{
    string TypeName; long AllocatedBytes; double AllocatedBytesPercent;
    long SampleCount; long ObjectCount;
    long SmallObjectHeapBytes; long LargeObjectHeapBytes;
}

sealed class AllocationCallStack
{
    long AllocatedBytes; double AllocatedBytesPercent; long SampleCount;
    string TypeName; IReadOnlyList<string> Frames;      // allocating method first
}

sealed class AllocationReport
{
    IReadOnlyList<AllocatedType> Types;
    long TotalAllocatedBytes; int DistinctTypeCount; long SampleCount;
    AllocationSampleSource Source;
    IReadOnlyList<AllocationCallStack> CallStacks;
    bool HasCallStacks; bool IsEmpty;
}

static class AllocationTracing
{
    const ClrEventKeywords RequiredClrEvents =
        ClrEventKeywords.Gc | ClrEventKeywords.Type | ClrEventKeywords.GcHeapAndTypeNames;
    const EventLevel RequiredClrEventLevel = EventLevel.Verbose;

    static TraceCollectionOptions CreateOptions(TimeSpan duration, string outputPath = null,
                                                bool resolveCallStacks = false);
    static AllocationReport FromFile(string traceFilePath, int count = 10, TextWriter log = null,
                                     bool resolveCallStacks = false);
    static void Write(TextWriter output, AllocationReport report, bool verbose = false);
    static void WriteCallStacks(TextWriter output, AllocationReport report, int maxFrames = 12);
}
```

## Profiles and keywords

```csharp
[Flags] enum TraceProfileKind { None, DotNetCommon, DotNetSampledThreadTime, GcVerbose, GcCollect,
                                Database, Default = DotNetCommon | DotNetSampledThreadTime }

sealed class TraceProfile
{
    TraceProfileKind Kind; string Name; string Description;
    IReadOnlyList<EventPipeProvider> Providers;
    long RundownKeyword; RetryStrategy RetryStrategy;
}

static class TraceProfiles
{
    IReadOnlyList<TraceProfile> All { get; }
    const TraceProfileKind DefaultProfiles = TraceProfileKind.Default;
    static TraceProfile Find(TraceProfileKind kind);
    static TraceProfile Find(string name);                       // case-insensitive
    static IEnumerable<TraceProfile> Expand(TraceProfileKind kinds);
}

[Flags] enum ClrEventKeywords : long { … }    // see providers-and-clrevents.md
```

## Helpers

```csharp
static class ProviderUtils
{
    IReadOnlyCollection<string> KnownCLREventKeywords { get; }
    static List<EventPipeProvider> ComputeProviderConfig(…);
    static EventPipeProvider ToCLREventPipeProvider(ClrEventKeywords clrEvents, EventLevel? clrEventLevel);
    static ClrEventKeywords ParseClrEvents(string clrEventsList);     // "gc+exception"
    static EventLevel ParseEventLevel(string token);                  // "verbose" or "5"
    static EventPipeProvider ToProvider(string provider, TextWriter log = null);
}

static class TraceReport
{
    static IReadOnlyList<SampledMethod> TopMethodsFromFile(string traceFilePath, int count, bool inclusive,
            IReadOnlyList<string> excludedModules, IReadOnlyList<string> blockingMethodPatterns, TextWriter log);
    static void WriteTopMethodsReport(TextWriter output, IReadOnlyList<SampledMethod> methods,
            bool inclusive = false, bool verbose = false);
}

enum TraceFileFormat { NetTrace = 1, Speedscope, Chromium }

static class TraceFileFormatConverter
{
    static string GetConvertedFilename(string fileToConvert, string outputFile, TraceFileFormat format);
    static void   ConvertToFormat(TraceFileFormat format, string fileToConvert, string outputFilename,
                                  TextWriter log = null);
}
```

## Heap and process dumps

```csharp
class MemoryGraphResult
{
    bool Success; bool Timeouted; bool Cancelled; bool NoHeapFound;
    Exception Exception; MemoryGraph Graph;
    void SaveToDisk(string fileName);
}

partial class Dumper
{
    enum CollectionType { Full, Heap, Mini, Triage }
}
```

## GC pause monitoring (`Memory.Introspect.GcPauses`)

```csharp
sealed class GcPauseMonitor : IAsyncDisposable
{
    int ProcessId; bool StackwalkEnabled;
    long EventsProcessed; long EventsLost; long PauseCount; long LongPauseCount;
    TimeSpan TotalPauseTime; TimeSpan MaxPause;
    IReadOnlyList<GcPause> RecentLongPauses;              // last RecentLongPauseCapacity, oldest first
    IReadOnlyList<GcPauseDetailedReport> DetailedReports;
    bool IsCapturingDetails;
    Task<GcPauseDetailedReport> CaptureDetailsAsync(TimeSpan duration, CancellationToken ct = default);
    Task StopAsync();
}

class GcPauseMonitorOptions
{
    TimeSpan LongPauseThreshold = 200ms;
    Action<GcPause> OnPause; Action<GcPause> OnLongPause;
    int CircularBufferSizeInMB = 16; int RecentLongPauseCapacity = 32;
    GcPauseDetailedCaptureOptions DetailedCapture = new();
}

class GcPauseDetailedCaptureOptions
{
    bool Enabled = true; TimeSpan MaxDuration = 60s; TimeSpan? StopAfterLongPause = 2s;
    string OutputDirectory;                                // null = <temp>/memory-introspect-gc-pauses
    bool CollectStacks = true; bool VerboseGcEvents = true; bool ObjectSurvivalAndMovement = false;
    bool CollectGcDump = false; int GcDumpTopTypes = 15;
    int MaxCaptures = 3; TimeSpan Cooldown = 5min; int CircularBufferSizeInMB = 256;
    Action<GcPauseDetailedReport> OnReport;
}

class GcPause
{
    DateTime StartTimeUtc; double PauseMSec; double SuspendMSec; double RestartMSec;
    GCSuspendEEReason SuspendReason; GcCollectionInfo Gc;  // null for a non-GC suspension
    bool IsLong; bool DuringDetailedCapture; GcPauseCause Cause; IReadOnlyList<string> Findings;
    string Summary; string ToReport();
}

enum GcPauseCause { Unknown, TimeToSafePoint, Marking, Compaction, Sweeping, CardScanning, NonGcSuspension, BackgroundGc }

class GcCollectionInfo
{
    int Number; int Generation; GCType Type; GCReason Reason; bool IsInduced; bool IsBlocking;
    bool Compacted; bool CompactedLargeObjectHeap; GCPauseMode PauseMode; int MemoryLoadPercent;
    GCGlobalMechanisms GlobalMechanisms; IReadOnlyList<gc_heap_compact_reason> CompactReasons;
    IReadOnlyList<string> CondemnReasons; GcPhaseTimes Phases; IReadOnlyList<GcMarkRoot> MarkRoots;
    IReadOnlyList<GcGenerationInfo> Generations; double DurationMSec; long PromotedBytes;
    long HeapSizeAfterBytes; int PinnedObjectCount; int GCHandleCount; int SinkBlockCount;
    long FinalizationPromotedCount; long FinalizationPromotedBytes; double LohCompactMSec;
    int HeapCount; int TriggeringThreadId;
}

class GcPhaseTimes   { double MarkRootsMSec, MarkShortWeakMSec, MarkFinalizationMSec, MarkLongWeakMSec,
                              PlanMSec, RelocateMSec, CompactMSec, SweepMSec, MarkMSec, PostMarkMSec; }
class GcMarkRoot     { MarkRootType Kind; double MarkMSec; long PromotedBytes; }
class GcGenerationInfo { Gens Generation; long SizeBeforeBytes, SizeAfterBytes,
                         FragmentationBeforeBytes, FragmentationAfterBytes, SurvivedBytes; }

class GcPauseDetailedReport
{
    GcPause Trigger; string TraceFilePath; long TraceSizeInBytes; TimeSpan Elapsed;
    IReadOnlyList<GcPause> Pauses; IReadOnlyList<GcPause> LongPauses;
    IReadOnlyList<GcTriggerSite> TriggerSites;            // who called GC.Collect / ran out the budget
    AllocationReport Allocations; IReadOnlyList<GcPinnedType> PinnedTypes;
    string GcDumpPath; IReadOnlyList<GcHeapType> HeapTypes; long HeapObjectCount; long HeapBytes;
    Exception Exception; bool Success; string ToReport();
}
```

`GCType`, `GCReason`, `GCSuspendEEReason`, `GCPauseMode`, `GCGlobalMechanisms`,
`gc_heap_compact_reason`, `MarkRootType` and `Gens` are TraceEvent's enums from
`Microsoft.Diagnostics.Tracing.Parsers.Clr`.

## `EventPipeProvider`

```csharp
EventPipeProvider(string name, EventLevel eventLevel, long keywords = 0xF00000000000,
                  IDictionary<string,string> arguments = null);
long Keywords { get; }  EventLevel EventLevel { get; }
string Name { get; }    IDictionary<string,string> Arguments { get; }
```

## Not public

`SamplingProfiler`, `TraceCollector` and the IPC internals are internal. Everything you need
is reachable through `MemoryIntrospector`, the option/result types and the static helpers
above.
