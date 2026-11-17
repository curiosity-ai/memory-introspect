using System.Diagnostics;
using Terminal.Gui.App;

namespace Memory.Introspect.Tool;

/// <summary>What every pane shares: the running application, the process being inspected and where captures are written.</summary>
internal sealed class ToolContext
{
    public ToolContext(IApplication app, string outputDirectory, int? processId)
    {
        App             = app;
        OutputDirectory = outputDirectory;

        if (processId is int pid) SelectProcess(pid);
    }

    public IApplication App { get; }

    /// <summary>Where captures are written. Editable on the Target pane.</summary>
    public string OutputDirectory { get; set; }

    public int?    ProcessId   { get; private set; }
    public string? ProcessName { get; private set; }

    public event Action? TargetChanged;

    public string TargetText => ProcessId is int pid
        ? $"PID {pid} ({ProcessName})"
        : "no process selected";

    public void SelectProcess(int pid)
    {
        ProcessId   = pid;
        ProcessName = NameOf(pid);

        TargetChanged?.Invoke();
    }

    public static string NameOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);

            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "exited";
        }
    }

    public MemoryIntrospector CreateIntrospector(Action<string> log, int circularBufferMB = 1024, int maxNodes = 10_000_000, bool expectLargeGraph = false)
    {
        return MemoryIntrospector.Create(new MemoryIntrospectorOptions()
        {
            Logger                 = new ToolLogger(log),
            Verbose                = true,
            CircularBufferSizeInMB = circularBufferMB,
            MaxNodeCount           = maxNodes,
            ExpectLargeGraph       = expectLargeGraph,
            Timeout                = TimeSpan.FromMinutes(5)
        });
    }

    /// <summary>A path for a new capture in the output folder, which is created when missing.</summary>
    public string NewOutputPath(string kind, string extension)
    {
        Directory.CreateDirectory(OutputDirectory);

        var target = $"process-{ProcessId}";

        return Path.GetFullPath(Path.Combine(OutputDirectory, $"{Widgets.ToolFormat.Stamp()}-{kind}-{target}{extension}"));
    }
}
