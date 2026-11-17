using System.Collections.ObjectModel;
using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Widgets;

/// <summary>Builders for the framed boxes and form rows every pane is made of, so they agree on border and focus.</summary>
internal static class ToolViews
{
    public const int LABEL_WIDTH = 24;

    /// <summary>A frame around something the user can type in, scroll or press.</summary>
    public static FrameView FocusableFrame(string title)
    {
        var frame = Frame(title);

        frame.CanFocus = true;

        return frame;
    }

    /// <summary>A frame around something the user only reads - skipped by Tab.</summary>
    public static FrameView Frame(string title)
    {
        return new FrameView()
        {
            Title       = title,
            BorderStyle = LineStyle.Rounded,
            CanFocus    = false
        };
    }

    public static Button Button(string text, Action onPress)
    {
        var button = new Button() { Text = text, ShadowStyle = ShadowStyles.None };

        button.Accepting += (_, e) =>
        {
            e.Handled = true;
            onPress();
        };

        return button;
    }

    /// <summary>A label in the left column and the control it describes beside it.</summary>
    public static void Row(View parent, int y, string label, View control, int height = 1)
    {
        parent.Add(new Label() { X = 0, Y = y, Width = LABEL_WIDTH, Height = 1, Text = label });

        control.X = LABEL_WIDTH;
        control.Y = y;

        if (control.Height is null) control.Height = height;

        parent.Add(control);
    }

    public static TextField Field(string text, int width = 14)
    {
        return new TextField() { Width = width, Height = 1, Text = text };
    }

    public static CheckBox Check(string text, bool isChecked)
    {
        return new CheckBox() { Text = text, Value = isChecked ? CheckState.Checked : CheckState.UnChecked };
    }

    public static OptionSelector Choice(params string[] labels)
    {
        return new OptionSelector() { Labels = labels, Value = 0, Width = Dim.Fill(), Height = labels.Length };
    }

    /// <summary>A single-line drop-down over a fixed set of choices; the first one is selected.</summary>
    public static DropDownList Dropdown(params string[] items)
    {
        return new DropDownList()
        {
            Width  = 46,
            Height = 1,
            Source = new ListWrapper<string>(new ObservableCollection<string>(items)),
            Value  = items[0]
        };
    }

    public static void SetTable<T>(TableView table, IReadOnlyList<T> rows, Dictionary<string, Func<T, object>> columns)
    {
        table.Table = new EnumerableTableSource<T>(rows, columns);
        table.SetNeedsDraw();
    }
}
