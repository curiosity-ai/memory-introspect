using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>A CPU sampling profile: where the process spent its time, by method.</summary>
internal sealed class CpuProfilePane : CapturePane
{
    private sealed record MethodRow(string SelfPercent, string TotalPercent, string Method);

    private readonly TextField _duration;
    private readonly TextField _count;
    private readonly CheckBox  _inclusive;
    private readonly TableView _table;

    public CpuProfilePane(ToolContext context) : base(context, "CPU Profile", "Profile", 3)
    {
        _duration  = ToolViews.Field("15");
        _count     = ToolViews.Field("25");
        _inclusive = ToolViews.Check("Rank by inclusive time (callers first)", false);

        ToolViews.Row(Form, 0, "Duration (seconds)", _duration);
        ToolViews.Row(Form, 1, "Top methods",        _count);
        ToolViews.Row(Form, 2, "",                   _inclusive);

        _table = new TableView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        ResultFrame.Add(_table);
    }

    protected override Func<CaptureRun, Task>? Prepare()
    {
        if (!TryInt(_duration, "Duration", 1, 3600, out var seconds)) return null;
        if (!TryInt(_count, "Top methods", 1, 1000, out var count)) return null;

        var inclusive = _inclusive.Value == CheckState.Checked;

        ToolViews.SetTable(_table, Array.Empty<MethodRow>(), Columns);

        return async run =>
        {
            var introspector = run.CreateIntrospector();
            var result       = await introspector.CollectSamplingProfileAsync(run.Pid, TimeSpan.FromSeconds(seconds), run.Token).ConfigureAwait(false);

            if (result.Exception is not null) throw result.Exception;
            if (result.Cancelled) throw new OperationCanceledException();
            if (!result.Success) throw new InvalidOperationException("The profile could not be collected.");

            var path = run.Context.NewOutputPath("cpu", ".nettrace");

            result.SaveToDisk(path);

            run.Log($"Saved {path} ({ToolFormat.Bytes(result.TraceSizeInBytes)}).");

            var methods = result.TopMethods(count, inclusive);
            var rows    = methods.Select(m => new MethodRow(ToolFormat.Percent(m.ExclusiveMetricPercent), ToolFormat.Percent(m.InclusiveMetricPercent), m.Name)).ToList();

            run.Ui(() => ToolViews.SetTable(_table, rows, Columns));
        };
    }

    private static Dictionary<string, Func<MethodRow, object>> Columns => new()
    {
        { "Self",   r => r.SelfPercent },
        { "Total",  r => r.TotalPercent },
        { "Method", r => r.Method }
    };
}
