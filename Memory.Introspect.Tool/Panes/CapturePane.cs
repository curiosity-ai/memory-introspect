using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>What a capture is given to work with: the target, a way to say what it is doing and a token to stop on.</summary>
internal sealed class CaptureRun
{
    private readonly CapturePane _pane;

    public CaptureRun(CapturePane pane, ToolContext context, CancellationToken token)
    {
        _pane   = pane;
        Context = context;
        Token   = token;
    }

    public ToolContext       Context { get; }
    public CancellationToken Token   { get; }

    /// <summary>Appends a line to the pane's log. Safe from any thread.</summary>
    public void Log(string line) => _pane.LogFromAnyThread(line);

    /// <summary>Runs <paramref name="action"/> on the interface's own thread.</summary>
    public void Ui(Action action) => Context.App.Invoke(action);

    public int Pid => Context.ProcessId ?? 0;

    public MemoryIntrospector CreateIntrospector(int circularBufferMB = 1024, int maxNodes = 10_000_000, bool expectLargeGraph = false)
    {
        return Context.CreateIntrospector(Log, circularBufferMB, maxNodes, expectLargeGraph);
    }
}

/// <summary>
/// A pane with an options form on top, Start / Cancel under it, a result area and a log. The subclass
/// reads its form in <see cref="Prepare"/> (on the interface thread) and returns the work to run, so the
/// work itself never touches a control.
/// </summary>
internal abstract class CapturePane : ToolPane
{
    private const int LOG_HEIGHT = 6;

    private readonly Button    _start;
    private readonly Button    _cancel;
    private readonly Label     _status;
    private readonly FrameView _form;

    private CancellationTokenSource? _cts;
    private DateTime                 _startedUtc;

    protected CapturePane(ToolContext context, string title, string runText, int formHeight) : base(context, title)
    {
        _form = ToolViews.FocusableFrame("Options");

        _form.X      = 0;
        _form.Y      = 0;
        _form.Width  = Dim.Fill();
        _form.Height = formHeight + 2;

        _start  = ToolViews.Button(runText + " (Ctrl+R)", Run);
        _cancel = ToolViews.Button("Cancel (F4)", Cancel);

        _start.X  = 0;
        _start.Y  = Pos.Bottom(_form);
        _cancel.X = Pos.Right(_start) + 1;
        _cancel.Y = Pos.Bottom(_form);
        _cancel.Enabled = false;

        _status = new Label() { X = Pos.Right(_cancel) + 2, Y = Pos.Bottom(_form), Width = Dim.Fill(), Height = 1, Text = "Idle" };

        ResultFrame = ToolViews.FocusableFrame("Result");

        ResultFrame.X      = 0;
        ResultFrame.Y      = Pos.Bottom(_form) + 1;
        ResultFrame.Width  = Dim.Fill();
        ResultFrame.Height = Dim.Fill(LOG_HEIGHT + 2);

        var logFrame = ToolViews.FocusableFrame("Log");

        logFrame.X      = 0;
        logFrame.Y      = Pos.AnchorEnd(LOG_HEIGHT + 2);
        logFrame.Width  = Dim.Fill();
        logFrame.Height = LOG_HEIGHT + 2;

        Log = new LogView();
        logFrame.Add(Log);

        Add(_form, _start, _cancel, _status, ResultFrame, logFrame);
    }

    /// <summary>The frame the subclass puts its form controls in.</summary>
    protected FrameView Form => _form;

    /// <summary>The frame the subclass puts its result views in.</summary>
    protected FrameView ResultFrame { get; }

    protected LogView Log { get; }

    public override bool CanRun => true;

    /// <summary>How the status line words a run the user ended with Cancel; a monitor says "Stopped".</summary>
    protected virtual string CancelledText => "Cancelled";

    public override bool IsBusy => _cts is not null;

    /// <summary>
    /// Reads and validates the form. Returns the work to run, or null after calling <see cref="Reject"/>
    /// - the work runs on a background thread, so it must capture values, not controls.
    /// </summary>
    protected abstract Func<CaptureRun, Task>? Prepare();

    protected void Reject(string message) => SetStatus(message);

    protected bool TryInt(TextField field, string name, int min, int max, out int value)
    {
        if (int.TryParse(field.Text?.Trim(), out value) && value >= min && value <= max) return true;

        Reject($"{name} must be a whole number from {min} to {max}.");

        return false;
    }

    public override void Run()
    {
        if (_cts is not null) return;

        if (Context.ProcessId is null)
        {
            Reject("Select a process on the Target pane first.");
            return;
        }

        var work = Prepare();

        if (work is null) return;

        _cts        = new CancellationTokenSource();
        _startedUtc = DateTime.UtcNow;

        var run = new CaptureRun(this, Context, _cts.Token);

        Log.Clear();
        SetRunning(true);
        LogLine($"Started {PaneTitle} on {Context.TargetText}.");

        _ = Task.Run(async () =>
        {
            string outcome;

            try
            {
                await work(run).ConfigureAwait(false);

                outcome = run.Token.IsCancellationRequested ? CancelledText : "Done";
            }
            catch (OperationCanceledException)
            {
                outcome = CancelledText;
            }
            catch (Exception e)
            {
                run.Log($"Failed: {e.GetType().Name}: {e.Message}");

                outcome = "Failed";
            }

            Context.App.Invoke(() =>
            {
                _cts?.Dispose();
                _cts = null;

                SetRunning(false);
                SetStatus($"{outcome} after {ToolFormat.Duration(DateTime.UtcNow - _startedUtc)}.");
                LogLine(outcome + ".");
            });
        });
    }

    public override void Cancel()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;

        _cts.Cancel();
        SetStatus("Cancelling...");
    }

    public override void Tick()
    {
        if (_cts is null || _cts.IsCancellationRequested) return;

        SetStatus($"Running... {ToolFormat.Duration(DateTime.UtcNow - _startedUtc)}");
    }

    public void LogFromAnyThread(string line) => Context.App.Invoke(() => LogLine(line));

    protected void LogLine(string line)
    {
        Log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
    }

    protected void SetStatus(string text) => _status.Text = text;

    private void SetRunning(bool running)
    {
        _start.Enabled  = !running;
        _cancel.Enabled = running;
    }
}
