using System.Globalization;

namespace Memory.Introspect.Tool.Widgets;

internal static class ToolFormat
{
    public static string Bytes(long bytes)
    {
        string[] units = { "B", "KB", "MB", "GB", "TB" };

        double value = bytes;
        var    unit  = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.#} {units[unit]}");
    }

    public static string Duration(TimeSpan span)
    {
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}h {span.Minutes:00}m {span.Seconds:00}s";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}m {span.Seconds:00}s";

        return string.Create(CultureInfo.InvariantCulture, $"{span.TotalSeconds:0.#}s");
    }

    /// <summary>Cuts <paramref name="text"/> to <paramref name="width"/> columns, ending in an ellipsis when it was longer.</summary>
    public static string Fit(string text, int width)
    {
        if (width <= 0) return "";
        if (text.Length <= width) return text;
        if (width == 1) return "…";

        return text.Substring(0, width - 1) + "…";
    }

    public static string Percent(double value) => string.Create(CultureInfo.InvariantCulture, $"{value:0.0}%");

    /// <summary>A file name stamp that sorts: 2026-10-24-00-14-33.</summary>
    public static string Stamp() => DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss", CultureInfo.InvariantCulture);
}
