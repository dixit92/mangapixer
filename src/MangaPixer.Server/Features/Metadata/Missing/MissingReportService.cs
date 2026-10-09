namespace com.lifepixer.mangapixer.Server.Features.Metadata.Missing;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The missing volumes / chapters report (1.28.0), admin-only. For every FOLDER with its own confirmed or
/// automatic link to a stored record, it compares the unit numbers the archive names state (the folder itself
/// plus its unit subfolders - Volumes, Chapters, Season / Part N - since 1.29.0; <see cref="MissingUnits"/>) with the totals of the stored record: the
/// English publishers' totals first, then the status in the country of origin, then the latest chapter. Reads
/// stored rows only - it never contacts a provider. Logs counts and timings, never names or titles.
/// </summary>
public sealed class MissingReportService
{
    public const int MaxLimit = 200;

    private readonly MangaPixerDbContext _db;
    private readonly ILogger<MissingReportService> _logger;
    private readonly ICoverResolver _covers;

    public MissingReportService(MangaPixerDbContext db, ILogger<MissingReportService> logger, ICoverResolver? covers = null)
    {
        _db = db;
        _logger = logger;
        _covers = covers ?? new FileCoverResolver(db);
    }

    private sealed record LinkedRow(
        long NodeId, string PublicId, string DisplayName, long LibraryId, int State, long RecordId,
        string Provider, string ExternalId, string Title, int? OriginVolumes, double? LatestChapter, string? StatusText, string? PublishersJson,
        DateTimeOffset FetchedAt);

    /// <summary>
    /// A page of the report. <paramref name="onlyMissing"/> keeps the series that are behind or have holes.
    /// Error <c>library_not_found</c> for an unknown library filter.
    /// </summary>
    public async Task<(string? Error, MissingReportPageDto? Page)> ListAsync(
        string? libraryPublicId, bool onlyMissing, string? cursor, int limit, CancellationToken ct = default)
    {
        long? libraryId = null;
        if (!string.IsNullOrEmpty(libraryPublicId))
        {
            libraryId = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            if (libraryId is null)
                return ("library_not_found", null);
        }
        limit = Math.Clamp(limit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;

        var sw = Stopwatch.StartNew();
        var rows = await LinkedFoldersAsync(libraryId, null, ct);
        var conversions = await ConversionsAsync(rows, ct);
        var results = await EvaluateAsync(rows, conversions, ct);
        // 1.39.0: a folder whose tracking an admin turned off gives no answer - left out of the list and its counts (counted apart).
        var notTracked = results.Values.Count(r => r.Progress.Result.Facts.TrackingOff);
        var ordered = rows
            .Select(r => (Row: r, Result: results[r.NodeId].Result, Progress: results[r.NodeId].Progress))
            .Where(x => !x.Progress.Result.Facts.TrackingOff)
            .OrderBy(x => x.Result.Verdict)
            .ThenByDescending(x => Math.Max(x.Result.Volumes?.BehindBy ?? 0, x.Result.Chapters?.BehindBy ?? 0))
            .ThenBy(x => x.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.NodeId)
            .ToList();
        var summary = new MissingReportSummaryDto
        {
            Series = ordered.Count,
            Behind = ordered.Count(x => x.Result.Verdict == MissingVerdict.Behind),
            Holes = ordered.Count(x => x.Result.Verdict == MissingVerdict.Holes),
            UpToDate = ordered.Count(x => x.Result.Verdict == MissingVerdict.UpToDate),
            NoTotal = ordered.Count(x => x.Result.Verdict == MissingVerdict.NoTotal),
            NoVerdict = ordered.Count(x => x.Result.Verdict is MissingVerdict.Mixed or MissingVerdict.NoUnits or MissingVerdict.Restarts),
            Upgrades = ordered.Count(x => x.Progress.Result.UpgradeVolumes.Count > 0),
            NotTracked = notTracked,
        };
        var filtered = onlyMissing
            ? ordered.Where(x => x.Result.Verdict is MissingVerdict.Behind or MissingVerdict.Holes).ToList()
            : ordered;
        var page = filtered.Skip(offset).Take(limit).ToList();
        var items = await ToDtosAsync(page, conversions, ct);
        var next = offset + page.Count;
        _logger.LogDebug("Missing report: {Series} linked series, {Behind} behind, {Holes} with holes, page {Count} in {ElapsedMs} ms",
            summary.Series, summary.Behind, summary.Holes, items.Count, sw.ElapsedMilliseconds);
        return (null, new MissingReportPageDto
        {
            Items = items,
            Summary = summary,
            Total = filtered.Count,
            NextCursor = next < filtered.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
        });
    }

    /// <summary>
    /// The report row of one folder with its own link, or null (no such node, not a linked folder). 1.39.0: a folder whose tracking is
    /// off keeps its row (the series line says "Completion not tracked"): no verdict, its progress says <c>trackingOff</c>.
    /// </summary>
    public async Task<MissingSeriesDto?> ForNodeAsync(string nodePublicId, CancellationToken ct = default)
    {
        var nodeId = await _db.CatalogNodes.Where(n => n.PublicId == nodePublicId).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct);
        if (nodeId is null)
            return null;
        var rows = await LinkedFoldersAsync(null, nodeId, ct);
        if (rows.Count == 0)
            return null;
        var conversions = await ConversionsAsync(rows, ct);
        var results = await EvaluateAsync(rows, conversions, ct);
        var one = results[rows[0].NodeId];
        return (await ToDtosAsync([(rows[0], one.Result, one.Progress)], conversions, ct))[0];
    }

    private async Task<List<LinkedRow>> LinkedFoldersAsync(long? libraryId, long? nodeId, CancellationToken ct)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var query =
            from l in _db.NodeSeriesLinks.AsNoTracking()
            where (l.State == confirmed || l.State == auto) && l.RecordId != null
                && (libraryId == null || l.LibraryId == libraryId) && (nodeId == null || l.NodeId == nodeId)
            join n in _db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.Kind == folder && n.Availability != tombstoned
            join r in _db.MetadataRecords.AsNoTracking() on l.RecordId equals r.Id
            select new LinkedRow(n.Id, n.PublicId, n.DisplayName, n.LibraryId, l.State, r.Id,
                r.Provider, r.ExternalId, r.Title, r.OriginVolumes, r.LatestChapter, r.StatusText, r.PublishersJson, r.FetchedAt);
        return await query.ToListAsync(ct);
    }

    /// <summary>
    /// The stored chapters-per-volume sources (1.28.0): AniList rows whose cross reference names the linked
    /// MangaUpdates record, keyed by that record's external id. One query; never a request.
    /// </summary>
    private async Task<Dictionary<string, MissingConversionDto>> ConversionsAsync(IReadOnlyList<LinkedRow> rows, CancellationToken ct)
    {
        var result = new Dictionary<string, MissingConversionDto>(StringComparer.Ordinal);
        if (!rows.Any(r => r.Provider == MetadataProviderAllowlist.MangaUpdates))
            return result;
        var stored = await _db.MetadataRecords.AsNoTracking()
            .Where(r => r.Provider == MetadataProviderAllowlist.AniList && r.CrossIdsJson != null)
            .Select(r => new { r.ExternalId, r.Title, r.SiteUrl, r.OriginVolumes, r.LatestChapter, r.OriginStatus, r.CrossIdsJson, r.FetchedAt })
            .ToListAsync(ct);
        foreach (var r in stored.OrderBy(r => r.FetchedAt))
        {
            if (MissingConversionService.LinkedMangaUpdatesId(r.CrossIdsJson) is not { } muId)
                continue;
            var chapters = r.LatestChapter is { } c ? (int)Math.Floor(c) : (int?)null;
            result[muId] = new MissingConversionDto
            {
                Provider = MetadataProviderAllowlist.AniList,
                ProviderName = "AniList",
                ExternalId = r.ExternalId,
                Title = r.Title,
                SiteUrl = r.SiteUrl,
                Volumes = r.OriginVolumes,
                Chapters = chapters,
                ChaptersPerVolume = MissingConversionService.Ratio(r.OriginStatus, r.OriginVolumes, chapters),
                FetchedAt = r.FetchedAt,
            };
        }
        return result;
    }

    private static MissingConversionDto? ConversionOf(LinkedRow row, IReadOnlyDictionary<string, MissingConversionDto> conversions) =>
        row.Provider == MetadataProviderAllowlist.MangaUpdates ? conversions.GetValueOrDefault(row.ExternalId) : null;

    /// <summary>Unit subfolders are followed this many levels below the linked folder (<c>Season 1/Volumes</c>).</summary>
    public const int MaxUnitDepth = SeriesProgressLoader.MaxUnitDepth;

    /// <summary>
    /// Evaluates every row through the shared progress engine (1.30.0, <see cref="SeriesProgressLoader"/>): the series scope (the
    /// linked folder plus its unit subfolders), volume files and chapter files merged through the stored volume list, compared per
    /// kind with what is released in the preferred language. The same numbers as the Volumes view; an official volume held only as
    /// chapters is an upgrade, never behind. Batched queries; stored rows only.
    /// </summary>
    private async Task<Dictionary<long, (MissingUnitsResult Result, SeriesProgressEntry Progress)>> EvaluateAsync(
        IReadOnlyList<LinkedRow> rows, IReadOnlyDictionary<string, MissingConversionDto> conversions, CancellationToken ct)
    {
        var targets = rows.Select(r => new SeriesProgressTarget(r.NodeId, r.RecordId, ConversionOf(r, conversions)?.ChaptersPerVolume)).ToList();
        var loaded = await new SeriesProgressLoader(_db).LoadAsync(targets, ct);
        return loaded.ToDictionary(
            kv => kv.Key,
            kv => (SeriesProgress.ToMissing(kv.Value.Result, kv.Value.VolumeArchives, kv.Value.ChapterArchives), kv.Value));
    }

    private async Task<List<MissingSeriesDto>> ToDtosAsync(
        IReadOnlyList<(LinkedRow Row, MissingUnitsResult Result, SeriesProgressEntry Progress)> page, IReadOnlyDictionary<string, MissingConversionDto> conversions, CancellationToken ct)
    {
        if (page.Count == 0)
            return [];
        var libraryIds = page.Select(p => p.Row.LibraryId).Distinct().ToList();
        var libraries = await _db.Libraries.AsNoTracking().Where(l => libraryIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PublicId, l.DisplayName), ct);
        var covers = await _covers.ResolveUrlsAsync(page.Select(p => new CoverTarget(p.Row.NodeId, p.Row.PublicId, true)).ToList(), ct);
        var language = await ReleasedInLanguage.PreferredAsync(_db, ct);
        var isEnglish = ReleasedInLanguage.IsEnglish(language);
        return page.Select(p =>
        {
            var library = libraries.GetValueOrDefault(p.Row.LibraryId);
            var duplicates = DuplicateUnits.FindIn(p.Progress.Rows);
            var english = MetadataJson.ReadList<MetadataJson.Publisher>(p.Row.PublishersJson)
                .Where(x => string.Equals(x.Kind, "english", StringComparison.Ordinal)).ToList();
            return new MissingSeriesDto
            {
                NodeId = p.Row.PublicId,
                DisplayName = p.Row.DisplayName,
                LibraryId = library.PublicId ?? string.Empty,
                LibraryName = library.DisplayName ?? string.Empty,
                CoverUrl = covers.GetValueOrDefault(p.Row.NodeId),
                Provider = p.Row.Provider,
                RecordTitle = p.Row.Title,
                LinkState = (SeriesLinkState)p.Row.State,
                Verdict = p.Result.Verdict,
                Volumes = ToDto(p.Result.Volumes),
                Chapters = ToDto(p.Result.Chapters),
                MixedFolders = p.Result.MixedFolders,
                EnglishTotalUnknown = isEnglish && english.Count > 0 && english.All(x => x.Volumes is null && x.Chapters is null),
                Language = language,
                Conversion = ConversionOf(p.Row, conversions),
                StatusText = p.Row.StatusText,
                FetchedAt = p.Row.FetchedAt,
                Progress = p.Progress.Dto,
                Duplicates = duplicates.Take(MissingUnits.MaxListed).Select(DuplicateUnits.ToDto).ToList(),
                DuplicateCount = duplicates.Count,
            };
        }).ToList();
    }

    private static MissingUnitGapDto? ToDto(MissingUnitGap? gap) => gap is null ? null : new MissingUnitGapDto
    {
        Kind = gap.Kind,
        ArchiveCount = gap.ArchiveCount,
        UnitCount = gap.UnitCount,
        Lowest = gap.Lowest,
        Have = gap.Have,
        Available = gap.Available,
        Source = gap.Source,
        Confidence = gap.Confidence,
        BehindBy = gap.BehindBy,
        Missing = gap.Missing,
        MissingCount = gap.MissingCount,
        OriginTotal = gap.OriginTotal,
    };
}
