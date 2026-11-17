using Terminal.Gui.ViewBase;

namespace Memory.Introspect.Tool.Panes;

internal abstract class ToolPane : View
{
    protected ToolPane(ToolContext context, string title)
    {
        Context   = context;
        PaneTitle = title;
        X         = 0;
        Y         = 0;
        Width     = Dim.Fill();
        Height    = Dim.Fill();

        //A view whose SuperView cannot be focused cannot be focused either, so every container
        //between the window and a control has to say yes for Tab to reach it.
        CanFocus = true;
        TabStop  = TabBehavior.TabGroup;
    }

    protected ToolContext Context { get; }

    /// <summary>Name shown in the navigation list.</summary>
    public string PaneTitle { get; }

    /// <summary>Called when the pane becomes the visible one.</summary>
    public virtual void Shown()
    {
    }

    /// <summary>Called about once a second while the pane is visible.</summary>
    public virtual void Tick()
    {
    }

    /// <summary>What the status bar's "Run" shortcut does on this pane.</summary>
    public virtual bool CanRun => false;

    public virtual void Run()
    {
    }

    /// <summary>What the status bar's "Cancel" shortcut does on this pane.</summary>
    public virtual void Cancel()
    {
    }

    /// <summary>True while something this pane started is still running, so closing the tool asks first.</summary>
    public virtual bool IsBusy => false;
}
