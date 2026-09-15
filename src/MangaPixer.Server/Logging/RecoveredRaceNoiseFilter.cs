using Serilog.Core;
using Serilog.Events;

namespace com.lifepixer.mangapixer.Server.Logging;

/// <summary>
/// Drops the EF Core error log lines for the recovered <c>reading_progress</c>
/// unique-constraint race (SQLite error 19).
/// </summary>
/// <remarks>
/// EF Core logs the failed INSERT at <see cref="LogEventLevel.Error"/> BEFORE
/// <c>ReadingStateService.UpdateProgressAsync</c> catches and recovers it, so a
/// recovered race still surfaced as an error in production (~10/24h). EF emits
/// TWO error events per race:
/// <list type="bullet">
/// <item><c>CommandError</c> (Id 20102) — exception is the raw
/// <see cref="Microsoft.Data.Sqlite.SqliteException"/>.</item>
/// <item><c>SaveChangesFailed</c> (Id 10000) — exception is a
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> whose inner
/// exception is the <see cref="Microsoft.Data.Sqlite.SqliteException"/>.</item>
/// </list>
/// This filter excludes BOTH, matching only when ALL of the following hold:
/// <list type="bullet">
/// <item>Level is Error.</item>
/// <item>The exception (or its inner exception) is a SQLite constraint violation
/// (error code 19) whose message names <c>reading_progress</c>.</item>
/// <item>The SourceContext is under <c>Microsoft.EntityFrameworkCore</c>.</item>
/// </list>
/// A genuine uncaught failure still surfaces via the unhandled-exception
/// middleware (<c>LogEvents.Http.UnhandledRequestError</c>), which has a
/// different SourceContext, so real 500s are not masked. The recovery itself is
/// observable at <see cref="LogEventLevel.Debug"/> via
/// <c>ReadingStateService</c> when the Reading debug category is enabled.
/// </remarks>
internal sealed class RecoveredRaceNoiseFilter : ILogEventFilter
{
    private const string SourceContextProperty = Constants.SourceContextPropertyName;
    private const string EfPrefix = "Microsoft.EntityFrameworkCore";

    public bool IsEnabled(LogEvent logEvent)
    {
        if (logEvent.Level != LogEventLevel.Error || logEvent.Exception is null)
            return true;

        // The SQLite-19 exception may be the direct exception (CommandError event)
        // or the inner exception of a DbUpdateException (SaveChangesFailed event).
        var sqlEx = logEvent.Exception as Microsoft.Data.Sqlite.SqliteException
                    ?? logEvent.Exception.InnerException as Microsoft.Data.Sqlite.SqliteException;
        if (sqlEx is not { SqliteErrorCode: 19 })
            return true;

        if ((sqlEx.Message?.Contains("reading_progress") ?? false) is false)
            return true;

        if (!logEvent.Properties.TryGetValue(SourceContextProperty, out var sc))
            return true;

        if (sc is not ScalarValue { Value: string source } || !source.StartsWith(EfPrefix, StringComparison.Ordinal))
            return true;

        // This is a recovered-race EF error line — drop it.
        return false;
    }
}
