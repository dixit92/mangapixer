namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The MangaDex companion of a linked series record (1.29.0, design 1.3). MangaDex is found FROM the MangaUpdates link:
/// a search by that record's own title (or one associated title), accepting only a record whose own <c>links.mu</c>
/// names the linked record (<see cref="MangaDexCrossLink"/>); an admin may choose a record by reference instead, or say
/// "Not on MangaDex". The companion is stored as a <c>metadata_records</c> row (<c>Provider = "mangadex"</c>, never
/// linked to a node) plus a <c>metadata_companions</c> row. Every request goes through
/// <see cref="MetadataGateway.CompanionCallAsync{T}"/>. Logs carry ids and codes only.
/// </summary>
public sealed class CompanionLinkService
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly IMangaDexProvider _mangaDex;
    private readonly VolumeCoverStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<CompanionLinkService> _logger;

    public CompanionLinkService(
        MangaPixerDbContext db, MetadataGateway gateway, IMangaDexProvider mangaDex, VolumeCoverStore store, TimeProvider time,
        ILogger<CompanionLinkService> logger)
    {
        _db = db;
        _gateway = gateway;
        _mangaDex = mangaDex;
        _store = store;
        _time = time;
        _logger = logger;
    }

    public const string MangaDex = MetadataProviderAllowlist.MangaDex;

    /// <summary>The companion row of <paramref name="seriesRecordId"/> for <paramref name="provider"/>, or null.</summary>
    public Task<MetadataCompanionEntity?> FindAsync(long seriesRecordId, string provider, CancellationToken ct = default) =>
        _db.MetadataCompanions.FirstOrDefaultAsync(c => c.RecordId == seriesRecordId && c.Provider == provider, ct);

    /// <summary>True when the MangaDex companion of the series should be looked at (none yet, or due).</summary>
    public static bool IsDue(MetadataCompanionEntity? companion, DateTimeOffset now) =>
        companion is null
        || (companion.State != (int)CompanionState.None && (companion.NextCheckAt is not { } next || next <= now));

    /// <summary>
    /// Finds or refreshes the MangaDex companion of <paramref name="series"/> (a MangaUpdates record): a known record is
    /// re-read by id (an automatic one whose link no longer names the series is searched again); otherwise the
    /// cross-link rule runs (1-2 searches). "Not on MangaDex" is never looked up. Gateway refusals propagate; a provider
    /// failure marks the companion Failed (retried after a day) and propagates too.
    /// </summary>
    public async Task<MetadataCompanionEntity> EnsureMangaDexAsync(
        MetadataRecordEntity series, long libraryId, MetadataCallContext? call, CancellationToken ct = default)
    {
        var companion = await FindAsync(series.Id, MangaDex, ct);
        if (companion is { State: (int)CompanionState.None })
            return companion;
        companion ??= NewCompanion(series.Id, MangaDex);
        var now = _time.GetUtcNow();
        try
        {
            MangaDexManga? found = null;
            var method = CompanionMethod.CrossLink;
            if (companion.CompanionRecordId is { } knownId
                && await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == knownId).Select(r => r.ExternalId).FirstOrDefaultAsync(ct) is { } mangaId)
            {
                var current = await _gateway.CompanionCallAsync(MangaDex, "get", libraryId, c => _mangaDex.GetAsync(mangaId, c), call, ct);
                if (current is not null && (companion.State == (int)CompanionState.Confirmed || MangaDexCrossLink.LinksTo(current.MangaUpdatesLink, series.ExternalId)))
                {
                    found = current;
                    method = (CompanionMethod)companion.Method;
                }
            }
            if (found is null && companion.State != (int)CompanionState.Confirmed)
                found = await CrossLinkAsync(series, libraryId, call, ct);

            if (found is null)
            {
                await DetachRecordAsync(companion, ct);
                companion.State = (int)CompanionState.NotFound;
                companion.Method = (int)CompanionMethod.CrossLink;
            }
            else
            {
                var record = await UpsertRecordAsync(found, series.ExternalId, ct);
                await SwitchRecordAsync(companion, record.Id, ct);
                companion.State = companion.State == (int)CompanionState.Confirmed ? (int)CompanionState.Confirmed : (int)CompanionState.Auto;
                companion.Method = (int)method;
            }
            companion.CheckedAt = now;
            companion.NextCheckAt = CompanionSchedule.NextCheck(series, now);
            await SaveAsync(companion, ct);
            _logger.LogInformation(LogEvents.Metadata.CompanionChecked, "Metadata companion {Provider} of record {RecordId}: {State}",
                MangaDex, series.Id, (CompanionState)companion.State);
            return companion;
        }
        catch (MetadataGatewayException ex) when (!AutoMatch.MetadataAutoMatchService.IsRefusal(ex))
        {
            companion.State = companion.CompanionRecordId is null ? (int)CompanionState.Failed : companion.State;
            companion.CheckedAt = now;
            companion.NextCheckAt = CompanionSchedule.AfterFailure(now);
            await SaveAsync(companion, ct);
            _logger.LogInformation(LogEvents.Metadata.CompanionChecked, "Metadata companion {Provider} of record {RecordId}: failed {Code}",
                MangaDex, series.Id, ex.Code);
            throw;
        }
    }

    /// <summary>
    /// An admin's choice (Interactive): the MangaDex record with <paramref name="mangaId"/> becomes the companion
    /// (Confirmed), after one GET by id. Null when MangaDex does not have it.
    /// </summary>
    public async Task<MetadataCompanionEntity?> SetByReferenceAsync(MetadataRecordEntity series, long libraryId, string mangaId, CancellationToken ct = default)
    {
        var manga = await _gateway.CompanionCallAsync(MangaDex, "get", libraryId, c => _mangaDex.GetAsync(mangaId, c), null, ct);
        if (manga is null)
            return null;
        var companion = await FindAsync(series.Id, MangaDex, ct) ?? NewCompanion(series.Id, MangaDex);
        var record = await UpsertRecordAsync(manga, series.ExternalId, ct);
        await SwitchRecordAsync(companion, record.Id, ct);
        var now = _time.GetUtcNow();
        companion.State = (int)CompanionState.Confirmed;
        companion.Method = (int)CompanionMethod.Reference;
        companion.CheckedAt = now;
        companion.NextCheckAt = CompanionSchedule.NextCheck(series, now);
        await SaveAsync(companion, ct);
        return companion;
    }

    /// <summary>"Not on MangaDex": the companion is None (never looked up again) and its stored covers are removed.</summary>
    public async Task<MetadataCompanionEntity> SetNoneAsync(MetadataRecordEntity series, CancellationToken ct = default)
    {
        var companion = await FindAsync(series.Id, MangaDex, ct) ?? NewCompanion(series.Id, MangaDex);
        await DetachRecordAsync(companion, ct);
        companion.State = (int)CompanionState.None;
        companion.Method = (int)CompanionMethod.Reference;
        companion.CheckedAt = _time.GetUtcNow();
        companion.NextCheckAt = null;
        await SaveAsync(companion, ct);
        return companion;
    }

    /// <summary>An AniList totals lookup failed at the provider: tried again after a day.</summary>
    public async Task RecordAniListFailedAsync(MetadataRecordEntity series, CancellationToken ct = default)
    {
        var companion = await FindAsync(series.Id, MetadataProviderAllowlist.AniList, ct) ?? NewCompanion(series.Id, MetadataProviderAllowlist.AniList);
        var now = _time.GetUtcNow();
        if (companion.CompanionRecordId is null)
            companion.State = (int)CompanionState.Failed;
        companion.CheckedAt = now;
        companion.NextCheckAt = CompanionSchedule.AfterFailure(now);
        await SaveAsync(companion, ct);
    }

    /// <summary>Records the AniList companion state of a series (the totals lookup's outcome).</summary>
    public async Task RecordAniListAsync(
        MetadataRecordEntity series, MetadataRecordEntity? aniList, bool byId, bool fromMangaDex, DateTimeOffset nextCheck, CancellationToken ct = default)
    {
        var companion = await FindAsync(series.Id, MetadataProviderAllowlist.AniList, ct) ?? NewCompanion(series.Id, MetadataProviderAllowlist.AniList);
        companion.CompanionRecordId = aniList?.Id;
        companion.State = aniList is null ? (int)CompanionState.NotFound : (int)CompanionState.Auto;
        companion.Method = (int)(fromMangaDex && byId ? CompanionMethod.FromCompanion : CompanionMethod.CrossLink);
        companion.CheckedAt = _time.GetUtcNow();
        companion.NextCheckAt = nextCheck;
        await SaveAsync(companion, ct);
    }

    private async Task<MangaDexManga?> CrossLinkAsync(MetadataRecordEntity series, long libraryId, MetadataCallContext? call, CancellationToken ct)
    {
        var storedAniList = await _db.MetadataCompanions.AsNoTracking()
            .Where(c => c.RecordId == series.Id && c.Provider == MetadataProviderAllowlist.AniList && c.CompanionRecordId != null)
            .Join(_db.MetadataRecords, c => c.CompanionRecordId, r => r.Id, (c, r) => r.ExternalId)
            .FirstOrDefaultAsync(ct);

        var query1 = MangaDexCrossLink.Query1(series.Title);
        if (query1.Length is > 0 and <= MetadataGateway.MaxQueryLength)
        {
            var results = await _gateway.CompanionCallAsync(MangaDex, "search", libraryId, c => _mangaDex.SearchAsync(query1, c), call, ct);
            if (MangaDexCrossLink.Pick(results, series.ExternalId, storedAniList) is { } hit)
                return hit;
        }
        var query2 = MangaDexCrossLink.Query2(MetadataJson.ReadList<string>(series.AltTitlesJson), query1);
        if (query2 is null)
            return null;
        var second = await _gateway.CompanionCallAsync(MangaDex, "search", libraryId, c => _mangaDex.SearchAsync(query2, c), call, ct);
        return MangaDexCrossLink.Pick(second, series.ExternalId, storedAniList);
    }

    /// <summary>The MangaDex record row (created or updated from the answer). Its cover list is kept by <see cref="VolumeMapService"/>.</summary>
    public async Task<MetadataRecordEntity> UpsertRecordAsync(MangaDexManga manga, string muExternalId, CancellationToken ct)
    {
        var record = await _db.MetadataRecords.FirstOrDefaultAsync(r => r.Provider == MangaDex && r.ExternalId == manga.Id, ct);
        if (record is null)
        {
            record = new MetadataRecordEntity { PublicId = await NewRecordPublicIdAsync(ct), Provider = MangaDex, ExternalId = manga.Id };
            _db.MetadataRecords.Add(record);
        }
        var cross = new Dictionary<string, string> { [MetadataProviderAllowlist.MangaUpdates] = muExternalId };
        if (manga.AniListId is { } al)
            cross[MetadataProviderAllowlist.AniList] = al;
        record.Title = manga.Title;
        record.AltTitlesJson = MetadataJson.WriteList(manga.AltTitles.ToList());
        record.Origin = (int?)OriginOf(manga.OriginalLanguage);
        record.StartYear = manga.Year;
        record.OriginVolumes = int.TryParse(manga.LastVolume, NumberStyles.None, CultureInfo.InvariantCulture, out var last) && last is > 0 and < 10_000 ? last : null;
        record.CrossIdsJson = JsonSerializer.Serialize(cross);
        record.SiteUrl = MangaDexProvider.SiteUrl(manga.Id);
        record.ExtraJson = JsonSerializer.Serialize(new MangaDexExtra(manga.OriginalLanguage, manga.MainCoverId, manga.MainCoverFile));
        record.FetchedAt = _time.GetUtcNow();
        record.FetchState = 0;
        await _db.SaveChangesAsync(ct);
        return record;
    }

    /// <summary>What a MangaDex record row keeps in <c>ExtraJson</c>: the original language and the main cover.</summary>
    public sealed record MangaDexExtra(string? OriginalLanguage, string? MainCoverId, string? MainCoverFile)
    {
        public static MangaDexExtra Read(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new(null, null, null);
            try
            {
                return JsonSerializer.Deserialize<MangaDexExtra>(json) ?? new(null, null, null);
            }
            catch (JsonException)
            {
                return new(null, null, null);
            }
        }
    }

    private static MetadataOrigin? OriginOf(string? language) => language switch
    {
        null => null,
        "ja" => MetadataOrigin.Japan,
        "ko" => MetadataOrigin.Korea,
        "zh" or "zh-hk" or "zh-ro" => MetadataOrigin.ChinaTaiwan,
        "en" => MetadataOrigin.EnglishOriginal,
        _ => MetadataOrigin.Other,
    };

    /// <summary>Points the companion at another MangaDex record; the previous record's covers and map are dropped.</summary>
    private async Task SwitchRecordAsync(MetadataCompanionEntity companion, long recordId, CancellationToken ct)
    {
        if (companion.CompanionRecordId == recordId)
            return;
        await DetachRecordAsync(companion, ct);
        companion.CompanionRecordId = recordId;
        // A different MangaDex record: the lists are read again on the next pass.
        await _db.SeriesVolumeMaps
            .Where(m => m.RecordId == companion.RecordId && m.Source == (int)VolumeMapSource.MangaDexAggregate)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.NextCheckAt, (DateTimeOffset?)null), ct);
    }

    /// <summary>Unhooks the MangaDex record; removes it (with its stored covers) when no other series uses it.</summary>
    private async Task DetachRecordAsync(MetadataCompanionEntity companion, CancellationToken ct)
    {
        if (companion.CompanionRecordId is not { } old)
            return;
        companion.CompanionRecordId = null;
        await SaveAsync(companion, ct);
        if (await _db.MetadataCompanions.AnyAsync(c => c.CompanionRecordId == old, ct))
            return;
        foreach (var cover in await _db.VolumeCovers.AsNoTracking().Where(c => c.ProviderRecordId == old && c.StoredVersion > 0)
                     .Select(c => new { c.PublicId, c.StoredVersion }).ToListAsync(ct))
            _store.Delete(cover.PublicId, cover.StoredVersion);
        await _db.VolumeCovers.Where(c => c.ProviderRecordId == old).ExecuteDeleteAsync(ct);
        await _db.MetadataRecords.Where(r => r.Id == old && r.Provider == MangaDex).ExecuteDeleteAsync(ct);
        await _db.SeriesVolumeMaps
            .Where(m => m.RecordId == companion.RecordId && m.Source == (int)VolumeMapSource.MangaDexAggregate)
            .ExecuteDeleteAsync(ct);
    }

    private async Task SaveAsync(MetadataCompanionEntity companion, CancellationToken ct)
    {
        if (companion.Id == 0 && _db.Entry(companion).State == EntityState.Detached)
            _db.MetadataCompanions.Add(companion);
        await _db.SaveChangesAsync(ct);
    }

    private static MetadataCompanionEntity NewCompanion(long seriesRecordId, string provider) =>
        new() { RecordId = seriesRecordId, Provider = provider, State = (int)CompanionState.NotFound };

    private async Task<string> NewRecordPublicIdAsync(CancellationToken ct)
    {
        while (true)
        {
            var id = "mr" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (!await _db.MetadataRecords.AnyAsync(r => r.PublicId == id, ct))
                return id;
        }
    }
}
