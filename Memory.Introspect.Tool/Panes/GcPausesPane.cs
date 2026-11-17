using Memory.Introspect.GcPauses;
using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>
/// Watches the process for long garbage-collection pauses and explains each one as it ends. The
/// monitor is cheap enough to stay on for hours; Cancel stops it.
/// </summary>
internal sealed class GcPausesPane : CapturePane
{
    private sealed record PauseRow(string Time, string Pause, string Suspend, string Generation, string Reason, string Cause);

    private readonly TextField _threshold;
    private readonly TextField _minutes;
    private readonly TextField _captures;
    private readonly CheckBox  _details;
    private readonly CheckBox  _gcdump;
    private readonly Label     _stats;
    private readonly TableView _table;
    private readonly LogView   _explanation;

    private GcPauseMonitor?          _monitor;
    private IReadOnlyList<GcPause>   _pauses = Array.Empty<GcPause>();

    public GcPausesPane(ToolContext context) : base(context, "GC Pauses", "Start monitoring", 5)
    {
        _threshold = ToolViews.Field("200");
        _minutes   = ToolViews.Field("60");
        _captures  = ToolViews.Field("3");
        _details   = ToolViews.Check("After a long pause, capture the next one in detail (stacks, allocations, pinning)", true);
        _gcdump    = ToolViews.Check("Also take a heap snapshot (one more full GC, often longer than the pause)", false);

        ToolViews.Row(Form, 0, "Long pause (ms)", _threshold);
        ToolViews.Row(Form, 1, "Watch for (minutes)",       _minutes);
        ToolViews.Row(Form, 2, "Detailed captures (max)",   _captures);
        ToolViews.Row(Form, 3, "",                          _details);
        ToolViews.Row(Form, 4, "",                          _gcdump);

        _stats = new Label() { X = 0, Y = 0, Width = Dim.Fill(), Height = 1, Text = "Not monitoring." };

        var tableFrame = ToolViews.FocusableFrame("Long pauses, newest first");

        tableFrame.X      = 0;
        tableFrame.Y      = 1;
        tableFrame.Width  = Dim.Fill();
        tableFrame.Height = Dim.Percent(55);

        _table = new TableView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        _table.ValueChanged += (_, _) => ShowExplanation();
        tableFrame.Add(_table);

        var explanationFrame = ToolViews.FocusableFrame("Why the selected pause was long");

        explanationFrame.X      = 0;
        explanationFrame.Y      = Pos.Bottom(tableFrame);
        explanationFrame.Width  = Dim.Fill();
        explanationFrame.Height = Dim.Fill();

        _explanation = new LogView();
        explanationFrame.Add(_explanation);

        ResultFrame.Add(_stats, tableFrame, explanationFrame);
    }

    protected override string CancelledText => "Stopped";

    protected override Func<CaptureRun, Task>? Prepare()
    {
        if (!TryInt(_threshold, "Threshold", 1, 600_000, out var threshold)) return null;
        if (!TryInt(_minutes, "Watch time", 1, 100_000, out var minutes)) return null;
        if (!TryInt(_captures, "Detailed captures", 0, 100, out var captures)) return null;

        var details = _details.Value == CheckState.Checked;
        var gcdump  = _gcdump.Value == CheckState.Checked;

        _pauses = Array.Empty<GcPause>();
        ToolViews.SetTable(_table, Array.Empty<PauseRow>(), Columns);
        _explanation.Clear();

        return async run =>
        {
            var introspector = run.CreateIntrospector();

            var options = new GcPauseMonitorOptions()
            {
                LongPauseThreshold = TimeSpan.FromMilliseconds(threshold),
                OnLongPause        = pause => run.Log("Long pause: " + pause.Summary),
                DetailedCapture    = new GcPauseDetailedCaptureOptions()
                {
                    Enabled         = details && captures > 0,
                    MaxCaptures     = captures,
                    CollectGcDump   = gcdump,
                    OutputDirectory = Path.GetFullPath(run.Context.OutputDirectory),
                    OnReport        = report => run.Log($"Detailed capture finished: {report.TraceFilePath} ({report.Pauses.Count} pauses)")
                }
            };

            Directory.CreateDirectory(options.DetailedCapture.OutputDirectory);

            var monitor = await introspector.StartGcPauseMonitorAsync(run.Pid, options, run.Token).ConfigureAwait(false);

            try
            {
                run.Ui(() => _monitor = monitor);
                run.Log($"Watching for pauses of {threshold} ms or more, for {minutes} minutes.");

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(minutes), run.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    //Cancel is how the monitor is stopped, not a failure.
                }

                run.Log("Stopping the monitor (a detailed capture in progress is finished first)...");
            }
            finally
            {
                await monitor.DisposeAsync().ConfigureAwait(false);

                run.Ui(() =>
                {
                    Refresh(monitor);
                    _monitor = null;
                });
            }
        };
    }

    public override void Tick()
    {
        base.Tick();

        if (_monitor is not null) Refresh(_monitor);
    }

    private void Refresh(GcPauseMonitor monitor)
    {
        _stats.Text = ToolFormat.Fit(
            $"{monitor.PauseCount:N0} pauses, {monitor.LongPauseCount:N0} long   max {monitor.MaxPause.TotalMilliseconds:N0} ms   total {monitor.TotalPauseTime.TotalMilliseconds:N0} ms   lost events {monitor.EventsLost:N0}{(monitor.IsCapturingDetails ? "   capturing in detail" : "")}",
            Math.Max(0, Viewport.Width - 4));

        var pauses = monitor.RecentLongPauses.Reverse().ToList();

        if (pauses.Count == _pauses.Count && (pauses.Count == 0 || ReferenceEquals(pauses[0], _pauses[0]))) return;

        var selected = _table.Value?.SelectedCell.Y ?? -1;

        _pauses = pauses;

        ToolViews.SetTable(_table, pauses.Select(p => new PauseRow(
            p.StartTimeUtc.ToLocalTime().ToString("HH:mm:ss"),
            $"{p.PauseMSec:N0} ms",
            $"{p.SuspendMSec:N0} ms",
            p.Gc is null ? "-" : "gen" + p.Gc.Generation,
            p.Gc is null ? p.SuspendReason.ToString() : p.Gc.Reason.ToString(),
            p.Cause.ToString())).ToList(), Columns);

        if (selected < 0 && pauses.Count > 0) ShowExplanation();
    }

    private void ShowExplanation()
    {
        var row = _table.Value?.SelectedCell.Y ?? -1;

        if (row < 0 || row >= _pauses.Count)
        {
            _explanation.Clear();
            return;
        }

        _explanation.Replace(new[] { _pauses[row].ToReport() });
    }

    private static Dictionary<string, Func<PauseRow, object>> Columns => new()
    {
        { "Time",    r => r.Time },
        { "Pause",   r => r.Pause },
        { "Suspend", r => r.Suspend },
        { "Gen",     r => r.Generation },
        { "Reason",  r => r.Reason },
        { "Cause",   r => r.Cause }
    };
}
