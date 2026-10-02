using Serilog.Core;
using Serilog.Events;

namespace com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Drops EF Core's own error log lines for a unique-constraint race that the calling service recovers from
/// (SQLite error 19), e.g. two first writes of the same <c>reading_progress</c> row.
/// </summary>
/// <remarks>
/// EF Core logs the failed INSERT at <see cref="LogEventLevel.Error"/> BEFORE the service's <c>catch</c> recovers
/// it, so a recovered race still surfaced as an error in production. EF emits TWO error events per race:
/// <list type="bullet">
/// <item><c>CommandError</c> (Id 20102) - exception is the raw
/// <see cref="Microsoft.Data.Sqlite.SqliteException"/>.</item>
/// <item><c>SaveChangesFailed</c> (Id 10000) - exception is a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> whose inner exception is the
/// <see cref="Microsoft.Data.Sqlite.SqliteException"/>.</item>
/// </list>
/// This filter excludes BOTH, matching only when ALL of the following hold:
/// <list type="bullet">
/// <item>Level is Error.</item>
/// <item>The exception (or its inner exception) is a SQLite constraint violation (error code 19).</item>
/// <item>The SourceContext is under <c>Microsoft.EntityFrameworkCore</c>.</item>
/// <item>The save runs inside an <see cref="ExpectedRaceScope"/>, i.e. the caller opened one because it recovers
/// from exactly this failure.</item>
/// </list>
/// A constraint failure outside a scope still logs, and a genuine uncaught failure also surfaces via the
/// unhandled-exception middleware (<c>LogEvents.Http.UnhandledRequestError</c>, a different SourceContext). The
/// recovery itself is observable at <see cref="LogEventLevel.Debug"/> in the service when its debug category is
/// enabled.
/// </remarks>
internal sealed class RecoveredRaceNoiseFilter : ILogEventFilter
{
    private const string SourceContextProperty = Constants.SourceContextPropertyName;
    private const string EfPrefix = "Microsoft.EntityFrameworkCore";

    public bool IsEnabled(LogEvent logEvent)
    {
        if (logEvent.Level != LogEventLevel.Error || logEvent.Exception is null)
            return true;

        if (!ExpectedRaceScope.IsActive)
            return true;

        // The SQLite-19 exception may be the direct exception (CommandError event)
        // or the inner exception of a DbUpdateException (SaveChangesFailed event).
        var sqlEx = logEvent.Exception as Microsoft.Data.Sqlite.SqliteException
                    ?? logEvent.Exception.InnerException as Microsoft.Data.Sqlite.SqliteException;
        if (sqlEx is not { SqliteErrorCode: 19 })
            return true;

        if (!logEvent.Properties.TryGetValue(SourceContextProperty, out var sc))
            return true;

        if (sc is not ScalarValue { Value: string source } || !source.StartsWith(EfPrefix, StringComparison.Ordinal))
            return true;

        // A recovered-race EF error line - drop it.
        return false;
    }
}
