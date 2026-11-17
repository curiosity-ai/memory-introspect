using Memory.Introspect.Tool.Widgets;
using Memory.Introspect.Trace;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>Which types the process allocated over a short window, and optionally from which call stacks.</summary>
internal sealed class AllocationsPane : CapturePane
{
    private sealed record TypeRow(string Bytes, string Percent, string Objects, string Large, string Type);

    private readonly TextField _duration;
    private readonly TextField _count;
    private readonly CheckBox  _stacks;
    private readonly TableView _table;
    private readonly LogView   _details;

    private AllocationReport? _report;

    public AllocationsPane(ToolContext context) : base(context, "Allocations", "Capture", 3)
    {
        _duration = ToolViews.Field("10");
        _count    = ToolViews.Field("25");
        _stacks   = ToolViews.Check("Resolve call stacks (slower, larger trace)", false);

        ToolViews.Row(Form, 0, "Duration (seconds)", _duration);
        ToolViews.Row(Form, 1, "Top types",          _count);
        ToolViews.Row(Form, 2, "",                   _stacks);

        var tableFrame = ToolViews.FocusableFrame("Allocated types");

        tableFrame.X      = 0;
        tableFrame.Y      = 0;
        tableFrame.Width  = Dim.Fill();
        tableFrame.Height = Dim.Percent(55);

        _table = new TableView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        _table.ValueChanged += (_, _) => ShowStacks();
        tableFrame.Add(_table);

        var detailsFrame = ToolViews.FocusableFrame("Call stacks of the selected type");

        detailsFrame.X      = 0;
        detailsFrame.Y      = Pos.Bottom(tableFrame);
        detailsFrame.Width  = Dim.Fill();
        detailsFrame.Height = Dim.Fill();

        _details = new LogView();
        detailsFrame.Add(_details);

        ResultFrame.Add(tableFrame, detailsFrame);
    }

    protected override Func<CaptureRun, Task>? Prepare()
    {
        if (!TryInt(_duration, "Duration", 1, 3600, out var seconds)) return null;
        if (!TryInt(_count, "Top types", 1, 1000, out var count)) return null;

        var stacks = _stacks.Value == CheckState.Checked;

        _report = null;

        ToolViews.SetTable(_table, Array.Empty<TypeRow>(), Columns);
        _details.Clear();

        return async run =>
        {
            var introspector = run.CreateIntrospector();
            var path         = run.Context.NewOutputPath("allocations", ".nettrace");

            var report = await introspector.CollectAllocationReportAsync(run.Pid, TimeSpan.FromSeconds(seconds), count, path, stacks, run.Token).ConfigureAwait(false);

            run.Log($"Saved {path}.");
            run.Log($"{ToolFormat.Bytes(report.TotalAllocatedBytes)} sampled in {report.SampleCount:N0} samples across {report.DistinctTypeCount:N0} types.");

            if (report.Types.Count == 0) run.Log("No allocations were sampled. Is the process allocating?");

            var rows = report.Types.Select(t => new TypeRow(
                ToolFormat.Bytes(t.AllocatedBytes),
                ToolFormat.Percent(t.AllocatedBytesPercent),
                t.ObjectCount.ToString("N0"),
                ToolFormat.Bytes(t.LargeObjectHeapBytes),
                t.TypeName)).ToList();

            run.Ui(() =>
            {
                _report = report;

                ToolViews.SetTable(_table, rows, Columns);
                ShowStacks();
            });
        };
    }

    private void ShowStacks()
    {
        var row = _table.Value?.SelectedCell.Y ?? -1;

        if (_report is null || row < 0 || row >= _report.Types.Count)
        {
            _details.Clear();
            return;
        }

        if (!_report.HasCallStacks)
        {
            _details.Replace(new[] { "Capture again with 'Resolve call stacks' to see where these allocations come from." });
            return;
        }

        var type  = _report.Types[row].TypeName;
        var lines = new List<string>();

        foreach (var stack in _report.CallStacks.Where(s => s.TypeName == type))
        {
            lines.Add($"{ToolFormat.Bytes(stack.AllocatedBytes)} ({ToolFormat.Percent(stack.AllocatedBytesPercent)})");

            foreach (var frame in stack.Frames) lines.Add("    " + frame);
        }

        if (lines.Count == 0) lines.Add("No call stack was resolved for this type.");

        _details.Replace(lines);
    }

    private static Dictionary<string, Func<TypeRow, object>> Columns => new()
    {
        { "Allocated", r => r.Bytes },
        { "%",         r => r.Percent },
        { "Objects",   r => r.Objects },
        { "LOH",       r => r.Large },
        { "Type",      r => r.Type }
    };
}
