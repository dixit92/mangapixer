using Microsoft.EntityFrameworkCore.Diagnostics;
using Serilog.Core;
using Serilog.Events;

namespace com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Drops EF Core's own error log lines for a unique-constraint race that the calling service recovers from
/// (SQLite error 19), e.g. two first writes of the same <c>reading_progress</c> row.
/// </summary>
/// <remarks>
/// EF Core logs the failed INSERT at <see cref="LogEventLevel.Error"/> BEFORE the service's <c>catch</c> recovers
/// it. EF emits TWO error events per race, and they do NOT look alike:
/// <list type="bullet">
/// <item><c>CommandError</c> (Id 20102, source <c>...Database.Command</c>) - carries NO exception on the log
/// event (the exception lives only in EF's event data, so the rendered line is the failed SQL), which is why the
/// filter this class started as - it looked for a SQLite exception on every event - never saw it and one such
/// line per recovered race reached the log.</item>
/// <item><c>SaveChangesFailed</c> (Id 10000, source <c>...Update</c>) - exception is a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> whose inner exception is the
/// <see cref="Microsoft.Data.Sqlite.SqliteException"/>.</item>
/// </list>
/// Both are dropped, and only when ALL of the following hold:
/// <list type="bullet">
/// <item>Level is Error and the SourceContext is under <c>Microsoft.EntityFrameworkCore</c>.</item>
/// <item>The save runs inside an <see cref="ExpectedRaceScope"/>, i.e. the caller opened one because it recovers
/// from exactly this failure (the scope, not the failing table's name, identifies a recovered race: a completion
/// saves two tables at once and SQLite names whichever it hits first).</item>
/// <item>The event is <c>CommandError</c>, or carries a SQLite constraint violation (error code 19) as its
/// exception or inner exception.</item>
/// </list>
/// A failure outside a scope still logs, and a genuine uncaught failure also surfaces via the unhandled-exception
/// middleware (<c>LogEvents.Http.UnhandledRequestError</c>, a different SourceContext). Callers must therefore
/// recover only from constraint violations inside a scope and let anything else propagate. The recovery itself is
/// observable at <see cref="LogEventLevel.Debug"/> in the service when its debug category is enabled.
/// </remarks>
internal sealed class RecoveredRaceNoiseFilter : ILogEventFilter
{
    private const string SourceContextProperty = Constants.SourceContextPropertyName;
    private const string EventIdProperty = "EventId";
    private const string EfPrefix = "Microsoft.EntityFrameworkCore";

    public bool IsEnabled(LogEvent logEvent)
    {
        if (logEvent.Level != LogEventLevel.Error || !ExpectedRaceScope.IsActive)
            return true;

        if (!logEvent.Properties.TryGetValue(SourceContextProperty, out var sc)
            || sc is not ScalarValue { Value: string source }
            || !source.StartsWith(EfPrefix, StringComparison.Ordinal))
            return true;

        // CommandError has no exception on the event; the scope already says this save's failure is expected.
        if (logEvent.Exception is null)
            return !IsCommandError(logEvent);

        // SaveChangesFailed: the SQLite-19 exception is the inner exception of a DbUpdateException (or the
        // exception itself for other EF events).
        var sqlEx = logEvent.Exception as Microsoft.Data.Sqlite.SqliteException
                    ?? logEvent.Exception.InnerException as Microsoft.Data.Sqlite.SqliteException;
        return sqlEx is not { SqliteErrorCode: 19 };
    }

    /// <summary>
    /// True for EF's <c>CommandError</c> event. The Serilog bridge renders the MEL event id as a structure with an
    /// <c>Id</c> (and a <c>Name</c>) property.
    /// </summary>
    private static bool IsCommandError(LogEvent logEvent)
    {
        if (!logEvent.Properties.TryGetValue(EventIdProperty, out var value))
            return false;
        if (value is StructureValue structure)
        {
            foreach (var property in structure.Properties)
            {
                if (property.Name == "Id" && property.Value is ScalarValue { Value: int id })
                    return id == RelationalEventId.CommandError.Id;
            }
        }

        return value is ScalarValue { Value: int scalarId } && scalarId == RelationalEventId.CommandError.Id;
    }
}
