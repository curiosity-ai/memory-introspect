using System.Reflection;
using Memory.Introspect.Tool;
using Terminal.Gui.App;

const string USAGE = """
    memory-introspect - a terminal UI for .NET memory and CPU diagnostics

    Usage:
      memory-introspect [--pid <id>] [--output <folder>]

    Options:
      -p, --pid <id>         Start with this process selected as the target.
      -o, --output <folder>  Where captures are written. Default: the current folder.
      -v, --version          Print the version.
      -h, --help             Print this help.

    Views: Target, Memory Dump (.gcdump / .dmp), CPU Profile, Trace (.nettrace),
    Allocations, GC Pauses and Analyze (reports on an existing .nettrace).
    Keys: Tab moves between controls, Ctrl+R runs, F4 cancels, Ctrl+Q quits.
    """;

int?   pid    = null;
string output = Directory.GetCurrentDirectory();

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "-h": case "--help": case "-?":
            Console.WriteLine(USAGE);
            return 0;

        case "-v": case "--version":
            Console.WriteLine(Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "unknown");
            return 0;

        case "-p": case "--pid":
            if (i + 1 >= args.Length || !int.TryParse(args[++i], out var parsed) || parsed <= 0)
            {
                Console.Error.WriteLine("--pid needs a process id.");
                return 2;
            }

            pid = parsed;
            break;

        case "-o": case "--output":
            if (i + 1 >= args.Length)
            {
                Console.Error.WriteLine("--output needs a folder.");
                return 2;
            }

            output = args[++i];
            break;

        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n\n{USAGE}");
            return 2;
    }
}

if (Console.IsInputRedirected || Console.IsOutputRedirected)
{
    Console.Error.WriteLine("memory-introspect is interactive and needs a terminal; its input and output cannot be redirected.");
    return 1;
}

//Terminal.Gui's default driver asks the terminal how big it is with an escape sequence, and a terminal that
//does not answer (some IDE consoles) is left with a screen of 0x0 that draws nothing. This driver reads the
//console size directly. The cost is the mouse: the interface is built to be driven by the keyboard.
const string DRIVER = "dotnet";

using var app = Application.Create().Init(DRIVER);

var context = new ToolContext(app, Path.GetFullPath(output), pid);

using var window = new ToolWindow(context);

var timeout = app.AddTimeout(TimeSpan.FromSeconds(1), () =>
{
    window.Tick();
    return true;
});

app.Run(window);
if (timeout is not null) app.RemoveTimeout(timeout);

return 0;
