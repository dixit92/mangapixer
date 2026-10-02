namespace com.lifepixer.mangapixer.Server.Features.Jobs;

using System.Collections.Concurrent;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What one job run ended with: <c>ok</c>, <c>failed</c>, <c>skipped</c> or <c>waiting</c> (the gate stopped it).</summary>
public static class JobOutcomes
{
    public const string Ok = "ok";
    public const string Failed = "failed";
    public const string Skipped = "skipped";
    public const string Waiting = "waiting";
}

/// <summary>
/// Persists the last run of every scheduled job (1.32.0, <c>job_runs</c>): one upsert when a run starts and one when it ends, each
/// in its own scope. The daily jobs read <see cref="LastStartedAsync"/> as their floor, so a restart neither runs them twice nor
/// skips a day; the Scheduled jobs section reads <see cref="GetAllAsync"/>. A failure to record never stops the job (logged, type
/// only). Details carry counts only.
/// </summary>
public sealed class JobRunRecorder
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<JobRunRecorder> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _running = new(StringComparer.Ordinal);

    public JobRunRecorder(IServiceScopeFactory scopes, TimeProvider time, ILogger<JobRunRecorder> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
    }

    /// <summary>True while a run of <paramref name="key"/> is going (this process).</summary>
    public bool IsRunning(string key) => _running.ContainsKey(key);

    /// <summary>Records the start of a run; returns its start time.</summary>
    public async Task<DateTimeOffset> StartedAsync(string key, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        _running[key] = now;
        await WriteAsync(key, row =>
        {
            row.LastStartedAt = now;
            row.LastOutcome = null;
            row.LastDetail = null;
            row.LastFinishedAt = null;
            row.LastDurationMs = null;
        }, ct);
        return now;
    }

    /// <summary>Records the end of the run started at <paramref name="startedAt"/>.</summary>
    public async Task FinishedAsync(string key, DateTimeOffset startedAt, string outcome, string? detail, CancellationToken ct)
    {
        _running.TryRemove(key, out _);
        var now = _time.GetUtcNow();
        await WriteAsync(key, row =>
        {
            row.LastStartedAt ??= startedAt;
            row.LastFinishedAt = now;
            row.LastOutcome = outcome;
            row.LastDetail = detail is { Length: > 128 } ? detail[..128] : detail;
            row.LastDurationMs = Math.Max(0, (long)(now - startedAt).TotalMilliseconds);
        }, ct);
    }

    /// <summary>Runs <paramref name="work"/> between a start and a finish record; an exception is recorded as <c>failed</c> and rethrown.</summary>
    public async Task<T> RunAsync<T>(string key, Func<Task<(T Result, string Outcome, string? Detail)>> work, CancellationToken ct)
    {
        var started = await StartedAsync(key, ct);
        try
        {
            var (result, outcome, detail) = await work();
            await FinishedAsync(key, started, outcome, detail, CancellationToken.None);
            return result;
        }
        catch (Exception ex)
        {
            await FinishedAsync(key, started, ex is OperationCanceledException ? JobOutcomes.Skipped : JobOutcomes.Failed,
                ex is OperationCanceledException ? "stopped" : ex.GetType().Name, CancellationToken.None);
            throw;
        }
    }

    public async Task<DateTimeOffset?> LastStartedAsync(string key, CancellationToken ct) => (await GetAsync(key, ct))?.LastStartedAt;

    public async Task<JobRunEntity?> GetAsync(string key, CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.JobRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Key == key, ct);
    }

    public async Task<IReadOnlyDictionary<string, JobRunEntity>> GetAllAsync(CancellationToken ct)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.JobRuns.AsNoTracking().ToDictionaryAsync(r => r.Key, StringComparer.Ordinal, ct);
    }

    private async Task WriteAsync(string key, Action<JobRunEntity> change, CancellationToken ct)
    {
        try
        {
            using var scope = _scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = await db.JobRuns.FirstOrDefaultAsync(r => r.Key == key, ct);
            if (row is null)
            {
                row = new JobRunEntity { Key = key };
                db.JobRuns.Add(row);
            }
            change(row);
            await db.SaveChangesAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(LogEvents.Administration.JobRunRecordFailed, "Recording a run of job {Job} failed: {Error}", key, ex.GetType().Name);
        }
    }
}
