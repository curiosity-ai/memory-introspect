---
name: process-dump
description: Capture .dmp process dumps from .NET code with Memory.Introspect's DumpAsync and Dumper.CollectionType (Full, Heap, Mini, Triage), including return-code handling, sizing, PII considerations and how to analyse the result. Use when you need a debugger-grade snapshot of a process.
---

# Process dumps (`.dmp`)

A process dump is a debugger-grade snapshot: threads, stacks, exception state, and — depending
on the type — the managed and native heaps. It is the heaviest artifact the library produces
and the only one that answers native-side questions.

## Signature

```csharp
Task<int> DumpAsync(int pid, string targetPath, Dumper.CollectionType collectionType);
```

Returns **`0` on success and `-1` on failure** — it is a return code, not an exception. Errors
are written to the logger, so configure one.

```csharp
using Memory.Introspect;

var file = $"{DateTimeOffset.UtcNow:yyyy-MM-dd-HH-mm-ss}-process-{pid}.dmp";
int rc = await introspector.DumpAsync(pid, file, Dumper.CollectionType.Mini);

if (rc != 0)
{
    logger.LogError("dump failed with code {0} — check privileges and disk space", rc);
    return;
}

logger.LogInformation("Wrote {0} ({1:N0} bytes)", file, new FileInfo(file).Length);
```

## The four collection types

```csharp
public enum Dumper.CollectionType { Full, Heap, Mini, Triage }
```

| Type | Contains | Size | Use it for |
| --- | --- | --- | --- |
| `Mini` | Module list, thread list, exception information, all stacks | Smallest | "What was it doing when it hung/crashed" — the default choice |
| `Triage` | Same as `Mini`, **with PII removed** | Smallest | Dumps that leave your infrastructure — support, vendors, bug trackers |
| `Heap` | Module and thread lists, all stacks, exception and handle information, all memory except mapped images | Large | Managed-object inspection in a debugger; most memory investigations |
| `Full` | Everything, including module images | Largest | Native debugging where module images matter |

Start at `Mini`. Move to `Heap` when you need to inspect objects. Reach for `Full` only when a
native debugging need actually demands it — on a large process it can be tens of gigabytes.

## Cost

The target is suspended while the dump is written, and the file is written synchronously. For
`Heap` and `Full` that means a pause proportional to the process's memory, and a file
proportional to it too. Check free disk space before dumping a large process — a failed write
partway through wastes the pause and leaves a truncated file.

## Privileges

Dumping **another** process needs the same user, or root/Administrator. On Linux the target
must be reachable through the diagnostics socket in `TMPDIR`; in containers, the dump has to
be written to a path the process can actually see. Dumping the **current** process usually
needs nothing special.

## PII

`Full` and `Heap` dumps contain live memory: connection strings, tokens, request bodies,
personal data. Treat them as production secrets — do not attach them to public issues. Use
`Triage` when a dump has to leave a controlled environment.

## Analysing

- **Visual Studio** — open the `.dmp` directly; "Debug with Managed Only" for a .NET process.
- **`dotnet-dump analyze <file>`** — SOS commands (`clrstack`, `dumpheap -stat`, `gcroot`,
  `pstacks`) cross-platform.
- **WinDbg** with SOS, for native-side work.

Note the library ports dump *collection*, not analysis — use the tools above on the resulting
file.

## Choosing between the artifact types

| Question | Artifact |
| --- | --- |
| Which methods are hot? | Sampling profile |
| What is being allocated, and where from? | Allocation report + call stacks |
| What is alive and who holds it? | `.gcdump` |
| What were all the threads doing right now? | `.dmp` (`Mini`) |
| What objects exist, inspectable in a debugger? | `.dmp` (`Heap`) |
| Native memory / interop / runtime-level crash? | `.dmp` (`Full`) |

## Related

- `gc-dump.md` — a much cheaper answer to most managed-memory questions
- `getting-started.md` — privilege and platform requirements
