using System.Collections.ObjectModel;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Memory.Introspect.Tool.Widgets;

/// <summary>A scrollable list of text lines that stays on the newest one until the reader moves away.</summary>
internal sealed class LogView : ListView
{
    private const int MAX_LINES = 5000;

    private readonly ObservableCollection<string> _lines = new();

    public LogView()
    {
        X      = 0;
        Y      = 0;
        Width  = Dim.Fill();
        Height = Dim.Fill();

        SetSource(_lines);
    }

    public int Count => _lines.Count;

    public void Add(string line)
    {
        foreach (var part in line.Split('\n'))
        {
            _lines.Add(part.TrimEnd('\r'));
        }

        while (_lines.Count > MAX_LINES)
        {
            _lines.RemoveAt(0);
        }

        SelectedItem = _lines.Count - 1;
    }

    public void Replace(IEnumerable<string> lines)
    {
        _lines.Clear();

        foreach (var line in lines)
        {
            foreach (var part in line.Split('\n'))
            {
                _lines.Add(part.TrimEnd('\r'));
            }
        }

        if (_lines.Count > 0) SelectedItem = 0;

        SetNeedsDraw();
    }

    public void Clear() => Replace(Array.Empty<string>());
}
