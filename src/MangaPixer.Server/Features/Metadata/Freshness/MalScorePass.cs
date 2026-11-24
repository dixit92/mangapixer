namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers.Adapters;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>What one MyAnimeList score pass did (counts only); <see cref="StoppedCode"/> null = it completed.</summary>
public sealed record MalScorePassResult(int Requests, int Scored, string? StoppedCode);

/// <summary>
/// The MyAnimeList community scores (1.54.0, owner: "MAL ... if linked only"; consent main 8 / automatic 6): for the MangaDex records of
/// linked series (<see cref="LatestUploadSweep.TargetsAsync"/>) that name a MyAnimeList id (<c>CrossIdsJson["mal"]</c> - never a title
/// search), <c>GET /v2/manga/{id}?fields=mean,num_scoring_users</c> with the app an admin registered for this server (its client id only - no
/// user, no token). Only while the metadata fetcher's automatic gate is open (its consent names this request) AND MyAnimeList is allowed for
/// this server with a registered app (the tracker's own gate and <c>Trackers:NetworkDisabled</c>). At most <see cref="PerRun"/> a run, every
/// <see cref="CommunityScorePass.Interval"/> (so at most 400 a day), paced with the tracker's MyAnimeList pacing, stopped when MyAnimeList
/// asks to slow down. The mean and scoring users are stored on the MangaDex record, read again after <see cref="CommunityScorePass.MaxAge"/>,
/// shown with their source and never used as a recommendation signal (the MAL API licence limits the purpose, 2(a)). Logs counts only.
/// </summary>
public sealed class MalScorePass(
    MangaPixerDbContext db, MetadataAutoMatchService autoMatch, LatestUploadSweep sweep, TrackerSettingsService trackers, IMalCommunityScores mal,
    TimeProvider time, ILogger<MalScorePass> logger)
{
    internal const string JobKey = "metadata-mal-scores";

    /// <summary>Requests per run (four runs a day at most: 400 a day).</summary>
    public const int PerRun = 100;

    public async Task<bool> IsDueAsync(CancellationToken ct = default)
    {
        var at = await db.JobRuns.AsNoTracking().Where(j => j.Key == JobKey).Select(j => j.LastStartedAt).FirstOrDefaultAsync(ct);
        var now = time.GetUtcNow();
        return at is not { } last || now - last >= CommunityScorePass.Interval || last > now;
    }

    /// <summary>One pass; null when a gate is closed (nothing sent, nothing recorded).</summary>
    public async Task<MalScorePassResult?> RunAsync(CancellationToken ct = default)
    {
        if (await autoMatch.CheckGlobalGateAsync(ct) is { } wait && wait.Code != "matcher_unavailable")
            return null;
        if (!await trackers.IsAllowedAsync(TrackerSlugs.MyAnimeList, ct))
            return null;
        var app = await trackers.AppSettingsAsync(TrackerSlugs.MyAnimeList, ct);
        if (string.IsNullOrWhiteSpace(app.ClientId))
            return null;

        var started = time.GetUtcNow();
        var run = await db.JobRuns.FirstOrDefaultAsync(j => j.Key == JobKey, ct);
        if (run is null)
        {
            run = new Persistence.Entities.JobRunEntity { Key = JobKey };
            db.JobRuns.Add(run);
        }
        run.LastStartedAt = started;
        await db.SaveChangesAsync(ct);

        var targets = (await sweep.TargetsAsync(ct)).Select(t => t.RecordId).ToList();
        var staleBefore = started - CommunityScorePass.MaxAge;
        var due = new List<(long Id, string Mal)>();
        foreach (var chunk in targets.Chunk(500))
        {
            var rows = await db.MetadataRecords.AsNoTracking()
                .Where(r => chunk.Contains(r.Id) && r.CrossIdsJson != null && (r.MalScoreAt == null || r.MalScoreAt <= staleBefore))
                .Select(r => new { r.Id, r.CrossIdsJson, r.MalScoreAt })
                .ToListAsync(ct);
            due.AddRange(rows.OrderBy(r => r.MalScoreAt ?? DateTimeOffset.MinValue).ThenBy(r => r.Id)
                .Select(r => (r.Id, Mal: MalIdOf(r.CrossIdsJson)))
                .Where(r => r.Mal is not null)
                .Select(r => (r.Id, r.Mal!)));
        }

        int requests = 0, scored = 0;
        string? stopped = null;
        foreach (var (id, malId) in due.Take(PerRun))
        {
            MalCommunityScore? score;
            try
            {
                score = await mal.CommunityScoreAsync(app, malId, ct);
            }
            catch (TrackerException ex)
            {
                stopped = ex.Failure.ToString();
                break;
            }
            requests++;
            var row = await db.MetadataRecords.FirstAsync(r => r.Id == id, ct);
            (row.MalScore, row.MalVotes, row.MalScoreAt) = (score?.Mean, score?.ScoringUsers, time.GetUtcNow());
            if (score?.Mean is not null)
                scored++;
            await db.SaveChangesAsync(ct);
        }

        run.LastFinishedAt = time.GetUtcNow();
        run.LastOutcome = stopped ?? "ok";
        run.LastDetail = $"{requests} read, {scored} scored, {Math.Max(0, due.Count - requests)} waiting";
        await db.SaveChangesAsync(ct);
        if (requests > 0 || stopped is not null)
        {
            logger.LogInformation(LogEvents.Metadata.CommunityScorePass,
                "MyAnimeList community scores: {Requests} requests, {Scored} with a score, {Waiting} waiting; {Outcome}",
                requests, scored, Math.Max(0, due.Count - requests), stopped ?? "ok");
        }
        return new MalScorePassResult(requests, scored, stopped);
    }

    /// <summary>
    /// "Delete stored MyAnimeList scores" (the MAL API licence, 3(e): delete on request within 24 hours): clears every stored MyAnimeList
    /// score at once; they come back on the next pass only while MyAnimeList stays allowed. Returns how many records held one.
    /// </summary>
    public async Task<int> DeleteAllAsync(CancellationToken ct = default)
    {
        var cleared = await db.MetadataRecords.Where(r => r.MalScoreAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.MalScore, (double?)null).SetProperty(r => r.MalVotes, (int?)null)
                .SetProperty(r => r.MalScoreAt, (DateTimeOffset?)null), ct);
        logger.LogInformation(LogEvents.Metadata.MalScoresDeleted, "MyAnimeList community scores deleted: {Records} records", cleared);
        return cleared;
    }

    /// <summary>The MyAnimeList id a stored record names (<c>CrossIdsJson["mal"]</c>), or null.</summary>
    internal static string? MalIdOf(string? crossIdsJson)
    {
        if (string.IsNullOrWhiteSpace(crossIdsJson))
            return null;
        try
        {
            var id = JsonSerializer.Deserialize<Dictionary<string, string>>(crossIdsJson)?.GetValueOrDefault(TrackerCrossIdKeys.MyAnimeList);
            return TrackerFacts.IsValidRemoteId(id) ? id : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
