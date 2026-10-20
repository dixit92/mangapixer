namespace com.lifepixer.mangapixer.Server.Operations.Perf;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Feeds <see cref="PerfRecorder"/> (1.46.0 lane M, measurement only): the duration of every SQL command EF runs (for a
/// query: from execution to the reader's dispose, so reading the rows counts) and of every transaction BEGIN / COMMIT
/// (SQLite's <c>BEGIN IMMEDIATE</c> waits for the write lock). A no-op unless a host switched
/// <c>MangaPixer:Perf:RequestTiming</c> on.
/// </summary>
public sealed class PerfCommandInterceptor : DbCommandInterceptor
{
    public static readonly PerfCommandInterceptor Instance = new();

    private PerfCommandInterceptor()
    {
    }

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, eventData.ReadCount);
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        PerfRecorder.Active?.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return Task.CompletedTask;
    }
}

/// <summary>Transaction BEGIN / COMMIT durations for <see cref="PerfRecorder"/> (measurement only).</summary>
public sealed class PerfTransactionInterceptor : DbTransactionInterceptor
{
    public static readonly PerfTransactionInterceptor Instance = new();

    private PerfTransactionInterceptor()
    {
    }

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        PerfRecorder.Active?.RecordTransaction("begin", eventData.Duration.TotalMilliseconds);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        PerfRecorder.Active?.RecordTransaction("begin", eventData.Duration.TotalMilliseconds);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => PerfRecorder.Active?.RecordTransaction("commit", eventData.Duration.TotalMilliseconds);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        PerfRecorder.Active?.RecordTransaction("commit", eventData.Duration.TotalMilliseconds);
        return Task.CompletedTask;
    }
}
