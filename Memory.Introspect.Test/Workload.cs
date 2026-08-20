using System.Diagnostics.Tracing;

namespace Memory.Introspect.Test;

/// <summary>
/// A custom EventSource emitted by the child workload, used to exercise provider
/// specifications and the stopping-event feature of the trace collector.
/// </summary>
[EventSource(Name = WorkloadEventSource.ProviderName)]
internal sealed class WorkloadEventSource : EventSource
{
    public const string ProviderName = "Memory-Introspect-TestWorkload";

    public static readonly WorkloadEventSource Log = new();

    private WorkloadEventSource() { }

    [Event(1, Level = EventLevel.Informational)]
    public void Milestone(int iteration, string phase) => WriteEvent(1, iteration, phase);
}

/// <summary>
/// The work the child process performs while it is being traced: CPU burn, allocation churn
/// that forces real GCs, exceptions, some blocking threads, and a steady stream of custom
/// EventSource events.
/// </summary>
internal static class Workload
{
    public static async Task RunAsync()
    {
        var stop = new CancellationTokenSource();
        var mres = new ManualResetEventSlim(false);
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { stop.Cancel(); mres.Set(); };

        var tasks = new[]
        {
            Task.Run(() => SpinHot(stop.Token)),
            Task.Run(() => SpinWarm(stop.Token)),
            Task.Run(() => AllocateGarbage(stop.Token)),
            Task.Run(() => ThrowAndCatch(stop.Token)),
            Task.Run(() => BlockedOnMres(mres, stop.Token)),
            Task.Run(() => BlockedOnMonitor(stop.Token)),
            Task.Run(() => EmitMilestones(stop.Token)),
        };

        try { await Task.Delay(-1, stop.Token); } catch { }
        try { await Task.WhenAll(tasks); } catch { }
    }

    private static void SpinHot(CancellationToken ct)
    {
        double x = 1.0001;
        while (!ct.IsCancellationRequested)
        {
            for (int i = 0; i < 10_000; i++) x = Math.Sqrt(x + i);
        }
    }

    private static void SpinWarm(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested) Thread.SpinWait(1_000_000);
    }

    // Allocates enough short- and mid-lived objects to keep gen0/gen1 collections coming, so
    // the GC keywords in the traced profiles actually produce events.
    private static void AllocateGarbage(CancellationToken ct)
    {
        var survivors = new List<byte[]>();
        int i = 0;
        while (!ct.IsCancellationRequested)
        {
            for (int j = 0; j < 200; j++)
            {
                _ = new byte[8 * 1024];
            }

            survivors.Add(new byte[64 * 1024]);
            if (survivors.Count > 256) survivors.RemoveRange(0, 128);

            if ((++i % 50) == 0) Thread.Sleep(1);
        }

        GC.KeepAlive(survivors);
    }

    private static void ThrowAndCatch(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                throw new InvalidOperationException("workload exception");
            }
            catch (InvalidOperationException)
            {
            }
            Thread.Sleep(20);
        }
    }

    private static void BlockedOnMres(ManualResetEventSlim mres, CancellationToken ct)
    {
        try { mres.Wait(ct); } catch (OperationCanceledException) { }
    }

    private static void BlockedOnMonitor(CancellationToken ct)
    {
        var gate = new object();
        lock (gate)
        {
            while (!ct.IsCancellationRequested) Monitor.Wait(gate, 1000);
        }
    }

    private static void EmitMilestones(CancellationToken ct)
    {
        int iteration = 0;
        while (!ct.IsCancellationRequested)
        {
            WorkloadEventSource.Log.Milestone(iteration++, "working");
            Thread.Sleep(250);
        }
    }
}
