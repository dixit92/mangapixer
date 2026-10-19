namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Server.Features.Jobs;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>What one tick of the new-chapter checks did (null = that step was not due or waited for the gate).</summary>
public sealed record NewChapterTickResult(LatestUploadSweepResult? Sweep, ReleasesFeedResult? Feed, int BackfillLists, RefreshPassResult? DuePass);

/// <summary>
/// One tick of the new-chapter checks (1.45.0, lane R), run by <see cref="Hosting.MetadataAutoMatchHostedService"/> on its minute tick
/// after the daily refresh check: the latest-upload sweep and the releases feed when due, the readable backfill, and a refresh pass of the
/// due records every <see cref="NewChapterChecks.DuePassInterval"/> since the last pass (daily or not) - or at once when the feed made
/// records due. Each step is on its own: a failure is logged by type and the next step goes on.
/// </summary>
public sealed class NewChapterChecksRunner(
    MangaPixerDbContext db, LatestUploadSweep sweep, ReleasesFeedPoll feed, ReadableBackfill backfill, MetadataRefreshService refresh,
    TimeProvider time, ILogger<NewChapterChecksRunner> logger)
{
    public async Task<NewChapterTickResult> RunTickAsync(CancellationToken ct = default)
    {
        var sweepResult = await StepAsync("sweep", async () => await sweep.IsDueAsync(ct) ? await sweep.RunAsync(ct) : null);
        var feedResult = await StepAsync("feed", async () => await feed.IsDueAsync(ct) ? await feed.RunAsync(ct) : null);
        var lists = await StepAsync("backfill", async () => (int?)await backfill.RunIfDueAsync(ct)) ?? 0;
        var pass = await StepAsync("due-pass", async () => await DuePassDueAsync(feedResult is { Matched: > 0 }, ct) ? await RunDuePassAsync(ct) : null);
        return new NewChapterTickResult(sweepResult, feedResult, lists, pass);
    }

    /// <summary>True when the last refresh pass (the daily job or one of these) started at least <see cref="NewChapterChecks.DuePassInterval"/> ago.</summary>
    private async Task<bool> DuePassDueAsync(bool nudged, CancellationToken ct)
    {
        if (nudged)
            return true;
        var now = time.GetUtcNow();
        var daily = await db.JobRuns.AsNoTracking().Where(j => j.Key == ScheduledJobKeys.MetadataRefresh).Select(j => j.LastStartedAt).FirstOrDefaultAsync(ct);
        var between = (await NewChapterChecks.PeekAsync(db, ct))?.DuePassAt;
        var last = new[] { daily, between }.Max();
        // Before any pass ran (a fresh install, the upgrade) the daily job goes first.
        return last is { } at && (now - at >= NewChapterChecks.DuePassInterval || at > now);
    }

    private async Task<RefreshPassResult?> RunDuePassAsync(CancellationToken ct)
    {
        var state = await NewChapterChecks.StateAsync(db, ct);
        state.DuePassAt = time.GetUtcNow();
        await db.SaveChangesAsync(ct);
        var result = await refresh.RunPassAsync(ct);
        if (result.Refreshed > 0 || result.StoppedCode is not null)
            logger.LogInformation(LogEvents.Metadata.RefreshDuePass, "Refresh of due records: {Refreshed} of {Due} refreshed; {Outcome}",
                result.Refreshed, result.Due, result.StoppedCode ?? "ok");
        return result;
    }

    private async Task<T?> StepAsync<T>(string step, Func<Task<T?>> run)
    {
        try
        {
            return await run();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(LogEvents.Metadata.FreshnessFailed, "New-chapter check {Step} failed: {Error}", step, ex.GetType().Name);
            db.ChangeTracker.Clear();
            return default;
        }
    }
}
