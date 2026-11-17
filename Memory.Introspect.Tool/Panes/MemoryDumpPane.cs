using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>The .gcdump (managed heap graph) and .dmp (process dump) captures.</summary>
internal sealed class MemoryDumpPane : CapturePane
{
    private static readonly string[] _types =
    {
        "Memory graph (.gcdump)",
        "Mini dump (.dmp)",
        "Heap dump (.dmp)",
        "Triage dump (.dmp)",
        "Full dump (.dmp)"
    };

    private readonly DropDownList _type;
    private readonly TextField    _maxNodes;
    private readonly TextField    _buffer;
    private readonly CheckBox     _large;
    private readonly LogView      _summary;

    public MemoryDumpPane(ToolContext context) : base(context, "Memory Dump", "Capture", 5)
    {
        _type     = ToolViews.Dropdown(_types);
        _maxNodes = ToolViews.Field("10000000");
        _buffer   = ToolViews.Field("2048");
        _large    = ToolViews.Check("Expect a very large heap (uses more memory, faster)", false);

        ToolViews.Row(Form, 0, "Dump type",             _type);
        ToolViews.Row(Form, 1, "Max nodes (gcdump)",    _maxNodes);
        ToolViews.Row(Form, 2, "Circular buffer (MB)",  _buffer);
        ToolViews.Row(Form, 3, "",                      _large);

        Form.Add(new Label() { X = 0, Y = 4, Width = Dim.Fill(), Height = 1, Text = "Open a .gcdump in Visual Studio or PerfView, a .dmp in WinDbg or lldb." });

        _summary = new LogView();
        ResultFrame.Add(_summary);
    }

    protected override Func<CaptureRun, Task>? Prepare()
    {
        if (!TryInt(_maxNodes, "Max nodes", 1000, int.MaxValue, out var maxNodes)) return null;
        if (!TryInt(_buffer, "Circular buffer", 1, 65536, out var buffer)) return null;

        var index = Array.IndexOf(_types, _type.Text);
        var large = _large.Value == CheckState.Checked;

        if (index < 0) index = 0;

        _summary.Clear();

        return async run =>
        {
            var introspector = run.CreateIntrospector(buffer, maxNodes, large);

            if (index == 0)
            {
                await CaptureGcDumpAsync(run, introspector).ConfigureAwait(false);
                return;
            }

            var kind = index switch
            {
                1 => Dumper.CollectionType.Mini,
                2 => Dumper.CollectionType.Heap,
                3 => Dumper.CollectionType.Triage,
                _ => Dumper.CollectionType.Full
            };

            var path = run.Context.NewOutputPath("dump", ".dmp");

            run.Log("A process dump cannot be interrupted once started.");

            var exitCode = await introspector.DumpAsync(run.Pid, path, kind).ConfigureAwait(false);

            if (exitCode != 0 || !File.Exists(path))
            {
                throw new InvalidOperationException($"The dump failed (exit code {exitCode}). Check that the tool and the process run as the same user.");
            }

            Summarize(run, path);
        };
    }

    private async Task CaptureGcDumpAsync(CaptureRun run, MemoryIntrospector introspector)
    {
        var result = await introspector.CollectMemoryGraphAsync(run.Pid, run.Token).ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.Cancelled) throw new OperationCanceledException();
            if (result.Exception is not null) throw result.Exception;

            throw new InvalidOperationException(result.Timeouted
                ? "Timed out while building the memory graph. Raise the circular buffer or the max nodes."
                : result.NoHeapFound ? "The process has no managed heap to dump." : "The memory graph could not be collected.");
        }

        var path = run.Context.NewOutputPath("heap", ".gcdump");

        result.SaveToDisk(path);

        run.Log($"Memory graph: {result.Graph.NodeCount:N0} objects in {result.Graph.NodeTypeCount:N0} types.");

        Summarize(run, path);
    }

    private void Summarize(CaptureRun run, string path)
    {
        var size = new FileInfo(path).Length;

        run.Ui(() => _summary.Replace(new[]
        {
            "Saved: " + path,
            "Size:  " + ToolFormat.Bytes(size)
        }));
    }
}
