namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The volume lists of a linked series (1.29.0, design P2.3 / 4.2 steps 2-3):
/// <list type="bullet">
/// <item>the MangaDex cover list in the preferred and the original cover language (<c>/cover</c>, 100 per page) ->
/// <c>volume_covers</c> rows (Listed; a changed file or a newer <c>updatedAt</c> is downloaded again; a cover no longer
/// listed is Gone, its file kept) plus the record's main cover (Kind Main, from the search answer - no extra request);</item>
/// <item>the MangaDex volume -> chapter list (<c>aggregate?includeUnavailable=1</c>) -> <c>series_volume_maps</c> (Source
/// MangaDexAggregate), cleaned by <see cref="VolumeListBuilder"/>, with <c>KnownVolumeCount</c> = the highest integer
/// <c>/cover</c> volume, else MangaDex <c>lastVolume</c>, else the series' volume total. Foreign numbering guard: when the
/// list's highest volume exceeds BOTH the series' volume total and the <c>/cover</c> maximum by more than 1 (JoJo parts
/// numbered continuously), the map is stored Empty - nothing groups from it;</item>
/// <item>the same volume list filtered by the preferred language (<c>translatedLanguage[]</c>, one more request) -> the chapters
/// released in it (<c>ReleasedLanguage</c> / <c>ReleasedChaptersJson</c>; owner, 1.29.0 RC: "missing" = released in the preferred
/// language; lane S reads them);</item>
/// <item>when MangaDex gives no volume list, the AniList totals (<see cref="MissingConversionService.LookupSeriesAsync"/>:
/// by the AniList id when known - MangaDex's own <c>links.al</c> - else by the linked MangaUpdates title) -> a
/// ratio-only map (Source AniListRatio).</item>
/// </list>
/// An unchanged answer rewrites nothing (<c>ContentHash</c>); a changed one bumps <c>Version</c>.
/// </summary>
public sealed class VolumeMapService
{
    public const int MaxCoverPages = 10;

    /// <summary>A MangaDex list with fewer exact volumes than this counts as "no volume list" (the AniList fallback).</summary>
    public const int MinExactVolumes = 2;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly IMangaDexProvider _mangaDex;
    private readonly MissingConversionService _conversion;
    private readonly CompanionLinkService _companions;
    private readonly TimeProvider _time;
    private readonly Reach.ReachCheckService? _reach;
    private readonly ILogger<VolumeMapService> _logger;

    public VolumeMapService(
        MangaPixerDbContext db, MetadataGateway gateway, IMangaDexProvider mangaDex, MissingConversionService conversion,
        CompanionLinkService companions, TimeProvider time, ILogger<VolumeMapService> logger, Reach.ReachCheckService? reach = null)
    {
        _reach = reach;
        _db = db;
        _gateway = gateway;
        _mangaDex = mangaDex;
        _conversion = conversion;
        _companions = companions;
        _time = time;
        _logger = logger;
    }

    /// <summary>The stored map of a series for a source, or null.</summary>
    public Task<SeriesVolumeMapEntity?> FindAsync(long seriesRecordId, VolumeMapSource source, CancellationToken ct = default) =>
        _db.SeriesVolumeMaps.FirstOrDefaultAsync(m => m.RecordId == seriesRecordId && m.Source == (int)source, ct);

    public static bool IsDue(SeriesVolumeMapEntity? map, DateTimeOffset now) => map is null || map.NextCheckAt is not { } next || next <= now;

    /// <summary>The two cover languages asked for: the preferred one, then the record's original language.</summary>
    public static IReadOnlyList<string> CoverLocales(string preferred, string? original) =>
        new[] { preferred, original }.Where(MangaDexProvider.IsValidLocale).Select(l => l!).Distinct(StringComparer.Ordinal).ToList();

    /// <summary>
    /// Reads the cover list and the volume list of the MangaDex record <paramref name="mangaDexRecord"/> for the series
    /// <paramref name="series"/>, and stores both. <paramref name="allLanguages"/> (an admin's "Show all languages")
    /// sends no language filter. Gateway refusals and failures propagate.
    /// </summary>
    public async Task<SeriesVolumeMapEntity> RefreshListsAsync(
        MetadataRecordEntity series, MetadataRecordEntity mangaDexRecord, long libraryId, string preferredLanguage, MetadataCallContext? call,
        bool allLanguages = false, CancellationToken ct = default)
    {
        var extra = CompanionLinkService.MangaDexExtra.Read(mangaDexRecord.ExtraJson);
        var locales = allLanguages ? null : CoverLocales(preferredLanguage, extra.OriginalLanguage);
        await ListCoversAsync(mangaDexRecord, libraryId, locales, call, ct);
        await UpsertMainCoverAsync(mangaDexRecord, extra, ct);

        var raw = await _gateway.CompanionCallAsync(MetadataProviderAllowlist.MangaDex, "aggregate", libraryId,
            c => _mangaDex.AggregateAsync(mangaDexRecord.ExternalId, c), call, ct);
        var built = VolumeListBuilder.Build(Raw(raw));

        // 1.29.0 RC (owner: "missing" = released in the preferred language): the same list filtered by that language - one
        // more request - tells which chapters are released in it.
        string? releasedLanguage = null, releasedJson = null;
        if (MangaDexProvider.IsValidLocale(preferredLanguage))
        {
            var released = await _gateway.CompanionCallAsync(MetadataProviderAllowlist.MangaDex, "aggregate-released", libraryId,
                c => _mangaDex.AggregateAsync(mangaDexRecord.ExternalId, c, preferredLanguage), call, ct);
            releasedLanguage = preferredLanguage;
            releasedJson = VolumeMapJson.WriteChapters(VolumeListBuilder.ChaptersOf(Raw(released)));
        }

        var coverMax = await _db.VolumeCovers.AsNoTracking()
            .Where(c => c.ProviderRecordId == mangaDexRecord.Id && c.Kind == (int)VolumeCoverKind.Volume && c.Volume != null)
            .MaxAsync(c => c.Volume, ct);
        var foreign = IsForeignNumbering(built.HighestVolume, series.OriginVolumes, coverMax);
        var state = foreign || built.Volumes.Count == 0 ? VolumeMapState.Empty : VolumeMapState.Ok;
        var known = coverMax ?? mangaDexRecord.OriginVolumes ?? series.OriginVolumes;
        return await StoreAsync(series, VolumeMapSource.MangaDexAggregate, state,
            state == VolumeMapState.Ok ? VolumeMapJson.Write(built.Volumes) : null,
            built.Unassigned.Count > 0 ? VolumeMapJson.WriteChapters(built.Unassigned) : null,
            state == VolumeMapState.Ok ? built.ChaptersPerVolume : null, known, ct, releasedLanguage, releasedJson);
    }

    private static IReadOnlyList<VolumeListBuilder.RawVolume> Raw(IReadOnlyList<MangaDexAggregateVolume> volumes) =>
        volumes.Select(v => new VolumeListBuilder.RawVolume(v.Volume, v.Chapters.Select(c => (c.Chapter, c.Count)).ToList())).ToList();

    /// <summary>
    /// The foreign-numbering guard (P2.3): the list's highest volume exceeds both the series' volume total and the
    /// highest <c>/cover</c> volume by more than 1. Both bounds must be known - one alone is not evidence enough.
    /// </summary>
    public static bool IsForeignNumbering(int? listHighest, int? seriesTotal, int? coverHighest) =>
        listHighest is { } h && seriesTotal is { } t && coverHighest is { } c && h > t + 1 && h > c + 1;

    /// <summary>True when a stored MangaDex map gives a usable volume list (at least <see cref="MinExactVolumes"/> volumes).</summary>
    public static bool HasVolumeList(SeriesVolumeMapEntity? map) =>
        map is { State: (int)VolumeMapState.Ok } && VolumeMapJson.Read(map.VolumesJson).Count >= MinExactVolumes;

    /// <summary>
    /// The AniList totals of a series without a MangaDex volume list: by the AniList id MangaDex links (when known),
    /// else by the linked MangaUpdates title. Stores the AniList row (as the Missing report does), the companion state
    /// and a ratio-only map. Gateway refusals and failures propagate.
    /// </summary>
    public async Task<SeriesVolumeMapEntity?> AniListFallbackAsync(
        MetadataRecordEntity series, MetadataRecordEntity? mangaDexRecord, long libraryId, MetadataCallContext? call, CancellationToken ct = default)
    {
        var al = mangaDexRecord is null ? null : AniListIdOf(mangaDexRecord.CrossIdsJson);
        var (outcome, record, byId) = await _conversion.LookupSeriesAsync(series, libraryId, al, call, ct);
        var now = _time.GetUtcNow();
        var found = outcome != MissingConversionOutcome.NoMatch ? record : null;
        await _companions.RecordAniListAsync(series, found, byId, fromMangaDex: al is not null, CompanionSchedule.NextCheck(series, now), ct);
        if (found is null)
            return null;
        var ratio = MissingConversionService.Ratio(found.OriginStatus, found.OriginVolumes, found.LatestChapter is { } c ? (int)c : null);
        return await StoreAsync(series, VolumeMapSource.AniListRatio, ratio is null ? VolumeMapState.Empty : VolumeMapState.Ok,
            null, null, ratio, found.OriginVolumes, ct);
    }

    private static string? AniListIdOf(string? crossIdsJson)
    {
        if (string.IsNullOrWhiteSpace(crossIdsJson))
            return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(crossIdsJson)?.GetValueOrDefault(MetadataProviderAllowlist.AniList);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>Pages through <c>/cover</c> (at most <see cref="MaxCoverPages"/>) and reconciles the stored rows.</summary>
    private async Task ListCoversAsync(MetadataRecordEntity record, long libraryId, IReadOnlyList<string>? locales, MetadataCallContext? call, CancellationToken ct)
    {
        var listed = new List<MangaDexCover>();
        var offset = 0;
        for (var page = 0; page < MaxCoverPages; page++)
        {
            var at = offset;
            var answer = await _gateway.CompanionCallAsync(MetadataProviderAllowlist.MangaDex, "covers", libraryId,
                c => _mangaDex.CoversAsync(record.ExternalId, locales, at, c), call, ct);
            listed.AddRange(answer.Covers.Where(c => c.MangaId == record.ExternalId));
            offset += MangaDexProvider.CoverPageSize;
            if (answer.Covers.Count == 0 || offset >= answer.Total)
                break;
        }

        var now = _time.GetUtcNow();
        var rows = await _db.VolumeCovers.Where(c => c.ProviderRecordId == record.Id && c.Kind == (int)VolumeCoverKind.Volume).ToListAsync(ct);
        var byRemote = rows.ToDictionary(r => r.RemoteId, StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var cover in listed)
        {
            if (!seen.Add(cover.Id))
                continue;
            var (volume, variant) = VolumeKey(cover.Volume);
            if (!byRemote.TryGetValue(cover.Id, out var row))
            {
                row = new VolumeCoverEntity
                {
                    PublicId = await NewCoverPublicIdAsync(ct),
                    ProviderRecordId = record.Id,
                    Kind = (int)VolumeCoverKind.Volume,
                    RemoteId = cover.Id,
                    State = (int)VolumeCoverState.Listed,
                    ListedAt = now,
                };
                _db.VolumeCovers.Add(row);
                byRemote[cover.Id] = row;
            }
            var changed = !string.Equals(row.RemoteFile, cover.FileName, StringComparison.Ordinal)
                || (cover.UpdatedAt is { } u && (row.RemoteUpdatedAt is not { } r || u > r));
            row.Volume = volume;
            row.Variant = variant;
            row.Locale = cover.Locale;
            row.RemoteFile = cover.FileName;
            row.RemoteUpdatedAt = cover.UpdatedAt ?? row.RemoteUpdatedAt;
            if (row.State is (int)VolumeCoverState.Gone or (int)VolumeCoverState.Failed || (changed && row.State == (int)VolumeCoverState.Stored))
                row.State = (int)VolumeCoverState.Listed;
        }
        // A cover of a listed language that is not listed any more is Gone (its stored file stays while something uses it).
        var listedLocales = locales?.ToHashSet(StringComparer.Ordinal);
        foreach (var row in rows.Where(r => !seen.Contains(r.RemoteId) && (listedLocales is null || listedLocales.Contains(r.Locale))))
            row.State = (int)VolumeCoverState.Gone;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>The record's main cover as a Kind Main row (RemoteId <c>main:&lt;coverId&gt;</c>, so it never collides with the same cover listed by volume).</summary>
    private async Task UpsertMainCoverAsync(MetadataRecordEntity record, CompanionLinkService.MangaDexExtra extra, CancellationToken ct)
    {
        if (extra.MainCoverId is null || extra.MainCoverFile is null)
            return;
        var remoteId = "main:" + extra.MainCoverId;
        var row = await _db.VolumeCovers.FirstOrDefaultAsync(c => c.ProviderRecordId == record.Id && c.Kind == (int)VolumeCoverKind.Main, ct);
        if (row is null)
        {
            row = new VolumeCoverEntity
            {
                PublicId = await NewCoverPublicIdAsync(ct),
                ProviderRecordId = record.Id,
                Kind = (int)VolumeCoverKind.Main,
                State = (int)VolumeCoverState.Listed,
                ListedAt = _time.GetUtcNow(),
            };
            _db.VolumeCovers.Add(row);
        }
        if (!string.Equals(row.RemoteId, remoteId, StringComparison.Ordinal) || !string.Equals(row.RemoteFile, extra.MainCoverFile, StringComparison.Ordinal))
        {
            row.RemoteId = remoteId;
            row.RemoteFile = extra.MainCoverFile;
            if (row.State != (int)VolumeCoverState.Listed)
                row.State = (int)VolumeCoverState.Listed;
        }
        row.Locale = extra.OriginalLanguage ?? string.Empty;
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>A MangaDex cover key: <c>"3"</c> -> (3, 0); <c>"3.1"</c> -> (3, 1) - an edition alternate; anything else -> (null, 0).</summary>
    public static (int? Volume, int Variant) VolumeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return (null, 0);
        var parts = key.Trim().Split('.');
        if (parts.Length > 2 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var volume) || volume > 100_000)
            return (null, 0);
        if (parts.Length == 1)
            return (volume, 0);
        return int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var variant) && variant is > 0 and < 1000
            ? (volume, variant)
            : (null, 0);
    }

    /// <summary>Stores a map for a source (shared with the Wikipedia companion, 1.32.0): an unchanged answer rewrites nothing.</summary>
    internal async Task<SeriesVolumeMapEntity> StoreAsync(
        MetadataRecordEntity series, VolumeMapSource source, VolumeMapState state, string? volumesJson, string? unassignedJson,
        double? chaptersPerVolume, int? knownVolumeCount, CancellationToken ct, string? releasedLanguage = null, string? releasedChaptersJson = null)
    {
        var now = _time.GetUtcNow();
        var parts = new List<string>
        {
            ((int)state).ToString(CultureInfo.InvariantCulture), volumesJson ?? string.Empty, unassignedJson ?? string.Empty,
            chaptersPerVolume?.ToString("R", CultureInfo.InvariantCulture) ?? string.Empty,
            knownVolumeCount?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
        };
        // The released-in-the-preferred-language list is part of the content (a newly released chapter bumps the version).
        if (releasedLanguage is not null)
            parts.AddRange([releasedLanguage, releasedChaptersJson ?? string.Empty]);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts)))).ToLowerInvariant();
        var map = await FindAsync(series.Id, source, ct);
        if (map is null)
        {
            map = new SeriesVolumeMapEntity { RecordId = series.Id, Source = (int)source };
            _db.SeriesVolumeMaps.Add(map);
        }
        var changed = !string.Equals(map.ContentHash, hash, StringComparison.Ordinal);
        if (changed)
        {
            map.State = (int)state;
            map.VolumesJson = volumesJson;
            map.UnassignedJson = unassignedJson;
            map.ChaptersPerVolume = chaptersPerVolume;
            map.KnownVolumeCount = knownVolumeCount;
            map.ReleasedLanguage = releasedLanguage;
            map.ReleasedChaptersJson = releasedChaptersJson;
            map.ContentHash = hash;
            map.Version++;
            _logger.LogInformation(LogEvents.Metadata.VolumeListStored, "Volume list of record {RecordId} stored ({Source}, {State}, {Volumes} volumes, version {Version})",
                series.Id, source, state, VolumeMapJson.Read(volumesJson).Count, map.Version);
        }
        map.FetchedAt = now;
        map.NextCheckAt = CompanionSchedule.NextCheck(series, now);
        await _db.SaveChangesAsync(ct);
        // 1.30.0 (reach): a new or changed volume list may show that an automatic link contradicts what its folder holds.
        if (changed && _reach is not null)
            await _reach.TryCheckRecordAsync(series.Id, ct);
        return map;
    }

    private async Task<string> NewCoverPublicIdAsync(CancellationToken ct)
    {
        while (true)
        {
            var id = "vc" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (!await _db.VolumeCovers.AnyAsync(c => c.PublicId == id, ct) && !_db.VolumeCovers.Local.Any(c => c.PublicId == id))
                return id;
        }
    }
}
