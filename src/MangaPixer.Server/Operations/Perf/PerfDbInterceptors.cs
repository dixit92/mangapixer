namespace com.lifepixer.mangapixer.Server.Operations.Perf;

using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Feeds <see cref="PerfRecorder"/> (1.46.0 lane M, measurement only): the duration of every SQL command EF runs (for a
/// query: from execution to the reader's dispose, so reading the rows counts) and of every transaction BEGIN / COMMIT
/// (SQLite's <c>BEGIN IMMEDIATE</c> waits for the write lock). Attached (one pair per host)
/// only when the host switched <c>MangaPixer:Perf:RequestTiming</c> on.
/// </summary>
public sealed class PerfCommandInterceptor : DbCommandInterceptor
{
    private readonly PerfRecorder _recorder;

    internal PerfCommandInterceptor(PerfRecorder recorder) => _recorder = recorder;

    // A query is recorded when it executes (time to the first row) and the time spent reading its rows is added when its
    // reader is disposed (DataReaderDisposingEventData.Duration runs from the command's start).
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<DbCommand, object> s_executed = new();

    public override DbDataReader ReaderExecuted(DbCommand command, CommandExecutedEventData eventData, DbDataReader result)
    {
        OnReaderExecuted(command, eventData);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        OnReaderExecuted(command, eventData);
        return ValueTask.FromResult(result);
    }

    private void OnReaderExecuted(DbCommand command, CommandExecutedEventData eventData)
    {
        var ms = eventData.Duration.TotalMilliseconds;
        _recorder.RecordCommand(command.CommandText, ms, 0);
        s_executed.AddOrUpdate(command, ms);
    }

    public override InterceptionResult DataReaderDisposing(DbCommand command, DataReaderDisposingEventData eventData, InterceptionResult result)
    {
        if (s_executed.TryGetValue(command, out var executed))
        {
            s_executed.Remove(command);
            var reading = Math.Max(0, eventData.Duration.TotalMilliseconds - (double)executed);
            _recorder.RecordCommand(command.CommandText, reading, eventData.ReadCount, countAsCommand: false);
        }
        return result;
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        _recorder.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        _recorder.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return ValueTask.FromResult(result);
    }

    public override object? ScalarExecuted(DbCommand command, CommandExecutedEventData eventData, object? result)
    {
        _recorder.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return result;
    }

    public override ValueTask<object?> ScalarExecutedAsync(DbCommand command, CommandExecutedEventData eventData, object? result, CancellationToken cancellationToken = default)
    {
        _recorder.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return ValueTask.FromResult(result);
    }

    public override void CommandFailed(DbCommand command, CommandErrorEventData eventData)
        => _recorder.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);

    public override Task CommandFailedAsync(DbCommand command, CommandErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _recorder.RecordCommand(command.CommandText, eventData.Duration.TotalMilliseconds, 0);
        return Task.CompletedTask;
    }
}

/// <summary>Transaction BEGIN / COMMIT durations for <see cref="PerfRecorder"/> (measurement only).</summary>
public sealed class PerfTransactionInterceptor : DbTransactionInterceptor
{
    private readonly PerfRecorder _recorder;

    internal PerfTransactionInterceptor(PerfRecorder recorder) => _recorder = recorder;

    public override DbTransaction TransactionStarted(DbConnection connection, TransactionEndEventData eventData, DbTransaction result)
    {
        _recorder.RecordTransaction("begin", eventData.Duration.TotalMilliseconds);
        return result;
    }

    public override ValueTask<DbTransaction> TransactionStartedAsync(DbConnection connection, TransactionEndEventData eventData, DbTransaction result, CancellationToken cancellationToken = default)
    {
        _recorder.RecordTransaction("begin", eventData.Duration.TotalMilliseconds);
        return ValueTask.FromResult(result);
    }

    public override void TransactionCommitted(DbTransaction transaction, TransactionEndEventData eventData)
        => _recorder.RecordTransaction("commit", eventData.Duration.TotalMilliseconds);

    public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        _recorder.RecordTransaction("commit", eventData.Duration.TotalMilliseconds);
        return Task.CompletedTask;
    }
}
