using System.Collections.ObjectModel;
using Memory.Introspect.Tool.Panes;
using Memory.Introspect.Tool.Widgets;
using Terminal.Gui.Drawing;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool;

/// <summary>The whole interface: a list of panes on the left, the selected one on the right.</summary>
internal sealed class ToolWindow : Window
{
    private const int NAVIGATION_WIDTH = 16;

    private readonly ToolContext                  _context;
    private readonly List<ToolPane>               _panes     = new();
    private readonly ObservableCollection<string> _paneNames = new();

    private readonly Label    _header;
    private readonly ListView _navigation;

    private ToolPane? _active;

    public ToolWindow(ToolContext context)
    {
        _context = context;

        Title       = "Memory.Introspect  -  .NET memory and CPU diagnostics";
        BorderStyle = LineStyle.Rounded;

        _header = new Label() { X = 1, Y = 0, Width = Dim.Fill(1), Height = 1, Text = "" };

        var navigationFrame = ToolViews.FocusableFrame("Views");

        navigationFrame.X      = 0;
        navigationFrame.Y      = 1;
        navigationFrame.Width  = NAVIGATION_WIDTH;
        navigationFrame.Height = Dim.Fill(1);

        _navigation = new ListView() { X = 0, Y = 0, Width = Dim.Fill(), Height = Dim.Fill() };
        _navigation.SetSource(_paneNames);
        _navigation.ValueChanged += (_, _) => ShowSelectedPane();
        navigationFrame.Add(_navigation);

        AddPane(new TargetPane(context));
        AddPane(new MemoryDumpPane(context));
        AddPane(new CpuProfilePane(context));
        AddPane(new TracePane(context));
        AddPane(new AllocationsPane(context));
        AddPane(new GcPausesPane(context));
        AddPane(new AnalyzePane(context));

        var statusBar = new StatusBar() { Y = Pos.AnchorEnd(1) };

        statusBar.Add(
            new Shortcut() { Title = "Run",    Key = Key.F2, BindKeyToApplication = true, Action = () => _active?.Run() },
            new Shortcut() { Title = "Cancel", Key = Key.F4, BindKeyToApplication = true, Action = () => _active?.Cancel() },
            new Shortcut() { Title = "Quit",   Key = Key.F10, BindKeyToApplication = true, Action = ConfirmQuit });

        Add(_header, navigationFrame, statusBar);

        _context.TargetChanged += UpdateHeader;

        UpdateHeader();

        //With a process given on the command line the target is already chosen, so start on the first capture.
        _navigation.SelectedItem = context.ProcessId is null ? 0 : 1;
        ShowSelectedPane();

        //Start on the view list so the arrow keys change pane straight away.
        _navigation.SetFocus();
    }

    /// <summary>
    /// Terminal.Gui's Tab wraps within one group and leaves crossing groups to F6. This window is one screen
    /// of controls rather than a form of separate sections, so Tab walks all of them: the view list first,
    /// then whatever the visible pane holds.
    /// </summary>
    protected override bool OnKeyDown(Key key)
    {
        if (key == Key.Tab) return MoveFocus(forward: true);
        if (key == Key.Tab.WithShift) return MoveFocus(forward: false);

        return base.OnKeyDown(key);
    }

    public void Tick() => _active?.Tick();

    private bool MoveFocus(bool forward)
    {
        var order = new List<View>() { _navigation };

        if (_active is not null) CollectFocusable(_active, order);

        if (order.Count < 2) return false;

        var current = order.FindIndex(v => v.HasFocus);
        var next    = current < 0
            ? 0
            : (current + (forward ? 1 : order.Count - 1)) % order.Count;

        order[next].SetFocus();

        return true;
    }

    /// <summary>The controls of <paramref name="parent"/> in layout order, containers replaced by what they hold.</summary>
    private static void CollectFocusable(View parent, List<View> into)
    {
        foreach (var subView in parent.SubViews)
        {
            if (!subView.CanFocus || !subView.Visible || !subView.Enabled) continue;

            var before = into.Count;

            CollectFocusable(subView, into);

            if (into.Count == before) into.Add(subView);
        }
    }

    private void AddPane(ToolPane pane)
    {
        //Siblings of the view list rather than children of one more container: each level of nesting is
        //another place Tab has to decide whether to wrap or move out.
        pane.X       = NAVIGATION_WIDTH;
        pane.Y       = 1;
        pane.Width   = Dim.Fill();
        pane.Height  = Dim.Fill(1);
        pane.Visible = false;

        _panes.Add(pane);
        _paneNames.Add(pane.PaneTitle);

        Add(pane);
    }

    private void ShowSelectedPane()
    {
        var index = _navigation.SelectedItem ?? 0;

        if (index < 0 || index >= _panes.Count) return;

        var pane = _panes[index];

        if (ReferenceEquals(pane, _active)) return;

        if (_active is not null) _active.Visible = false;

        _active         = pane;
        _active.Visible = true;

        _active.Shown();

        SetNeedsDraw();
    }

    private void UpdateHeader()
    {
        _header.Text = $"Target: {_context.TargetText}   Output: {_context.OutputDirectory}";
    }

    private void ConfirmQuit()
    {
        if (_panes.Any(p => p.IsBusy))
        {
            var choice = MessageBox.Query(
                _context.App,
                "Quit",
                "A capture is still running. Quitting cancels it and discards what it has collected so far.",
                "Cancel", "Quit");

            if (choice != 1) return;

            foreach (var pane in _panes) pane.Cancel();
        }

        _context.App.RequestStop();
    }
}
