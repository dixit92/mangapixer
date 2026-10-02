namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Background id-only refresh of linked records (metadata stage 2, owner decision 14; 1.32.0 scheduled and on the admin's
/// cadence): once a day at the admin's hour (the Scheduled jobs section, <see cref="Hosting.MetadataAutoMatchHostedService"/>), a
/// record linked Confirmed or Auto is re-fetched by id when its cadence has passed since it was fetched - the admin's choice
/// for ongoing and finished series, faster for a series that publishes quickly (<see cref="RefreshCadence.For"/>); a record the
/// provider no longer has (gone) never. At most <see cref="MaxPerDay"/> GETs per budget day (persisted, survives restarts),
/// inside the ONE daily budget, behind the same automatic gate as auto-match, oldest first; paced by the gateway at &lt;= 1/s.
/// Sends record ids only - never a name. The poster is fetched again only when its URL changed. Each pass first recomputes
/// every linked record's cadence (stored in <c>RefreshCadenceDays</c>, which the companions and the cover re-check follow too)
/// and notes what the refresh saw (<see cref="RefreshObservations"/>).
/// </summary>
public sealed class MetadataRefreshService
{
    /// <summary>At most this many refresh GETs a day (1.32.0 provisional decision Q6: 200; was 100).</summary>
    public const int MaxPerDay = 200;

    private static readonly int[] Linked = [(int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto];

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly MetadataAutoMatchService _autoMatch;
    private readonly MetadataIdentifyService _identify;
    private readonly MetadataBudget _budget;
    private readonly MetadataGatewayState _gatewayState;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataRefreshService> _logger;
    private readonly Reach.ReachCheckService _reach;

    public MetadataRefreshService(
        MangaPixerDbContext db,
        MetadataGateway gateway,
        MetadataAutoMatchService autoMatch,
        MetadataIdentifyService identify,
        MetadataBudget budget,
        MetadataGatewayState gatewayState,
        TimeProvider time,
        ILogger<MetadataRefreshService> logger,
        Reach.ReachCheckService reach)
    {
        _db = db;
        _gateway = gateway;
        _autoMatch = autoMatch;
        _identify = identify;
        _budget = budget;
        _gatewayState = gatewayState;
        _time = time;
        _logger = logger;
        _reach = reach;
    }

    /// <summary>The admin's cadence choice.</summary>
    public async Task<RefreshCadencePolicy> PolicyAsync(CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataRefreshOngoingDays, s.MetadataRefreshFinishedDays, s.MetadataRefreshFollowPace })
            .FirstOrDefaultAsync(ct);
        return row is null
            ? RefreshCadencePolicy.Default
            : RefreshCadencePolicy.FromStored(row.MetadataRefreshOngoingDays, row.MetadataRefreshFinishedDays, row.MetadataRefreshFollowPace);
    }

    /// <summary>A linked record's cadence inputs.</summary>
    private sealed record LinkedRecord(long Id, DateTimeOffset FetchedAt, int FetchState, int? OriginStatus, int? StartYear, int? OriginVolumes,
        double? LatestChapter, int? RefreshCadenceDays);

    private IQueryable<MetadataRecordEntity> LinkedRecords() =>
        _db.MetadataRecords.Where(r => _db.NodeSeriesLinks.Any(l => l.RecordId == r.Id && Linked.Contains(l.State)
            && _db.Libraries.Any(lib => lib.Id == l.LibraryId && lib.MetadataEnabled)));

    /// <summary>What the cadence of one record is computed from (its observations, oldest first, and the volume map's chapters a volume).</summary>
    public async Task<RefreshEvidence> EvidenceAsync(MetadataRecordEntity record, CancellationToken ct = default)
    {
        var history = await _db.MetadataRecordObservations.AsNoTracking()
            .Where(o => o.RecordId == record.Id)
            .OrderBy(o => o.ObservedAt).ThenBy(o => o.Id)
            .ToListAsync(ct);
        var perVolume = await _db.SeriesVolumeMaps.AsNoTracking()
            .Where(m => m.RecordId == record.Id && m.ChaptersPerVolume != null)
            .OrderBy(m => m.Source)
            .Select(m => m.ChaptersPerVolume)
            .FirstOrDefaultAsync(ct);
        return new RefreshEvidence(record.OriginStatus, record.StartYear, record.OriginVolumes, record.LatestChapter, perVolume,
            history.Select(RefreshObservations.ToCore).ToList());
    }

    /// <summary>
    /// Recomputes and stores the cadence of every linked record (no request): writes a baseline observation for a record that
    /// has none, then <see cref="RefreshCadence.For"/> under the admin's policy. Returns the records' cadences by id.
    /// </summary>
    public async Task<IReadOnlyDictionary<long, int>> RecomputeCadencesAsync(CancellationToken ct = default)
    {
        var policy = await PolicyAsync(ct);
        var now = _time.GetUtcNow();
        var records = await LinkedRecords().AsNoTracking()
            .Select(r => new LinkedRecord(r.Id, r.FetchedAt, r.FetchState, r.OriginStatus, r.StartYear, r.OriginVolumes, r.LatestChapter, r.RefreshCadenceDays))
            .ToListAsync(ct);
        var ids = records.Select(r => r.Id).ToList();
        var observations = (await _db.MetadataRecordObservations.AsNoTracking()
                .Where(o => ids.Contains(o.RecordId))
                .ToListAsync(ct))
            .GroupBy(o => o.RecordId)
            .ToDictionary(g => g.Key, g => g.OrderBy(o => o.ObservedAt).ThenBy(o => o.Id).ToList());
        var perVolume = (await _db.SeriesVolumeMaps.AsNoTracking()
                .Where(m => ids.Contains(m.RecordId) && m.ChaptersPerVolume != null)
                .Select(m => new { m.RecordId, m.Source, m.ChaptersPerVolume })
                .ToListAsync(ct))
            .GroupBy(m => m.RecordId)
            .ToDictionary(g => g.Key, g => g.OrderBy(m => m.Source).First().ChaptersPerVolume);

        var baselines = 0;
        var cadences = new Dictionary<long, int>();
        var changed = new Dictionary<long, int>();
        foreach (var r in records)
        {
            if (!observations.TryGetValue(r.Id, out var history))
            {
                var baseline = new MetadataRecordObservationEntity
                {
                    RecordId = r.Id,
                    ObservedAt = r.FetchedAt,
                    LatestChapter = r.LatestChapter,
                    OriginVolumes = r.OriginVolumes,
                    OriginStatus = r.OriginStatus,
                };
                _db.MetadataRecordObservations.Add(baseline);
                history = [baseline];
                baselines++;
            }
            var evidence = new RefreshEvidence(r.OriginStatus, r.StartYear, r.OriginVolumes, r.LatestChapter, perVolume.GetValueOrDefault(r.Id),
                history.Select(RefreshObservations.ToCore).ToList());
            var days = RefreshCadence.For(policy, evidence, now).Days;
            cadences[r.Id] = days;
            if (r.RefreshCadenceDays != days)
                changed[r.Id] = days;
        }
        if (baselines > 0)
            await _db.SaveChangesAsync(ct);
        foreach (var group in changed.GroupBy(c => c.Value))
        {
            var groupIds = group.Select(c => c.Key).ToList();
            await _db.MetadataRecords.Where(r => groupIds.Contains(r.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RefreshCadenceDays, group.Key), ct);
        }
        await PullInCompanionChecksAsync(changed, ct);
        return cadences;
    }

    /// <summary>
    /// One cadence everywhere: when a series' cadence got shorter, its companion checks and volume list already scheduled further
    /// out come in to (last check + the new cadence). A longer cadence applies from their next check on.
    /// </summary>
    private async Task PullInCompanionChecksAsync(IReadOnlyDictionary<long, int> changed, CancellationToken ct)
    {
        if (changed.Count == 0)
            return;
        var ids = changed.Keys.ToList();
        var companions = await _db.MetadataCompanions.Where(c => ids.Contains(c.RecordId) && c.NextCheckAt != null && c.CheckedAt != null).ToListAsync(ct);
        foreach (var c in companions)
        {
            var latest = c.CheckedAt!.Value + TimeSpan.FromDays(changed[c.RecordId]);
            if (c.NextCheckAt > latest)
                c.NextCheckAt = latest;
        }
        var maps = await _db.SeriesVolumeMaps.Where(m => ids.Contains(m.RecordId) && m.NextCheckAt != null).ToListAsync(ct);
        foreach (var m in maps)
        {
            var latest = m.FetchedAt + TimeSpan.FromDays(changed[m.RecordId]);
            if (m.NextCheckAt > latest)
                m.NextCheckAt = latest;
        }
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// One pass; returns how many records were refreshed (or found gone), how many were due, and why it stopped early (the
    /// gate's code) if it did.
    /// </summary>
    public async Task<RefreshPassResult> RunPassAsync(CancellationToken ct = default)
    {
        var cadences = await RecomputeCadencesAsync(ct);

        var wait = await _autoMatch.CheckGlobalGateAsync(ct);
        if (wait is not null && wait.Code != "matcher_unavailable")
            return new RefreshPassResult(0, 0, wait.Code); // Refresh needs no matcher, but everything else of the gate.

        var now = _time.GetUtcNow();
        var candidates = await LinkedRecords().AsNoTracking()
            .Where(r => r.FetchState != 1)
            .Select(r => new { r.Id, r.FetchedAt, r.OriginStatus })
            .ToListAsync(ct);
        var due = candidates
            .Where(r => r.FetchedAt + TimeSpan.FromDays(cadences.TryGetValue(r.Id, out var d) ? d : (int)RefreshCadence.AgeFor(r.OriginStatus).TotalDays) <= now)
            .OrderBy(r => r.FetchedAt)
            .Select(r => r.Id)
            .ToList();

        var remaining = MaxPerDay - await UsedTodayAsync(ct);
        if (remaining <= 0)
            return new RefreshPassResult(0, due.Count, due.Count > 0 ? "refresh_cap" : null);

        var done = 0;
        string? stopped = null;
        // 1.32.0: a refusal by ONE provider (busy - GCD's slow bucket -, backing off, removed from the allowed sites) skips that
        // provider's records for the rest of the pass; the other providers' records go on. Anything else closes the pass.
        var refusedProviders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var recordId in due.Take(remaining))
        {
            var record = await _db.MetadataRecords.FirstAsync(r => r.Id == recordId, ct);
            if (refusedProviders.Contains(record.Provider))
                continue;
            if (!await TryTakeSlotAsync(ct))
            {
                stopped = "refresh_cap";
                break;
            }
            var libraryId = await _db.NodeSeriesLinks.AsNoTracking()
                .Where(l => l.RecordId == recordId && Linked.Contains(l.State) && _db.Libraries.Any(lib => lib.Id == l.LibraryId && lib.MetadataEnabled))
                .Select(l => l.LibraryId)
                .FirstOrDefaultAsync(ct);
            var call = MetadataCallContext.Automatic();
            try
            {
                var fetched = await _gateway.GetSeriesAsync(record.Provider, libraryId, record.ExternalId, ct, call);
                var at = _time.GetUtcNow();
                if (fetched is null)
                {
                    record.FetchState = 1;
                    record.FetchedAt = at;
                    await _db.SaveChangesAsync(ct);
                }
                else
                {
                    var oldImage = record.ImageRemoteUrl;
                    MetadataIdentifyService.Apply(record, fetched, at);
                    await _db.SaveChangesAsync(ct);
                    // 1.32.0: what the refresh saw, and the record's cadence from it.
                    await RefreshObservations.NoteAsync(_db, record, at, ct);
                    var days = RefreshCadence.For(await PolicyAsync(ct), await EvidenceAsync(record, ct), at).Days;
                    if (record.RefreshCadenceDays != days)
                    {
                        record.RefreshCadenceDays = days;
                        await _db.SaveChangesAsync(ct);
                    }
                    // 1.30.0 (reach): refreshed totals may contradict an Auto link's reach (never throws).
                    await _reach.TryCheckRecordAsync(record.Id, ct);
                    if (record.ImageRemoteUrl is not null && (record.ImageState != 1 || !string.Equals(oldImage, record.ImageRemoteUrl, StringComparison.Ordinal)))
                        await _identify.TryStoreImageAsync(record, libraryId, ct, call);
                }
                done++;
            }
            catch (MetadataGatewayException ex) when (IsProviderRefusal(ex))
            {
                refusedProviders.Add(record.Provider); // This provider only; the record stays due for the next pass.
                stopped ??= ex.Code;
            }
            catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
            {
                stopped = ex.Code; // The gate closed (switch, budget): the next pass continues.
                break;
            }
            catch (MetadataGatewayException ex)
            {
                record.FetchState = 2;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(LogEvents.Metadata.RefreshFailed, "Metadata refresh of record {RecordId} failed: {Code}", recordId, ex.Code);
            }
        }
        if (stopped is null && due.Count > remaining)
            stopped = "refresh_cap";
        if (done > 0)
            _logger.LogInformation(LogEvents.Metadata.RefreshPass, "Metadata refresh pass: {Count} records refreshed", done);
        return new RefreshPassResult(done, due.Count, stopped);
    }

    /// <summary>A refusal that concerns one provider only (its bucket, its backoff, its place on the allowed sites).</summary>
    internal static bool IsProviderRefusal(MetadataGatewayException ex) =>
        ex.Code is "provider_busy" or "provider_backoff" or "provider_not_allowed";

    /// <summary>Refresh GETs taken today (the budget day).</summary>
    public async Task<int> UsedTodayAsync(CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataRefreshDayUtc, s.MetadataRefreshUsed })
            .FirstOrDefaultAsync(ct);
        return _budget.IsToday(row?.MetadataRefreshDayUtc) ? row!.MetadataRefreshUsed : 0;
    }

    /// <summary>Linked records past their check date now (for the Scheduled jobs section), and how many follow each cadence.</summary>
    public async Task<(int Overdue, IReadOnlyDictionary<int, int> ByDays)> SummaryAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var rows = await LinkedRecords().AsNoTracking()
            .Where(r => r.FetchState != 1)
            .Select(r => new { r.FetchedAt, r.OriginStatus, r.RefreshCadenceDays })
            .ToListAsync(ct);
        var overdue = rows.Count(r => r.FetchedAt + RefreshCadence.AgeFor(r.RefreshCadenceDays, r.OriginStatus) <= now);
        var byDays = rows.GroupBy(r => (int)RefreshCadence.AgeFor(r.RefreshCadenceDays, r.OriginStatus).TotalDays)
            .ToDictionary(g => g.Key, g => g.Count());
        return (overdue, byDays);
    }

    /// <summary>Takes one of today's refresh slots (serialized with the budget writes).</summary>
    private async Task<bool> TryTakeSlotAsync(CancellationToken ct)
    {
        await _gatewayState.StateLock.WaitAsync(ct);
        try
        {
            var used = await UsedTodayAsync(ct);
            if (used >= MaxPerDay)
                return false;
            var today = _budget.Today();
            var next = used + 1;
            return await _db.AppSettings.Where(s => s.Id == AppSettingsEntity.SingletonId)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(x => x.MetadataRefreshDayUtc, today)
                    .SetProperty(x => x.MetadataRefreshUsed, next), ct) > 0;
        }
        finally
        {
            _gatewayState.StateLock.Release();
        }
    }
}

/// <summary>One refresh pass: records refreshed (or found gone), records that were due, and the code that stopped it early (null = done).</summary>
public readonly record struct RefreshPassResult(int Refreshed, int Due, string? StoppedCode);
