namespace com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>What one sweep did (counts only); <see cref="StoppedCode"/> null = it completed.</summary>
public sealed record LatestUploadSweepResult(int Requests, int Records, int Changed, int Libraries, string? StoppedCode);

/// <summary>
/// The MangaDex latest-upload sweep (1.45.0, lane R; MangaList's recommendation 1 and the shared chapter signal): every few hours,
/// <c>GET /manga?ids[]=..&amp;limit=100&amp;contentRating[]=</c>(all four) with the MangaDex ids of the linked series' MangaDex records -
/// the companions, and a series linked to MangaDex itself - 100 per request, through the gateway like every MangaDex call (the automatic
/// gate and consent, the allowlist, each library's "Fetch from the web", the daily budget, one request a second, the backoff).
/// <list type="bullet">
/// <item>Each record's <c>attributes.latestUploadedChapter</c> is stored (<c>mangadex_latest_uploads</c>) with the moment it was first seen;
/// the same value again moves only its check time - so the export item changes only when the chapter id does.</item>
/// <item>A CHANGED value (not the first read) makes that series' MangaDex volume lists due now (recommendation 5): the volume-cover pass
/// re-reads them on its next tick.</item>
/// <item>A sweep that completes stores its START time for every library it covered (<c>library_freshness.MangaDexSweepAt</c>, the export's
/// <c>sweeps.mangadexLatestUpload</c>); one stopped by the budget, a backoff, a switch or a provider error advances no library.</item>
/// </list>
/// Sends MangaDex record ids only. A series inside a "Don't match" folder is left out (as in the volume-cover pass). Logs ids and counts.
/// </summary>
public sealed class LatestUploadSweep(
    MangaPixerDbContext db, MetadataGateway gateway, IMangaDexProvider mangaDex, MetadataAutoMatchService autoMatch, TimeProvider time,
    ILogger<LatestUploadSweep> logger)
{
    /// <summary>One MangaDex record to read: its stored id and MangaDex id, and the libraries whose linked series use it.</summary>
    internal sealed record Target(long RecordId, string MangaId, IReadOnlyList<long> Libraries);

    /// <summary>True when the sweep is due (the admin's interval since the last completed one, or a retry after a stopped one).</summary>
    public async Task<bool> IsDueAsync(CancellationToken ct = default)
    {
        var state = await NewChapterChecks.PeekAsync(db, ct);
        return NewChapterChecks.IsDue(state?.SweepStartedAt, state?.SweepOutcome, TimeSpan.FromHours(NewChapterChecks.HoursOf(state?.CheckHours)),
            time.GetUtcNow());
    }

    /// <summary>
    /// One sweep. Null when the automatic gate for MangaDex is closed, or no linked series has a MangaDex record yet (nothing sent, nothing
    /// recorded - the sweep waits).
    /// </summary>
    public async Task<LatestUploadSweepResult?> RunAsync(CancellationToken ct = default)
    {
        if (await autoMatch.CheckGlobalGateAsync(MetadataProviderAllowlist.MangaDex, ct) is not null)
            return null;
        var started = time.GetUtcNow();
        var targets = await TargetsAsync(ct);
        // Nothing to read yet (no linked series has a MangaDex record): wait - the first one found is read on the next tick.
        if (targets.Count == 0)
            return null;
        var libraries = await db.Libraries.AsNoTracking().Where(l => l.MetadataEnabled).Select(l => l.Id).OrderBy(id => id).ToListAsync(ct);
        var call = MetadataCallContext.Automatic();
        string? stopped = null;
        int records = 0, changed = 0;
        var changedRecords = new List<long>();

        // Each record is asked for once, for the first library that uses it; batches never mix libraries (each request passes that
        // library's own "Fetch from the web").
        foreach (var group in targets.GroupBy(t => t.Libraries[0]).OrderBy(g => g.Key))
        {
            foreach (var batch in group.Chunk(MangaDexProvider.LatestUploadBatch))
            {
                IReadOnlyList<MangaDexLatestUpload> answer;
                try
                {
                    var ids = batch.Select(t => t.MangaId).ToList();
                    answer = await gateway.CompanionCallAsync(MetadataProviderAllowlist.MangaDex, "latest-uploads", group.Key,
                        c => mangaDex.LatestUploadsAsync(ids, c), call, ct);
                }
                catch (MetadataGatewayException ex)
                {
                    stopped = ex.Code;
                    break;
                }
                var now = time.GetUtcNow();
                var byId = answer.ToDictionary(a => a.MangaId, StringComparer.Ordinal);
                var batchIds = batch.Select(t => t.RecordId).ToList();
                var stored = await db.MangaDexLatestUploads.Where(u => batchIds.Contains(u.RecordId)).ToDictionaryAsync(u => u.RecordId, ct);
                foreach (var target in batch)
                {
                    // A record MangaDex did not return (taken down) keeps its stored value: nothing new is known about it.
                    if (!byId.TryGetValue(target.MangaId, out var upload))
                        continue;
                    records++;
                    if (!stored.TryGetValue(target.RecordId, out var row))
                    {
                        db.MangaDexLatestUploads.Add(new MangaDexLatestUploadEntity
                        {
                            RecordId = target.RecordId,
                            ChapterId = upload.ChapterId,
                            SeenAt = now,
                            CheckedAt = now,
                        });
                        continue;
                    }
                    row.CheckedAt = now;
                    if (string.Equals(row.ChapterId, upload.ChapterId, StringComparison.Ordinal))
                        continue;
                    row.ChapterId = upload.ChapterId;
                    row.SeenAt = now;
                    changed++;
                    changedRecords.Add(target.RecordId);
                }
                await db.SaveChangesAsync(ct);
            }
            if (stopped is not null)
                break;
        }

        var due = await MakeListsDueAsync(changedRecords, ct);
        var state = await NewChapterChecks.StateAsync(db, ct);
        state.SweepStartedAt = started;
        state.SweepFinishedAt = time.GetUtcNow();
        state.SweepOutcome = stopped ?? "ok";
        state.SweepRequests = call.RequestsSent;
        state.SweepRecords = records;
        state.SweepChanged = changed;
        var advanced = 0;
        if (stopped is null)
        {
            // Every MangaDex record of each of these libraries was asked for in this sweep (a library without any is covered too, once
            // any library has one).
            var rows = await db.LibraryFreshness.Where(f => libraries.Contains(f.LibraryId)).ToDictionaryAsync(f => f.LibraryId, ct);
            foreach (var library in libraries)
            {
                if (!rows.TryGetValue(library, out var row))
                    db.LibraryFreshness.Add(row = new LibraryFreshnessEntity { LibraryId = library });
                row.MangaDexSweepAt = started;
                advanced++;
            }
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation(LogEvents.Metadata.LatestUploadSweep,
            "MangaDex latest-upload sweep: {Records} records, {Changed} changed, {Due} volume lists due, {Requests} requests, {Libraries} libraries confirmed; {Outcome}",
            records, changed, due, call.RequestsSent, advanced, stopped ?? "ok");
        return new LatestUploadSweepResult(call.RequestsSent, records, changed, advanced, stopped);
    }

    /// <summary>
    /// The MangaDex records to read: of every series linked Confirmed / Auto from a live node in a library whose "Fetch from the web" is on
    /// (not inside a "Don't match" folder), its found MangaDex companion (a series linked to MangaDex is its own) - with the libraries
    /// that use it, lowest first.
    /// </summary>
    internal async Task<IReadOnlyList<Target>> TargetsAsync(CancellationToken ct)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var tombstoned = (int)Core.Catalog.CatalogNodeAvailability.Tombstoned;
        var found = new[] { (int)CompanionState.Auto, (int)CompanionState.Confirmed };
        var mangaDexId = MetadataProviderAllowlist.MangaDex;
        var rows = await (
            from l in db.NodeSeriesLinks.AsNoTracking()
            where (l.State == confirmed || l.State == auto) && l.RecordId != null
            join lib in db.Libraries.AsNoTracking() on l.LibraryId equals lib.Id
            where lib.MetadataEnabled
            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.Availability != tombstoned
            join c in db.MetadataCompanions.AsNoTracking() on l.RecordId equals c.RecordId
            where c.Provider == mangaDexId && found.Contains(c.State) && c.CompanionRecordId != null
            join md in db.MetadataRecords.AsNoTracking() on c.CompanionRecordId equals md.Id
            where md.Provider == mangaDexId
            select new { l.NodeId, n.ParentId, l.LibraryId, md.Id, md.ExternalId })
            .ToListAsync(ct);
        var excluded = await VolumeCoverPass.UnderDontMatchAsync(db, rows.Select(r => (r.NodeId, r.ParentId)).Distinct().ToList(), ct);
        return rows.Where(r => !excluded.Contains(r.NodeId) && MangaDexProvider.IsValidId(r.ExternalId))
            .GroupBy(r => r.Id)
            .Select(g => new Target(g.Key, g.First().ExternalId, g.Select(r => r.LibraryId).Distinct().Order().ToList()))
            .OrderBy(t => t.Libraries[0]).ThenBy(t => t.RecordId)
            .ToList();
    }

    /// <summary>
    /// Recommendation 5: the series whose MangaDex record has a new latest upload get their MangaDex volume lists due now (the
    /// volume-cover pass reads them on its next tick, inside its own switch). Returns how many lists were made due.
    /// </summary>
    private async Task<int> MakeListsDueAsync(IReadOnlyList<long> mangaDexRecords, CancellationToken ct)
    {
        if (mangaDexRecords.Count == 0)
            return 0;
        var mangaDexId = MetadataProviderAllowlist.MangaDex;
        var series = await db.MetadataCompanions.AsNoTracking()
            .Where(c => c.Provider == mangaDexId && c.CompanionRecordId != null && mangaDexRecords.Contains(c.CompanionRecordId.Value))
            .Select(c => c.RecordId).Distinct().ToListAsync(ct);
        var now = time.GetUtcNow();
        var aggregate = (int)VolumeMapSource.MangaDexAggregate;
        var made = await db.SeriesVolumeMaps
            .Where(m => series.Contains(m.RecordId) && m.Source == aggregate && m.NextCheckAt != null && m.NextCheckAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, now), ct);
        if (made > 0)
            logger.LogInformation(LogEvents.Metadata.CrossTrigger, "New MangaDex uploads: {Lists} volume lists of {Series} series due now", made, series.Count);
        return made;
    }
}
