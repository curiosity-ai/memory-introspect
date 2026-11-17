using System.ComponentModel;
using System.Diagnostics;
using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Panes;

/// <summary>Picks the process every other pane works on, and the folder their captures go to.</summary>
internal sealed class TargetPane : ToolPane
{
    private sealed record ProcessRow(int Pid, string Name, long WorkingSet, string Started, string Command);

    private readonly TableView _table;
    private readonly Label     _selected;
    private readonly Label     _hint;

    private IReadOnlyList<ProcessRow> _rows = Array.Empty<ProcessRow>();

    public TargetPane(ToolContext context) : base(context, "Target")
    {
        var output = ToolViews.Field(context.OutputDirectory, 40);

        output.ValueChanged += (_, _) => Context.OutputDirectory = output.Text?.Trim() ?? "";

        var folder = ToolViews.Frame("Captures are written to");

        folder.X      = 0;
        folder.Y      = 0;
        folder.Width  = Dim.Fill();
        folder.Height = 3;

        output.X = 0;
        output.Y = 0;
        output.Width = Dim.Fill();
        folder.Add(output);

        var refresh = ToolViews.Button("Refresh", Refresh);
        var use     = ToolViews.Button("Use selected process", UseSelected);

        refresh.X = 0;
        refresh.Y = 3;
        use.X     = Pos.Right(refresh) + 1;
        use.Y     = 3;

        _selected = new Label() { X = Pos.Right(use) + 2, Y = 3, Width = Dim.Fill(), Height = 1, Text = "" };

        var tableFrame = ToolViews.FocusableFrame(".NET processes that can be inspected");

        tableFrame.X      = 0;
        tableFrame.Y      = 4;
        tableFrame.Width  = Dim.Fill();
        tableFrame.Height = Dim.Fill(1);

        _table = new TableView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill(), FullRowSelect = true };
        tableFrame.Add(_table);

        _hint = new Label() { X = 0, Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1, Text = "Select a row, then press the button." };

        Add(folder, refresh, use, _selected, tableFrame, _hint);

        Context.TargetChanged += ShowTarget;

        ShowTarget();
    }

    public override void Shown() => Refresh();

    private void ShowTarget()
    {
        _selected.Text = "Target: " + Context.TargetText;
    }

    private void UseSelected()
    {
        var row = _table.Value?.SelectedCell.Y ?? -1;

        if (row < 0 || row >= _rows.Count) return;

        Context.SelectProcess(_rows[row].Pid);
    }

    private void Refresh()
    {
        _ = Task.Run(() =>
        {
            var rows = new List<ProcessRow>();
            var self = Environment.ProcessId;

            foreach (var pid in MemoryIntrospector.GetTraceableProcesses())
            {
                if (pid == self) continue;

                rows.Add(Describe(pid));
            }

            rows.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            Context.App.Invoke(() =>
            {
                _rows = rows;

                ToolViews.SetTable(_table, _rows, new Dictionary<string, Func<ProcessRow, object>>()
                {
                    { "PID",         r => r.Pid },
                    { "Process",     r => r.Name },
                    { "Memory",      r => ToolFormat.Bytes(r.WorkingSet) },
                    { "Started",     r => r.Started },
                    { "Command",     r => r.Command }
                });

                if (rows.Count == 0) _hint.Text = "No inspectable .NET process found. Start the application first, then refresh.";
            });
        });
    }

    private static ProcessRow Describe(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            return new ProcessRow(pid, process.ProcessName, process.WorkingSet64, process.StartTime.ToString("MM-dd HH:mm"), CommandOf(pid));
        }
        catch (ArgumentException)
        {
            return new ProcessRow(pid, "exited", 0, "", "");
        }
        catch (InvalidOperationException)
        {
            return new ProcessRow(pid, "exited", 0, "", "");
        }
        catch (Win32Exception)
        {
            //Another user's process: it is listed, the details just are not ours to read.
            return new ProcessRow(pid, ToolContext.NameOf(pid), 0, "", "");
        }
    }

    /// <summary>
    /// What a <c>dotnet</c> process runs, which its name does not say. Read from /proc where there is
    /// one; elsewhere an application's own executable name is already the answer.
    /// </summary>
    private static string CommandOf(int pid)
    {
        var file = $"/proc/{pid}/cmdline";

        if (!File.Exists(file)) return "";

        try
        {
            return File.ReadAllText(file).Replace('\0', ' ').Trim();
        }
        catch (IOException)
        {
            return "";
        }
        catch (UnauthorizedAccessException)
        {
            return "";
        }
    }
}
