using System.Collections.ObjectModel;
using Memory.Introspect.Tool.Widgets;
using Memory.Introspect.Trace;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>Reports on a .nettrace file captured earlier - here, with another tool, or on another machine.</summary>
internal sealed class AnalyzePane : ToolPane
{
    private readonly TextField                    _path;
    private readonly TextField                    _count;
    private readonly TextField                    _threshold;
    private readonly CheckBox                     _stacks;
    private readonly ListView                     _files;
    private readonly ObservableCollection<string> _fileNames = new();
    private readonly Label                        _status;
    private readonly LogView                      _output;
    private readonly List<Button>                 _buttons = new();

    private string[] _filePaths = Array.Empty<string>();
    private bool     _running;

    public AnalyzePane(ToolContext context) : base(context, "Analyze")
    {
        _path      = new TextField() { X = 12, Y = 0, Width = Dim.Fill(), Height = 1 };
        _count     = ToolViews.Field("25", 6);
        _threshold = ToolViews.Field("200", 6);
        _stacks    = ToolViews.Check("Call stacks", false);

        _count.X     = 12;
        _count.Y     = 1;
        _threshold.X = 38;
        _threshold.Y = 1;
        _stacks.X    = 46;
        _stacks.Y    = 1;

        var topMethods  = Action("Top methods",    () => Report(TopMethods));
        var allocations = Action("Allocations",    () => Report(Allocations));
        var gcPauses    = Action("GC pauses",      () => Report(GcPauses));
        var speedscope  = Action("To speedscope",  () => Report(p => Convert(p, TraceFileFormat.Speedscope)));
        var chromium    = Action("To chromium",    () => Report(p => Convert(p, TraceFileFormat.Chromium)));

        topMethods.X  = 0;
        gcPauses.X    = Pos.Right(topMethods) + 1;
        allocations.X = Pos.Right(gcPauses) + 1;
        speedscope.X  = Pos.Right(allocations) + 1;
        chromium.X    = Pos.Right(speedscope) + 1;

        foreach (var button in new[] { topMethods, allocations, gcPauses, speedscope, chromium })
        {
            button.Y = 2;
        }

        _status = new Label() { X = 0, Y = 3, Width = Dim.Fill(), Height = 1, Text = "Pick a trace below or type its path." };

        var filesFrame = ToolViews.FocusableFrame("Traces in the output folder, newest first");

        filesFrame.X      = 0;
        filesFrame.Y      = 4;
        filesFrame.Width  = Dim.Fill();
        filesFrame.Height = 6;

        _files = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _files.SetSource(_fileNames);
        _files.ValueChanged += (_, _) => PickFile();
        filesFrame.Add(_files);

        var outputFrame = ToolViews.FocusableFrame("Report");

        outputFrame.X      = 0;
        outputFrame.Y      = Pos.Bottom(filesFrame);
        outputFrame.Width  = Dim.Fill();
        outputFrame.Height = Dim.Fill();

        _output = new LogView();
        outputFrame.Add(_output);

        Add(
            new Label() { X = 0, Y = 0, Text = "Trace file" }, _path,
            new Label() { X = 0, Y = 1, Text = "Top N" }, _count,
            new Label() { X = 20, Y = 1, Text = "Long pause (ms)" }, _threshold, _stacks,
            topMethods, gcPauses, allocations, speedscope, chromium,
            _status, filesFrame, outputFrame);
    }

    public override bool IsBusy => _running;

    public override void Shown() => RefreshFiles();

    private Button Action(string text, System.Action onPress)
    {
        var button = ToolViews.Button(text, onPress);

        _buttons.Add(button);

        return button;
    }

    private void RefreshFiles()
    {
        var folder = Context.OutputDirectory;

        _filePaths = Directory.Exists(folder)
            ? new DirectoryInfo(folder).GetFiles("*.nettrace").OrderByDescending(f => f.LastWriteTimeUtc).Select(f => f.FullName).ToArray()
            : Array.Empty<string>();

        _fileNames.Clear();

        foreach (var file in _filePaths) _fileNames.Add($"{Path.GetFileName(file)}  ({ToolFormat.Bytes(new FileInfo(file).Length)})");
    }

    private void PickFile()
    {
        var index = _files.SelectedItem ?? -1;

        if (index >= 0 && index < _filePaths.Length) _path.Text = _filePaths[index];
    }

    private void Report(Func<string, string[]> analysis)
    {
        if (_running) return;

        var path = _path.Text?.Trim().Trim('"') ?? "";

        if (!File.Exists(path))
        {
            _status.Text = "That file does not exist.";
            return;
        }

        _running = true;

        foreach (var button in _buttons) button.Enabled = false;

        _status.Text = "Analysing... a large trace can take a while.";
        _output.Clear();

        _ = Task.Run(() =>
        {
            string[] lines;
            string   status;

            try
            {
                lines  = analysis(path);
                status = "Done.";
            }
            catch (Exception e)
            {
                lines  = new[] { $"{e.GetType().Name}: {e.Message}" };
                status = "Failed.";
            }

            Context.App.Invoke(() =>
            {
                _running = false;

                foreach (var button in _buttons) button.Enabled = true;

                _status.Text = status;
                _output.Replace(lines);
            });
        });
    }

    private MemoryIntrospector Introspector() => Context.CreateIntrospector(_ => { });

    private int Count() => int.TryParse(_count.Text?.Trim(), out var n) && n > 0 ? n : 25;

    private string[] TopMethods(string path)
    {
        var methods = Introspector().ReportTopMethods(path, Count());
        var writer  = new StringWriter();

        TraceReport.WriteTopMethodsReport(writer, methods);

        return writer.ToString().Split('\n');
    }

    private string[] Allocations(string path)
    {
        var report = Introspector().ReportTopAllocatedTypes(path, Count(), _stacks.Value == CheckState.Checked);
        var lines  = new List<string>() { $"{ToolFormat.Bytes(report.TotalAllocatedBytes)} sampled in {report.SampleCount:N0} samples across {report.DistinctTypeCount:N0} types", "" };

        foreach (var type in report.Types)
        {
            lines.Add($"{ToolFormat.Bytes(type.AllocatedBytes),10}  {ToolFormat.Percent(type.AllocatedBytesPercent),6}  {type.TypeName}");
        }

        foreach (var stack in report.CallStacks)
        {
            lines.Add("");
            lines.Add($"{ToolFormat.Bytes(stack.AllocatedBytes)} ({ToolFormat.Percent(stack.AllocatedBytesPercent)}) {stack.TypeName}");

            foreach (var frame in stack.Frames) lines.Add("    " + frame);
        }

        return lines.ToArray();
    }

    private string[] GcPauses(string path)
    {
        var threshold = int.TryParse(_threshold.Text?.Trim(), out var ms) && ms > 0 ? ms : 200;
        var report    = Introspector().ReportGcPauses(path, TimeSpan.FromMilliseconds(threshold), _stacks.Value == CheckState.Checked);

        return report.ToReport().Split('\n');
    }

    private string[] Convert(string path, TraceFileFormat format)
    {
        var written = Introspector().ConvertTraceFile(path, format);

        return new[] { "Written: " + written };
    }
}
