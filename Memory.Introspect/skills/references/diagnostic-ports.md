---
name: diagnostic-ports
description: Connect Memory.Introspect through a diagnostic port instead of a process id — the DiagnosticPort option, connect vs listen modes, DOTNET_DiagnosticPorts and DOTNET_DefaultDiagnosticPortSuspend, ResumeRuntime, and tracing containerised or startup-time processes. Use when a PID is not reachable or you need to trace application startup.
---

# Diagnostic ports

Normally the library finds a process through the default diagnostics endpoint — a named pipe
on Windows, a Unix domain socket in `TMPDIR` elsewhere — keyed by process id. A **diagnostic
port** is an alternative endpoint the target is configured to expose, which solves two
problems a PID cannot:

- **Reachability** — the target is in another container or namespace, and its default endpoint
  is not visible to you.
- **Startup** — you need events from the first milliseconds, before you could have learned the
  PID.

## Configuring it

```csharp
// For every capture from this introspector
var introspector = MemoryIntrospector.Create(new MemoryIntrospectorOptions
{
    Logger         = logger,
    DiagnosticPort = "/tmp/myapp-diag.sock",
});

// Or per trace (overrides the introspector's value)
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration       = TimeSpan.FromSeconds(30),
    DiagnosticPort = "/tmp/myapp-diag.sock,connect",
    OutputPath     = "startup.nettrace",
});
```

When a diagnostic port is set, it is used instead of the process id — `processId` is still
required by the signature but is not what the connection is made on. Pass the real PID anyway;
it is recorded on the result.

## Format

```
<address>[,connect|,listen]
```

- **address** — a Unix domain socket path (`/tmp/myapp-diag.sock`), a named pipe name on
  Windows, or a `ws://`/`wss://` URI for the WebSocket transport.
- **`connect`** — *we* connect to a port the target is listening on.
- **`listen`** — *we* listen and the target connects to us. This is the reverse-connect mode
  used for startup tracing.
- When the suffix is omitted, `listen` is assumed.

Transport defaults to a named pipe on Windows and a Unix domain socket elsewhere; a `ws://`
address selects the WebSocket transport. A malformed value throws `FormatException`.

## Target-side configuration

The target opts in through environment variables (set on the **traced** process):

```bash
# The target listens on this port; the collector uses ",connect"
DOTNET_DiagnosticPorts=/tmp/myapp-diag.sock

# The target connects out to a listening collector, and suspends at startup until
# a session is established — nothing is missed
DOTNET_DiagnosticPorts=/tmp/collector.sock,suspend
DOTNET_DefaultDiagnosticPortSuspend=1
```

## Startup tracing and ResumeRuntime

A runtime suspended at startup waits for a diagnostics connection before running any managed
code. That gives you a trace with nothing missing — but the process stays frozen until
something resumes it. That something is `ResumeRuntime`:

```csharp
var trace = await introspector.CollectTraceAsync(pid, new TraceCollectionOptions
{
    Duration       = TimeSpan.FromSeconds(30),
    Profiles       = TraceProfileKind.DotNetCommon,
    DiagnosticPort = "/tmp/collector.sock,listen",
    ResumeRuntime  = true,           // let the app start once the session is up
    OutputPath     = "startup.nettrace",
});
```

`ResumeRuntime = true` is the `--resume-runtime` switch. It is a no-op on runtimes that do not
support the command (notably .NET Core 3.1), so it is safe to set unconditionally. **Forget it
on a suspended target and the app never starts.**

## Containers

Tracing a process in another container generally means either sharing the socket directory as
a volume:

```yaml
# target container
environment:
  - DOTNET_DiagnosticPorts=/diag/app.sock
volumes:
  - diag:/diag

# collector container
volumes:
  - diag:/diag        # then DiagnosticPort = "/diag/app.sock,connect"
```

…or running the capture **inside** the target container against `Environment.ProcessId`, which
avoids the problem entirely (`self-tracing.md`).

## Which captures support it

`DiagnosticPort` on `MemoryIntrospectorOptions` applies to traces, sampling profiles,
`.gcdump` collection and `.dmp` collection. `TraceCollectionOptions.DiagnosticPort` overrides
it for that one trace.

## Related

- `self-tracing.md` — usually the simpler answer in a container
- `getting-started.md` — the default endpoint and its privilege rules
- `troubleshooting.md` — connection failures
