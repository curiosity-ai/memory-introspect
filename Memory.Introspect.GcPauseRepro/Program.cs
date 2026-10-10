using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Memory.Introspect;
using Memory.Introspect.GcPauses;

// Reproduces long GC pauses and checks that Memory.Introspect can explain them.
//
//   * a ConcurrentDictionary holding millions of nodes that point at themselves and at random
//     other nodes: a large, highly connected object graph that is expensive to mark;
//   * mutator threads that keep rewiring the graph and churning garbage, so gen2 fragments;
//   * a timer that calls GC.Collect(2, Forced, blocking, compacting) with LOH compaction on;
//   * a heartbeat thread that sleeps 1 s and reports how long it really slept, which is how an
//     application would notice a stall without any tooling;
//   * a GcPauseMonitor on the same process, which should explain every stall the heartbeat sees.
//
// Usage: dotnet run -c Release -- [--nodes N] [--links K] [--duration S] [--compact-every S]
//                                 [--threshold MS] [--monitor none|basic|detailed|always-detailed] [--gcdump]
//                                 [--mutators N] [--out DIR] [--quiet]

var options = ReproOptions.Parse(args);
var clock = Stopwatch.StartNew();

void Log(string message) => Console.WriteLine($"[{clock.Elapsed.TotalSeconds,7:0.000}s] {message}");

Log($"pid {Environment.ProcessId}, {Environment.ProcessorCount} cores, server GC: {GCSettings.IsServerGC}, latency mode: {GCSettings.LatencyMode}, runtime {Environment.Version}");
Log($"config: {options}");

// ---- build the graph -------------------------------------------------------------------------

var graph = Graph.Build(options.Nodes, options.Links, Log);
Log($"heap after build: {GC.GetTotalMemory(false) / 1024.0 / 1024.0:N0} MB, gen2 GCs so far {GC.CollectionCount(2)}");

// ---- monitor ---------------------------------------------------------------------------------

GcPauseMonitor? monitor = null;
var heartbeatStalls = new ConcurrentQueue<(DateTime StartUtc, DateTime EndUtc, double SleptMSec)>();
var observedPauses = new ConcurrentQueue<GcPause>();

if (options.Monitor != MonitorMode.None)
{
    // The graph has ~16 M objects; let a heap snapshot (--gcdump) see all of them.
    var introspector = MemoryIntrospector.Create(new() { Logger = null, Verbose = false, MaxNodeCount = 50_000_000 });
    var monitorOptions = new GcPauseMonitorOptions
    {
        LongPauseThreshold = TimeSpan.FromMilliseconds(options.ThresholdMSec),
        OnPause = observedPauses.Enqueue,
        OnLongPause = pause =>
        {
            // How long after the pause ended the monitor got to explain it.
            double lagMSec = (DateTime.UtcNow - pause.StartTimeUtc).TotalMilliseconds - pause.PauseMSec;
            Log($"[gc-monitor] LONG PAUSE (reported {lagMSec:N0} ms after it ended)\n" + pause.ToReport());
        },
        DetailedCapture = new GcPauseDetailedCaptureOptions
        {
            Enabled = options.Monitor == MonitorMode.Detailed,
            // Manual captures (--monitor always-detailed) use the same settings.
            MaxCaptures = 1,
            MaxDuration = TimeSpan.FromSeconds(60),
            StopAfterLongPause = TimeSpan.FromSeconds(2),
            CollectStacks = true,
            VerboseGcEvents = true,
            CollectGcDump = options.GcDump,
            OutputDirectory = options.OutputDirectory,
            OnReport = report => Log("[gc-monitor] detailed capture finished\n" + report.ToReport()),
        },
    };

    var sw = Stopwatch.StartNew();
    monitor = await introspector.StartGcPauseMonitorAsync(Environment.ProcessId, monitorOptions);
    Log($"[gc-monitor] started in {sw.ElapsedMilliseconds} ms (stack walk on always-on session: {monitor.StackwalkEnabled})");

    if (options.Monitor == MonitorMode.AlwaysDetailed)
    {
        // Overhead measurement: keep the expensive detailed session on for the whole run.
        _ = monitor.CaptureDetailsAsync(TimeSpan.FromSeconds(options.DurationSeconds));
    }
}

// ---- workload ----------------------------------------------------------------------------------

using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(options.DurationSeconds));

var heartbeat = new Thread(() => Heartbeat(stop.Token)) { IsBackground = true, Name = "heartbeat" };
heartbeat.Start();

long mutations = 0;
var mutators = Enumerable.Range(0, options.Mutators)
    .Select(i => new Thread(() => Interlocked.Add(ref mutations, graph.Mutate(seed: 1000 + i, stop.Token))) { IsBackground = true, Name = $"mutator-{i}" })
    .ToList();
mutators.ForEach(t => t.Start());

var largeObjects = new Thread(() => graph.ChurnLargeObjects(stop.Token)) { IsBackground = true, Name = "loh-churn" };
largeObjects.Start();

var compactions = new ConcurrentQueue<double>();
using var compactor = new Timer(_ => CompactorTick(), null, TimeSpan.FromSeconds(options.CompactEverySeconds), TimeSpan.FromSeconds(options.CompactEverySeconds));

TimeSpan pauseBefore = GC.GetTotalPauseDuration();
TimeSpan cpuBefore = Process.GetCurrentProcess().TotalProcessorTime;
int gen2Before = GC.CollectionCount(2);
var workloadClock = Stopwatch.StartNew();

try { await Task.Delay(-1, stop.Token); } catch (OperationCanceledException) { }

compactor.Change(Timeout.Infinite, Timeout.Infinite);
mutators.ForEach(t => t.Join());
largeObjects.Join();
heartbeat.Join();
workloadClock.Stop();
TimeSpan cpu = Process.GetCurrentProcess().TotalProcessorTime - cpuBefore;

// ---- summary -----------------------------------------------------------------------------------

var stalls = heartbeatStalls.ToArray();
TimeSpan paused = GC.GetTotalPauseDuration() - pauseBefore;

Console.WriteLine();
Log("=== summary ===");
Log($"workload ran {workloadClock.Elapsed.TotalSeconds:N1} s, {mutations:N0} graph mutations ({mutations / workloadClock.Elapsed.TotalSeconds:N0}/s)");
Log($"GC: {GC.CollectionCount(0)} gen0 / {GC.CollectionCount(1)} gen1 / {GC.CollectionCount(2)} gen2 total, {GC.CollectionCount(2) - gen2Before} gen2 during workload, " +
    $"paused {paused.TotalMilliseconds:N0} ms ({paused.TotalMilliseconds * 100 / workloadClock.Elapsed.TotalMilliseconds:0.0}% of wall clock)");
GCMemoryInfo lastFull = GC.GetGCMemoryInfo(GCKind.FullBlocking);
Log($"last full blocking GC #{lastFull.Index}: pause {string.Join(" + ", lastFull.PauseDurations.ToArray().Select(p => $"{p.TotalMilliseconds:N0} ms"))}, " +
    $"promoted {lastFull.PromotedBytes / 1024.0 / 1024.0:N0} MB, compacted {lastFull.Compacted}, heap {lastFull.HeapSizeBytes / 1024.0 / 1024.0:N0} MB");
double[] compactionMSec = compactions.Order().ToArray();
if (compactionMSec.Length > 0)
{
    Log($"induced compacting gen2 GCs: {compactionMSec.Length}, median {compactionMSec[compactionMSec.Length / 2]:N0} ms, min {compactionMSec[0]:N0} ms, max {compactionMSec[^1]:N0} ms " +
        $"[{string.Join(", ", compactions.Select(c => c.ToString("N0")))}]");
}
Log($"process CPU during workload: {cpu.TotalSeconds:N1} s ({cpu.TotalMilliseconds / workloadClock.Elapsed.TotalMilliseconds * 100 / Environment.ProcessorCount:0.0}% of {Environment.ProcessorCount} cores)");
Log($"heartbeat stalls (slept >= 1000 + {options.ThresholdMSec} ms): {stalls.Length}, worst {(stalls.Length == 0 ? 0 : stalls.Max(s => s.SleptMSec)):N0} ms");

if (monitor is not null)
{
    // Stopping the monitor drains the event stream, so the last pause is in before correlating.
    var stopSw = Stopwatch.StartNew();
    await monitor.DisposeAsync();
    Log($"monitor stopped in {stopSw.ElapsedMilliseconds} ms; detailed reports: {monitor.DetailedReports.Count}");

    GcPause[] pauses = observedPauses.ToArray();
    Log($"monitor: {monitor.PauseCount:N0} pauses, {monitor.LongPauseCount:N0} long, max {monitor.MaxPause.TotalMilliseconds:N0} ms, total {monitor.TotalPauseTime.TotalMilliseconds:N0} ms, " +
        $"{monitor.EventsProcessed:N0} events read, {monitor.EventsLost:N0} lost");

    // Every heartbeat stall should line up with the pauses the monitor saw while the heartbeat
    // was asleep: the extra time it slept should be (mostly) time the runtime was paused.
    int explained = 0;
    foreach (var stall in stalls)
    {
        GcPause[] inside = pauses
            .Where(p => p.StartTimeUtc >= stall.StartUtc.AddMilliseconds(-20) && p.StartTimeUtc <= stall.EndUtc)
            .ToArray();
        double pausedMSec = inside.Sum(p => p.PauseMSec);
        double extraMSec = stall.SleptMSec - 1000;
        GcPause? longest = inside.MaxBy(p => p.PauseMSec);
        bool isExplained = pausedMSec >= 0.8 * extraMSec;
        explained += isExplained ? 1 : 0;
        Log($"  stall at {stall.StartUtc:HH:mm:ss.fff}Z: {extraMSec:N0} ms late; {inside.Length} pauses totalling {pausedMSec:N0} ms during it" +
            (longest is null ? string.Empty : $", longest: {longest.Summary}") +
            (isExplained ? string.Empty : "  <- NOT explained by GC pauses"));
    }
    Log($"{explained} of {stalls.Length} heartbeat stalls explained by the GC monitor");
}

GC.KeepAlive(graph);
return;

// ---- helpers -----------------------------------------------------------------------------------

// The heartbeat sleeps for one second and reports how long it actually slept. Any extra time
// was spent with the thread unable to run: for a whole-process stall, a GC pause.
void Heartbeat(CancellationToken ct)
{
    long beat = 0;
    while (!ct.IsCancellationRequested)
    {
        DateTime startUtc = DateTime.UtcNow;
        long start = Stopwatch.GetTimestamp();
        Thread.Sleep(1000);
        double slept = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        beat++;

        if (slept >= 1000 + options.ThresholdMSec)
        {
            heartbeatStalls.Enqueue((startUtc, DateTime.UtcNow, slept));
            Log($"[heartbeat {beat}] STALL: slept {slept:N0} ms instead of 1000 ms");
        }
        else if (!options.Quiet)
        {
            Log($"[heartbeat {beat}] +{slept:N0} ms");
        }
    }
}

// Deliberately the worst thing one can do on a big heap: a forced, blocking, compacting gen2 GC
// that also compacts the large object heap.
[MethodImpl(MethodImplOptions.NoInlining)]
void CompactorTick()
{
    long start = Stopwatch.GetTimestamp();
    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
    GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
    compactions.Enqueue(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
    Log($"[compactor] GC.Collect(2, Forced, blocking, compacting) + LOH compaction returned after {Stopwatch.GetElapsedTime(start).TotalMilliseconds:N0} ms");
}

enum MonitorMode { None, Basic, Detailed, AlwaysDetailed }

sealed record ReproOptions
{
    public int Nodes { get; init; } = 5_000_000;
    public int Links { get; init; } = 4;
    public int DurationSeconds { get; init; } = 75;
    public int CompactEverySeconds { get; init; } = 10;
    public int ThresholdMSec { get; init; } = 1000;
    public MonitorMode Monitor { get; init; } = MonitorMode.Detailed;
    public bool GcDump { get; init; }
    public int Mutators { get; init; } = 2;
    public string? OutputDirectory { get; init; }
    public bool Quiet { get; init; }

    public static ReproOptions Parse(string[] args)
    {
        var o = new ReproOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string Next() => args[++i];
            o = args[i] switch
            {
                "--nodes" => o with { Nodes = int.Parse(Next()) },
                "--links" => o with { Links = int.Parse(Next()) },
                "--duration" => o with { DurationSeconds = int.Parse(Next()) },
                "--compact-every" => o with { CompactEverySeconds = int.Parse(Next()) },
                "--threshold" => o with { ThresholdMSec = int.Parse(Next()) },
                "--monitor" => o with { Monitor = Enum.Parse<MonitorMode>(Next().Replace("-", string.Empty), ignoreCase: true) },
                "--gcdump" => o with { GcDump = true },
                "--mutators" => o with { Mutators = int.Parse(Next()) },
                "--out" => o with { OutputDirectory = Next() },
                "--quiet" => o with { Quiet = true },
                _ => throw new ArgumentException($"Unknown argument '{args[i]}'"),
            };
        }
        return o;
    }
}

/// <summary>A node that references itself and a handful of random other nodes.</summary>
sealed class Node
{
    public readonly long Id;
    public Node Self;
    public Node?[] Links;
    public string? Label;

    public Node(long id, int links)
    {
        Id = id;
        Self = this;
        Links = new Node?[links];
        Label = id % 4 == 0 ? "node-" + id : null;
    }
}

sealed class Graph
{
    private readonly ConcurrentDictionary<long, Node> _nodes;
    private readonly int _count;
    private readonly int _links;
    private readonly byte[][] _largeObjects = new byte[256][];
    private readonly GCHandle[] _pinned = new GCHandle[512];

    private Graph(int count, int links)
    {
        _count = count;
        _links = links;
        _nodes = new ConcurrentDictionary<long, Node>(Environment.ProcessorCount, count);
    }

    public static Graph Build(int count, int links, Action<string> log)
    {
        var sw = Stopwatch.StartNew();
        var graph = new Graph(count, links);

        Parallel.For(0, Environment.ProcessorCount, worker =>
        {
            for (long id = worker; id < count; id += Environment.ProcessorCount)
            {
                graph._nodes[id] = new Node(id, links);
            }
        });
        log($"created {count:N0} nodes in {sw.ElapsedMilliseconds:N0} ms");

        // Wire every node to random others: no locality at all, lots of cycles.
        Parallel.For(0, Environment.ProcessorCount, worker =>
        {
            var random = new Random(worker);
            for (long id = worker; id < count; id += Environment.ProcessorCount)
            {
                Node node = graph._nodes[id];
                for (int l = 0; l < links; l++)
                {
                    node.Links[l] = graph._nodes[random.NextInt64(count)];
                }
            }
        });
        log($"wired {count * (long)links:N0} references in {sw.ElapsedMilliseconds:N0} ms");

        for (int i = 0; i < graph._largeObjects.Length; i++)
        {
            graph._largeObjects[i] = new byte[256 * 1024];
        }

        // A few long-lived pinned buffers, as a networking stack would have.
        for (int i = 0; i < graph._pinned.Length; i++)
        {
            graph._pinned[i] = GCHandle.Alloc(new byte[4096], GCHandleType.Pinned);
        }

        return graph;
    }

    /// <summary>
    /// Rewires the graph and churns garbage: each step replaces a node's link array (the old one
    /// dies in gen2, fragmenting it), points an old node at another node, and allocates a little
    /// short-lived garbage. Returns the number of steps done.
    /// </summary>
    public long Mutate(int seed, CancellationToken ct)
    {
        var random = new Random(seed);
        long steps = 0;
        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < 1_000; i++)
            {
                Node node = _nodes[random.NextInt64(_count)];
                var links = new Node?[_links];
                for (int l = 0; l < links.Length; l++)
                {
                    links[l] = _nodes[random.NextInt64(_count)];
                }
                node.Links = links;

                if ((i & 7) == 0)
                {
                    node.Label = "relabel-" + steps;
                }

                _ = new List<long>(8) { node.Id, steps };
                steps++;
            }
            Thread.Sleep(20);
        }
        return steps;
    }

    /// <summary>Replaces large buffers now and then, so the LOH fragments.</summary>
    public void ChurnLargeObjects(CancellationToken ct)
    {
        var random = new Random(7);
        while (!ct.IsCancellationRequested)
        {
            _largeObjects[random.Next(_largeObjects.Length)] = new byte[random.Next(100_000, 512 * 1024)];
            Thread.Sleep(20);
        }
    }
}
