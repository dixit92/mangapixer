namespace com.lifepixer.mangaplex.Tests.Server.Hosting;

using Microsoft.Extensions.Logging;
using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Serilog ILogEventSink that collects all log events for later assertion.
/// Used by hosting-correctness tests to capture startup and maintenance events that
/// flow through the Serilog pipeline (UseSerilog replaces the standard MEL
/// logging factory, so ILoggerProvider-based collectors are bypassed).
/// </summary>
public sealed class CollectingSink : ILogEventSink
{
    private readonly List<LogEvent> _events = new();
    private readonly object _lock = new();

    public void Emit(LogEvent logEvent)
    {
        lock (_lock)
        {
            _events.Add(logEvent);
        }
    }

    public IReadOnlyList<LogEvent> Events
    {
        get
        {
            lock (_lock)
                return _events.ToList();
        }
    }

    /// <summary>
    /// Clears all collected events.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
            _events.Clear();
    }

    /// <summary>
    /// Returns true if any event message contains the given substring
    /// (case-insensitive).
    /// </summary>
    public bool ContainsMessage(string substring, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        lock (_lock)
        {
            return _events.Any(e =>
                e.MessageTemplate.Text.Contains(substring, comparison));
        }
    }

    /// <summary>
    /// Returns true if any event at the given level or higher contains
    /// the given substring in its message.
    /// </summary>
    public bool ContainsMessageAtLevel(LogEventLevel minimumLevel, string substring,
        StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        lock (_lock)
        {
            return _events.Any(e =>
                e.Level >= minimumLevel &&
                e.MessageTemplate.Text.Contains(substring, comparison));
        }
    }
}

/// <summary>
/// Captured log event (MEL-level, for tests that use ILogger directly).
/// </summary>
public sealed record CapturedLog(LogLevel Level, string Message, Exception? Exception);
