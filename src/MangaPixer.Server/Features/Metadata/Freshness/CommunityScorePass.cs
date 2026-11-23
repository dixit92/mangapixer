namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What one community-score pass did (counts only); <see cref="StoppedCode"/> null = it completed.</summary>
public sealed record CommunityScorePassResult(int Requests, int Records, int Scored, string? StoppedCode);

/// <summary>
/// The MangaDex community scores (1.54.0, owner: "MangaUpdates + MAL and MangaDex if linked only", consent main 8 / automatic 6): for the
/// MangaDex records of linked series - the same set the latest-upload sweep reads (<see cref="LatestUploadSweep.TargetsAsync"/>: a found
/// companion, or a series linked to MangaDex) - <c>GET /statistics/manga?manga[]=..</c>, 100 ids per request, through the gateway like every
/// MangaDex call (the automatic gate and consent, the allowlist, each library's "Fetch from the web", the daily budget, one request a
/// second, the backoff). Each record's Bayesian rating and number of ratings is stored on the record; a record is read again after
/// <see cref="MaxAge"/>. Sends MangaDex record ids only; logs ids and counts. MangaUpdates' score needs no request (it comes with the series
/// answer); the MyAnimeList score is read separately (with the app an admin registered).
/// </summary>
public sealed class CommunityScorePass(
    MangaPixerDbContext db, MetadataGateway gateway, IMangaDexProvider mangaDex, MetadataAutoMatchService autoMatch, LatestUploadSweep sweep,
    TimeProvider time, ILogger<CommunityScorePass> logger)
{
    /// <summary>The job row that remembers the last pass.</summary>
    internal const string JobKey = "metadata-community-scores";

    /// <summary>A pass at most this often (most passes then find nothing stale and send nothing).</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    /// <summary>A stored score is read again after this long (the ongoing-series refresh cadence).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);

    public async Task<bool> IsDueAsync(CancellationToken ct = default)
    {
        var at = await db.JobRuns.AsNoTracking().Where(j => j.Key == JobKey).Select(j => j.LastStartedAt).FirstOrDefaultAsync(ct);
        var now = time.GetUtcNow();
        return at is not { } last || now - last >= Interval || last > now;
    }

    /// <summary>One pass; null when the automatic gate for MangaDex is closed (nothing sent, nothing recorded).</summary>
    public async Task<CommunityScorePassResult?> RunAsync(CancellationToken ct = default)
    {
        if (await autoMatch.CheckGlobalGateAsync(MetadataProviderAllowlist.MangaDex, ct) is not null)
            return null;
        var started = time.GetUtcNow();
        var run = await db.JobRuns.FirstOrDefaultAsync(j => j.Key == JobKey, ct);
        if (run is null)
        {
            run = new JobRunEntity { Key = JobKey };
            db.JobRuns.Add(run);
        }
        run.LastStartedAt = started;
        await db.SaveChangesAsync(ct);

        var targets = await sweep.TargetsAsync(ct);
        var staleBefore = started - MaxAge;
        var stale = new HashSet<long>();
        foreach (var chunk in targets.Select(t => t.RecordId).Chunk(500))
        {
            stale.UnionWith(await db.MetadataRecords.AsNoTracking()
                .Where(r => chunk.Contains(r.Id) && (r.CommunityScoreAt == null || r.CommunityScoreAt <= staleBefore))
                .Select(r => r.Id).ToListAsync(ct));
        }
        var call = MetadataCallContext.Automatic();
        int requests = 0, records = 0, scored = 0;
        string? stopped = null;
        foreach (var group in targets.Where(t => stale.Contains(t.RecordId)).GroupBy(t => t.Libraries[0]).OrderBy(g => g.Key))
        {
            foreach (var batch in group.Chunk(MangaDexProvider.StatisticsBatch))
            {
                IReadOnlyList<MangaDexStatistics> answer;
                try
                {
                    var ids = batch.Select(t => t.MangaId).ToList();
                    answer = await gateway.CompanionCallAsync(MetadataProviderAllowlist.MangaDex, "statistics", group.Key,
                        c => mangaDex.StatisticsAsync(ids, c), call, ct);
                }
                catch (MetadataGatewayException ex)
                {
                    stopped = ex.Code;
                    break;
                }
                requests++;
                var now = time.GetUtcNow();
                var byId = answer.ToDictionary(a => a.MangaId, StringComparer.Ordinal);
                var batchIds = batch.Select(t => t.RecordId).ToList();
                var rows = await db.MetadataRecords.Where(r => batchIds.Contains(r.Id)).ToDictionaryAsync(r => r.Id, ct);
                foreach (var target in batch)
                {
                    if (!rows.TryGetValue(target.RecordId, out var row))
                        continue;
                    records++;
                    // Not returned or not rated: no score now (checked, so not asked again before MaxAge).
                    var stats = byId.GetValueOrDefault(target.MangaId);
                    (row.CommunityScore, row.CommunityVotes, row.CommunityScoreAt) = (stats?.Bayesian, stats?.Votes, now);
                    if (stats?.Bayesian is not null)
                        scored++;
                }
                await db.SaveChangesAsync(ct);
            }
            if (stopped is not null)
                break;
        }

        run.LastFinishedAt = time.GetUtcNow();
        run.LastOutcome = stopped ?? "ok";
        run.LastDetail = $"{records} records, {scored} scored";
        await db.SaveChangesAsync(ct);
        if (requests > 0 || stopped is not null)
        {
            logger.LogInformation(LogEvents.Metadata.CommunityScorePass,
                "MangaDex community scores: {Requests} requests, {Records} records read, {Scored} with a score; {Outcome}",
                requests, records, scored, stopped ?? "ok");
        }
        return new CommunityScorePassResult(requests, records, scored, stopped);
    }
}
