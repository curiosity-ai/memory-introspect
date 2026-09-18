---
name: custom-eventsource
description: Trace your own EventSource with Memory.Introspect — declaring the EventSource, enabling it by provider name alongside runtime events, keyword and level filtering, and using its events to drive a stopping event. Use when you want application-level events in a .nettrace next to runtime events.
---

# Tracing your own EventSource

Your application's own `EventSource` is just another EventPipe provider. Enabling it in a
trace puts application-level events on the same timeline as GC, JIT and exception events,
which is what makes a trace explain *why* something was slow rather than just *that* it was.

## Declare the EventSource

```csharp
using System.Diagnostics.Tracing;

[EventSource(Name = ProviderName)]
internal sealed class AppEventSource : EventSource
{
    public const string ProviderName = "MyCompany-MyApp";

    public static readonly AppEventSource Log = new();
    private AppEventSource() { }

    [Event(1, Level = EventLevel.Informational)]
    public void RequestStart(string route) => WriteEvent(1, route);

    [Event(2, Level = EventLevel.Informational)]
    public void RequestStop(string route, int statusCode, double elapsedMs)
        => WriteEvent(2, route, statusCode, elapsedMs);

    [Event(3, Level = EventLevel.Warning)]
    public void RequestFailed(string route, int statusCode) => WriteEvent(3, route, statusCode);
}
```

The provider name — `[EventSource(Name = …)]`, or the class name with `.` replaced by `-` when
you do not set one — is what you enable in the trace. Get it wrong and the trace succeeds with
no events from it, silently.

## Enable it in a capture

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration  = TimeSpan.FromSeconds(30),
    Providers = new[]
    {
        $"{AppEventSource.ProviderName}:0x0:4",     // all keywords, Informational
    },
    // …alongside the runtime, so app events sit next to GC/exception events
    Profiles   = TraceProfileKind.DotNetCommon,
    OutputPath = "app.nettrace",
});

// Confirm it was actually enabled
bool enabled = trace.Providers.Any(p => p.Name == AppEventSource.ProviderName);
```

`0x0` means "the provider's default keywords" — for a simple `EventSource` with no `Keywords`
class, that is everything. The `:4` is `EventLevel.Informational`; raise it to `5` for
Verbose events, lower it to `3` to keep only warnings and worse.

## Keyword filtering

If your `EventSource` declares keywords, they filter the same way the runtime provider's do:

```csharp
internal sealed class AppEventSource : EventSource
{
    public static class Keywords
    {
        public const EventKeywords Requests = (EventKeywords)0x1;
        public const EventKeywords Database = (EventKeywords)0x2;
        public const EventKeywords Cache    = (EventKeywords)0x4;
    }

    [Event(1, Level = EventLevel.Informational, Keywords = Keywords.Requests)]
    public void RequestStart(string route) => WriteEvent(1, route);
}
```

```csharp
Providers = new[] { $"{AppEventSource.ProviderName}:0x5:4" },   // Requests | Cache, not Database
```

Or strongly typed:

```csharp
ProviderConfigurations = new[]
{
    new EventPipeProvider(AppEventSource.ProviderName, EventLevel.Informational,
        keywords: (long)(AppEventSource.Keywords.Requests | AppEventSource.Keywords.Cache)),
},
```

## Provider arguments

Some providers take key/value arguments — `Microsoft-Diagnostics-DiagnosticSource` uses
`FilterAndPayloadSpecs`, which is how the `database` profile captures EF Core command text.
The spec-string form is `Name:Keywords:Level:[key1=value1][;key2=value2]`; the typed form is
the `arguments` parameter of `EventPipeProvider`.

## Stopping a trace on your own event

Application events make excellent trace terminators — record continuously, stop the moment
something goes wrong:

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration                   = TimeSpan.FromMinutes(5),      // upper bound
    Providers                  = new[] { $"{AppEventSource.ProviderName}:0x0:4" },
    StoppingEventProviderName  = AppEventSource.ProviderName,
    StoppingEventEventName     = "RequestFailed",
    StoppingEventPayloadFilter = new Dictionary<string, string> { ["statusCode"] = "500" },
    OutputPath                 = "failure.nettrace",
});
```

The payload filter keys are the **parameter names** of the `[Event]` method
(`RequestFailed(string route, int statusCode)` → `route`, `statusCode`), compared as strings.
See `stopping-events.md` for the mismatch detection.

## Related

- `stopping-events.md` — the full stopping-event contract
- `providers-and-clrevents.md` — provider spec syntax and merging
- `self-tracing.md` — an app tracing its own EventSource in-process
