namespace com.lifepixer.mangapixer.Tests.Server.Logging;

using com.lifepixer.mangapixer.Server.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Serilog;
using Serilog.Core;
using Serilog.Events;
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

    [Fact]
    public void CommandError_InsideTheScope_IsDropped_WhicheverTableTheMessageNames()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin())
        {
            logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error(Unique("reading_progress"), "Failed executing DbCommand");
            logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error(Unique("read_marks"), "Failed executing DbCommand");
            logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error(Unique("favorites"), "Failed executing DbCommand");
        }

        Assert.Empty(seen);
    }

    [Fact]
    public void SaveChangesFailed_InsideTheScope_IsDropped()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin())
        {
            var failure = new DbUpdateException("An error occurred while saving the entity changes.", Unique("read_marks"));
            logger.ForContext(Constants.SourceContextPropertyName, EfUpdate).Error(failure, "An exception occurred in the database while saving changes");
        }

        Assert.Empty(seen);
    }

    [Fact]
    public void TheSameFailure_OutsideAScope_StillLogs()
    {
        var (logger, seen) = CreateLogger();
        logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error(Unique(), "Failed executing DbCommand");

        Assert.Single(seen);
    }

    [Fact]
    public void AfterTheScopeCloses_TheFailureLogsAgain()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin()) { }
        logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error(Unique(), "Failed executing DbCommand");

        Assert.Single(seen);
    }

    [Fact]
    public void InsideTheScope_OnlyEfConstraintErrors_AreDropped()
    {
        var (logger, seen) = CreateLogger();
        using (ExpectedRaceScope.Begin())
        {
            // Not under EF: a service's own error line.
            logger.ForContext(Constants.SourceContextPropertyName, "com.lifepixer.mangapixer.Server.Features.Reading.ReadingStateService")
                .Error(Unique(), "own error");
            // Not a constraint violation (SQLITE_BUSY = 5).
            logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error(new SqliteException("database is locked", 5), "busy");
            // Not an exception at all.
            logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Error("plain error");
            // Not an error: warnings are never dropped.
            logger.ForContext(Constants.SourceContextPropertyName, EfCommand).Warning(Unique(), "warning");
            // No SourceContext.
            logger.Error(Unique(), "no context");
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
