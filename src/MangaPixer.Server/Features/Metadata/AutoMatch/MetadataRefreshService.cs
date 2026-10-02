namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Background id-only refresh of linked records (metadata stage 2, owner decision
/// 14): a record linked Confirmed or Auto is re-fetched by id every 30 days while
/// it is ongoing and every 90 days once it is complete; a record the provider no
/// longer has (gone) never. At most <see cref="MaxPerDay"/> GETs per UTC day
/// (persisted, survives restarts), inside the ONE daily budget, behind the same
/// automatic gate as auto-match, oldest first; paced by the gateway at &lt;= 1/s.
/// Sends record ids only - never a name. The poster is fetched again only when its
/// URL changed.
/// </summary>
public sealed class MetadataRefreshService
{
    public const int MaxPerDay = 100;
    /// <summary>The cadence lives in <see cref="RefreshCadence"/> (1.32.0); these stay as its names here.</summary>
    public static TimeSpan OngoingAge => RefreshCadence.OngoingAge;
    public static TimeSpan CompleteAge => RefreshCadence.FinishedAge;

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

    /// <summary>One pass; returns how many records were refreshed (or found gone).</summary>
    public async Task<int> RunPassAsync(CancellationToken ct = default)
    {
        var wait = await _autoMatch.CheckGlobalGateAsync(ct);
        if (wait is not null && wait.Code != "matcher_unavailable")
            return 0; // Refresh needs no matcher, but everything else of the gate.

        var now = _time.GetUtcNow();
        var ongoingBefore = now - OngoingAge;
        var completeBefore = now - CompleteAge;
        var complete = RefreshCadence.FinishedStatuses.ToArray();
        var linked = new[] { (int)SeriesLinkState.Confirmed, (int)SeriesLinkState.Auto };

        var remaining = MaxPerDay - await UsedTodayAsync(ct);
        if (remaining <= 0)
            return 0;

        var due = await _db.MetadataRecords.AsNoTracking()
            .Where(r => r.FetchState != 1
                && (complete.Contains(r.OriginStatus) ? r.FetchedAt < completeBefore : r.FetchedAt < ongoingBefore)
                && _db.NodeSeriesLinks.Any(l => l.RecordId == r.Id && linked.Contains(l.State)
                    && _db.Libraries.Any(lib => lib.Id == l.LibraryId && lib.MetadataEnabled)))
            .OrderBy(r => r.FetchedAt)
            .Select(r => r.Id)
            .Take(remaining)
            .ToListAsync(ct);

        var done = 0;
        foreach (var recordId in due)
        {
            if (!await TryTakeSlotAsync(ct))
                break;
            var record = await _db.MetadataRecords.FirstAsync(r => r.Id == recordId, ct);
            var libraryId = await _db.NodeSeriesLinks.AsNoTracking()
                .Where(l => l.RecordId == recordId && linked.Contains(l.State) && _db.Libraries.Any(lib => lib.Id == l.LibraryId && lib.MetadataEnabled))
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
                    // 1.30.0 (reach): refreshed totals may contradict an Auto link's reach (never throws).
                    await _reach.TryCheckRecordAsync(record.Id, ct);
                    if (record.ImageRemoteUrl is not null && (record.ImageState != 1 || !string.Equals(oldImage, record.ImageRemoteUrl, StringComparison.Ordinal)))
                        await _identify.TryStoreImageAsync(record, libraryId, ct, call);
                }
                done++;
            }
            catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
            {
                break; // The gate closed (switch, budget, backoff): the next pass continues.
            }
            catch (MetadataGatewayException ex)
            {
                record.FetchState = 2;
                await _db.SaveChangesAsync(ct);
                _logger.LogInformation(LogEvents.Metadata.RefreshFailed, "Metadata refresh of record {RecordId} failed: {Code}", recordId, ex.Code);
            }
        }
        if (done > 0)
            _logger.LogInformation(LogEvents.Metadata.RefreshPass, "Metadata refresh pass: {Count} records refreshed", done);
        return done;
    }

    private async Task<int> UsedTodayAsync(CancellationToken ct)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataRefreshDayUtc, s.MetadataRefreshUsed })
            .FirstOrDefaultAsync(ct);
        return row?.MetadataRefreshDayUtc is { } day && day == _budget.Today() ? row.MetadataRefreshUsed : 0;
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
