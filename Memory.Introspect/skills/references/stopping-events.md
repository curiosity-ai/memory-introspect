---
name: stopping-events
description: End a Memory.Introspect trace when a specific event is observed instead of when a timer expires — StoppingEventProviderName, StoppingEventEventName, StoppingEventPayloadFilter, the StoppedByStoppingEvent and StoppingEventPayloadFilterMismatched result flags. Use when capturing the run-up to a failure or a rare condition.
---

# Stopping on an event instead of a timer

A duration-based trace is a guess: too short and you miss the incident, too long and you get a
gigabyte of noise. A **stopping event** records continuously and cuts the capture the instant
a nominated event is seen — so the trace ends with the thing you care about.

## The three options

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration                   = TimeSpan.FromMinutes(5),   // still an upper bound
    Providers                  = new[] { "MyCompany-MyApp:0x0:4" },
    StoppingEventProviderName  = "MyCompany-MyApp",
    StoppingEventEventName     = "RequestFailed",
    StoppingEventPayloadFilter = new Dictionary<string, string> { ["statusCode"] = "500" },
    OutputPath                 = "failure.nettrace",
});

if (trace.StoppedByStoppingEvent)
{
    // the trace was cut short by the event, and ends with it
}
```

| Option | CLI equivalent | Required |
| --- | --- | --- |
| `StoppingEventProviderName` | `--stopping-event-provider-name` | Yes, to stop on an event at all |
| `StoppingEventEventName` | `--stopping-event-event-name` | Optional on its own; **required** if you use a payload filter |
| `StoppingEventPayloadFilter` | `--stopping-event-payload-filter` | Optional; needs both of the above |

**The stopping-event provider must also be enabled in the trace.** The collector watches the
event stream it is already recording; a provider that is not enabled emits nothing to watch.

Always keep a `Duration` as a backstop, or the capture runs until cancellation or process exit
if the event never arrives.

## Payload filters

Keys are the payload field names — the parameter names of the `[Event]` method — and values
are compared as strings:

```csharp
[Event(3, Level = EventLevel.Warning)]
public void RequestFailed(string route, int statusCode) => WriteEvent(3, route, statusCode);
```

```csharp
StoppingEventPayloadFilter = new Dictionary<string, string>
{
    ["route"]      = "/api/checkout",
    ["statusCode"] = "500",
},
```

All entries must match for the event to stop the trace.

## Mismatch detection

A filter naming fields the event does not have could never match, and would silently hang
until the duration expired. That case is detected and reported instead:

```csharp
var result = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration                   = TimeSpan.FromSeconds(30),
    Providers                  = new[] { "MyCompany-MyApp:0x0:4" },
    StoppingEventProviderName  = "MyCompany-MyApp",
    StoppingEventEventName     = "RequestFailed",
    StoppingEventPayloadFilter = new Dictionary<string, string> { ["nosuchfield"] = "x" },
});

// result.StoppingEventPayloadFilterMismatched == true
// result.StoppedByStoppingEvent              == false
// the trace still ran the full duration and is still usable
```

So after any stopping-event capture, the useful check is:

```csharp
if (trace.StoppingEventPayloadFilterMismatched)
    logger.LogWarning("payload filter names fields the event does not have — check the [Event] signature");
else if (!trace.StoppedByStoppingEvent)
    logger.LogInformation("the stopping event never fired; trace ran the full {0:0.##}s", trace.Elapsed.TotalSeconds);
```

## Stopping event vs cancellation

| | Stopping event | `CancellationToken` |
| --- | --- | --- |
| Triggered by | An event the target emits | Your own code |
| Needs the condition observable as an event | Yes | No |
| Works when the interesting process is not the one running the capture | Yes | Only if you can detect it from outside |
| Result flag | `StoppedByStoppingEvent` | `Cancelled` |

Both keep the data captured so far. Use whichever matches where the knowledge lives — and they
compose: a token for "the operator said stop" plus a stopping event for "the app hit the
error".

## Related

- `custom-eventsource.md` — making your app emit events worth stopping on
- `trace-collect.md` — the rest of the options
