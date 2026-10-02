namespace com.lifepixer.mangapixer.Tests.Server.Logging;

using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;
using Xunit;

/// <summary>
/// The filter that keeps EF Core's own error lines for a RECOVERED unique-constraint race out of the log, checked
/// through a real Serilog logger configured the way the host configures it (filter on the logger configuration).
/// The end-to-end proof against the host's real pipeline is <c>RecoveredRaceLoggingTests</c>.
/// </summary>
public sealed class RecoveredRaceNoiseFilterTests
{
    private const string EfCommand = "Microsoft.EntityFrameworkCore.Database.Command";
    private const string EfUpdate = "Microsoft.EntityFrameworkCore.Update";

    private static (Serilog.ILogger Logger, List<LogEvent> Seen) CreateLogger()
    {
        var seen = new List<LogEvent>();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .Filter.With(new RecoveredRaceNoiseFilter())
            .WriteTo.Sink(new DelegatingSink(seen.Add))
            .CreateLogger();
        return (logger, seen);
    }

    private static SqliteException Unique(string table = "reading_progress")
        => new($"UNIQUE constraint failed: {table}.UserId, {table}.ItemId.", 19);

    /// <summary>
    /// An EF-style event the way the Serilog bridge delivers it: the MEL event id as a structure with Id and Name,
    /// and the SourceContext. EF's <c>CommandError</c> arrives with NO exception (measured against the real host),
    /// <c>SaveChangesFailed</c> with the DbUpdateException.
    /// </summary>
    private static LogEvent Ef(string source, int eventId, Exception? exception, LogEventLevel level = LogEventLevel.Error)
        => new(DateTimeOffset.UtcNow, level, exception, new MessageTemplateParser().Parse("EF event"),
        [
            new LogEventProperty(Constants.SourceContextPropertyName, new ScalarValue(source)),
            new LogEventProperty("EventId", new StructureValue([new LogEventProperty("Id", new ScalarValue(eventId)), new LogEventProperty("Name", new ScalarValue("x"))])),
        ]);

    private const int CommandErrorId = 20102;
    private const int SaveChangesFailedId = 10000;

    [Fact]
    public void CommandError_WithNoException_InsideTheScope_IsDropped()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin())
            logger.Write(Ef(EfCommand, CommandErrorId, exception: null));

        Assert.Empty(seen);
    }

    [Fact]
    public void SaveChangesFailed_InsideTheScope_IsDropped_WhicheverTableTheMessageNames()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin())
        {
            foreach (var table in new[] { "reading_progress", "read_marks", "favorites" })
                logger.Write(Ef(EfUpdate, SaveChangesFailedId, new DbUpdateException("An error occurred while saving the entity changes.", Unique(table))));
        }

        Assert.Empty(seen);
    }

    [Fact]
    public void TheSameFailures_OutsideAScope_StillLog()
    {
        var (logger, seen) = CreateLogger();
        logger.Write(Ef(EfCommand, CommandErrorId, exception: null));
        logger.Write(Ef(EfUpdate, SaveChangesFailedId, new DbUpdateException("save failed", Unique())));

        Assert.Equal(2, seen.Count);
    }

    [Fact]
    public void AfterTheScopeCloses_TheFailureLogsAgain()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin()) { }
        logger.Write(Ef(EfCommand, CommandErrorId, exception: null));

        Assert.Single(seen);
    }

    [Fact]
    public void InsideTheScope_OnlyEfCommandErrorsAndConstraintViolations_AreDropped()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin())
        {
            // Not under EF: a service's own error line.
            logger.Write(Ef("com.lifepixer.mangapixer.Server.Features.Reading.ReadingStateService", CommandErrorId, exception: null));
            // An EF error that is neither CommandError nor a constraint violation.
            logger.Write(Ef(EfCommand, 20100, exception: null));
            // A SQLite failure that is not a constraint violation (SQLITE_BUSY = 5), even on SaveChangesFailed.
            logger.Write(Ef(EfUpdate, SaveChangesFailedId, new DbUpdateException("busy", new SqliteException("database is locked", 5))));
            // Not an error: warnings are never dropped.
            logger.Write(Ef(EfCommand, CommandErrorId, exception: null, level: LogEventLevel.Warning));
            // No SourceContext at all.
            logger.Error("no context");
        }

        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task TheScope_FollowsTheAsyncFlow_AndDoesNotLeakToOthers()
    {
        Assert.False(ExpectedRaceScope.IsActive);
        bool insideAfterAwait;
        using (ExpectedRaceScope.Begin())
        {
            await Task.Delay(10);
            insideAfterAwait = ExpectedRaceScope.IsActive;
            // A sibling flow started inside the scope inherits it; one started outside does not.
            Assert.True(await Task.Run(() => ExpectedRaceScope.IsActive));
        }

        Assert.True(insideAfterAwait);
        Assert.False(ExpectedRaceScope.IsActive);
        Assert.False(await Task.Run(() => ExpectedRaceScope.IsActive));
    }

    [Fact]
    public void Scopes_Nest()
    {
        using (ExpectedRaceScope.Begin())
        {
            using (ExpectedRaceScope.Begin()) { }
            Assert.True(ExpectedRaceScope.IsActive);
        }

        Assert.False(ExpectedRaceScope.IsActive);
    }

    private sealed class DelegatingSink(Action<LogEvent> emit) : ILogEventSink
    {
        public void Emit(LogEvent logEvent) => emit(logEvent);
    }
}
