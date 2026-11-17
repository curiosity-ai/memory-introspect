using Memory.Introspect.Trace;
using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>An EventPipe trace, the equivalent of <c>dotnet-trace collect</c>.</summary>
internal sealed class TracePane : CapturePane
{
    private static readonly (string Label, TraceProfileKind Kind)[] _profiles =
    {
        ("Default (common + sampled thread time)", TraceProfileKind.Default),
        (".NET common (GC, JIT, loader, exceptions)", TraceProfileKind.DotNetCommon),
        ("Sampled thread time", TraceProfileKind.DotNetSampledThreadTime),
        ("GC verbose (collections + allocations)", TraceProfileKind.GcVerbose),
        ("GC collect (low overhead)", TraceProfileKind.GcCollect),
        ("Database (ADO.NET / EF Core)", TraceProfileKind.Database)
    };

    private static readonly (string Label, TraceFileFormat Format)[] _formats =
    {
        ("nettrace (PerfView / Visual Studio)", TraceFileFormat.NetTrace),
        ("nettrace + speedscope", TraceFileFormat.Speedscope),
        ("nettrace + chromium (Perfetto)", TraceFileFormat.Chromium)
    };

    private readonly DropDownList _profile;
    private readonly DropDownList _format;
    private readonly TextField    _duration;
    private readonly TextField    _buffer;
    private readonly CheckBox     _rundown;
    private readonly LogView      _summary;

    public TracePane(ToolContext context) : base(context, "Trace", "Trace", 5)
    {
        _profile  = ToolViews.Dropdown(_profiles.Select(p => p.Label).ToArray());
        _format   = ToolViews.Dropdown(_formats.Select(f => f.Label).ToArray());
        _duration = ToolViews.Field("15");
        _buffer   = ToolViews.Field("1024");
        _rundown  = ToolViews.Check("Rundown (resolves jitted method names; slower to stop)", true);

        ToolViews.Row(Form, 0, "Profile",             _profile);
        ToolViews.Row(Form, 1, "Output format",       _format);
        ToolViews.Row(Form, 2, "Duration (seconds)",  _duration);
        ToolViews.Row(Form, 3, "Circular buffer (MB)", _buffer);
        ToolViews.Row(Form, 4, "",                    _rundown);

        _summary = new LogView();
        ResultFrame.Add(_summary);
    }

    protected override Func<CaptureRun, Task>? Prepare()
    {
        if (!TryInt(_duration, "Duration", 1, 86400, out var seconds)) return null;
        if (!TryInt(_buffer, "Circular buffer", 1, 65536, out var buffer)) return null;

        var profile = _profiles.FirstOrDefault(p => p.Label == _profile.Text);
        var format  = _formats.FirstOrDefault(f => f.Label == _format.Text);
        var rundown = _rundown.Value == CheckState.Checked;

        if (profile.Label is null) profile = _profiles[0];
        if (format.Label is null) format   = _formats[0];

        _summary.Clear();

        return async run =>
        {
            var introspector = run.CreateIntrospector(buffer);

            var options = new TraceCollectionOptions()
            {
                Profiles               = profile.Kind,
                Duration               = TimeSpan.FromSeconds(seconds),
                CircularBufferSizeInMB = buffer,
                Rundown                = rundown,
                OutputPath             = run.Context.NewOutputPath("trace", ".nettrace"),
                Format                 = format.Format,
                Progress               = new Progress<TraceProgress>(p => run.Ui(() => _summary.Replace(new[] { $"Recording... {ToolFormat.Duration(p.Elapsed)}, {ToolFormat.Bytes(p.SizeInBytes)} written" })))
            };

            var result = await introspector.CollectTraceAsync(run.Pid, options, run.Token).ConfigureAwait(false);

            if (result.Exception is not null) throw result.Exception;
            if (!result.Success) throw new InvalidOperationException("The trace could not be collected.");

            var lines = new List<string>()
            {
                "Saved:    " + result.TraceFilePath,
                "Size:     " + ToolFormat.Bytes(result.TraceSizeInBytes),
                "Recorded: " + ToolFormat.Duration(result.Elapsed)
            };

            if (!string.IsNullOrEmpty(result.ConvertedFilePath)) lines.Add("Converted: " + result.ConvertedFilePath);
            if (result.StoppedByStoppingEvent) lines.Add("Stopped by the stopping event.");

            run.Ui(() => _summary.Replace(lines));
        };
    }
}
