using Microsoft.Extensions.Logging;

namespace Memory.Introspect.Tool;

/// <summary>Hands what the library logs to a pane, which decides how to put it on screen.</summary>
internal sealed class ToolLogger : ILogger
{
    private readonly Action<string> _sink;

    public ToolLogger(Action<string> sink)
    {
        _sink = sink;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);

        if (exception is not null) message += " " + exception.Message;

        _sink(message);
    }
}
