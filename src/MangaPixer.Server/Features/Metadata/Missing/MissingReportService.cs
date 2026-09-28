namespace com.lifepixer.mangapixer.Server.Features.Metadata.Missing;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The missing volumes / chapters report (1.28.0), admin-only. For every FOLDER with its own confirmed or
/// automatic link to a stored record, it compares the unit numbers the archive names state (the folder itself
/// plus its Volumes / Chapters subfolders; <see cref="MissingUnits"/>) with the totals of the stored record: the
/// English publishers' totals first, then the status in the country of origin, then the latest chapter. Reads
/// stored rows only - it never contacts a provider. Logs counts and timings, never names or titles.
/// </summary>
public sealed class MissingReportService
{
    public const int MaxLimit = 200;

    private readonly MangaPixerDbContext _db;
    private readonly ILogger<MissingReportService> _logger;

    public MissingReportService(MangaPixerDbContext db, ILogger<MissingReportService> logger)
    {
        _db = db;
        _logger = logger;
    }

    private sealed record LinkedRow(
        long NodeId, string PublicId, string DisplayName, long LibraryId, int State,
        string Provider, string Title, int? OriginVolumes, double? LatestChapter, string? StatusText, string? PublishersJson,
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
        var results = await EvaluateAsync(rows, ct);
        var ordered = rows
            .Select(r => (Row: r, Result: results[r.NodeId]))
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
            NoVerdict = ordered.Count(x => x.Result.Verdict is MissingVerdict.Mixed or MissingVerdict.NoUnits),
        };
        var filtered = onlyMissing
            ? ordered.Where(x => x.Result.Verdict is MissingVerdict.Behind or MissingVerdict.Holes).ToList()
            : ordered;
        var page = filtered.Skip(offset).Take(limit).ToList();
        var items = await ToDtosAsync(page, ct);
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

    /// <summary>The report row of one folder with its own link, or null (no such node, not a linked folder).</summary>
    public async Task<MissingSeriesDto?> ForNodeAsync(string nodePublicId, CancellationToken ct = default)
    {
        var nodeId = await _db.CatalogNodes.Where(n => n.PublicId == nodePublicId).Select(n => (long?)n.Id).FirstOrDefaultAsync(ct);
        if (nodeId is null)
            return null;
        var rows = await LinkedFoldersAsync(null, nodeId, ct);
        if (rows.Count == 0)
            return null;
        var results = await EvaluateAsync(rows, ct);
        return (await ToDtosAsync([(rows[0], results[rows[0].NodeId])], ct))[0];
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
            select new LinkedRow(n.Id, n.PublicId, n.DisplayName, n.LibraryId, l.State,
                r.Provider, r.Title, r.OriginVolumes, r.LatestChapter, r.StatusText, r.PublishersJson, r.FetchedAt);
        return await query.ToListAsync(ct);
    }

    /// <summary>Evaluates every row with two batched queries (children, then unit-subfolder children).</summary>
    private async Task<Dictionary<long, MissingUnitsResult>> EvaluateAsync(IReadOnlyList<LinkedRow> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.NodeId).ToList();
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var archive = (int)CatalogNodeKind.Archive;
        var children = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId != null && ids.Contains(n.ParentId.Value) && n.Availability != tombstoned)
            .Select(n => new { n.Id, ParentId = n.ParentId!.Value, n.Kind, n.DisplayName })
            .ToListAsync(ct);

        // Volumes / Chapters subfolders belong to the series unless they carry a link of their own.
        var unitFolders = children
            .Where(c => c.Kind != archive && (AutoMatchText.IsVolumeFolderName(c.DisplayName) || AutoMatchText.IsChapterFolderName(c.DisplayName)))
            .Select(c => c.Id)
            .ToList();
        var ownLinked = (await _db.NodeSeriesLinks.AsNoTracking().Where(l => unitFolders.Contains(l.NodeId)).Select(l => l.NodeId).ToListAsync(ct))
            .ToHashSet();
        var unitFolderParent = children.Where(c => unitFolders.Contains(c.Id) && !ownLinked.Contains(c.Id)).ToDictionary(c => c.Id, c => c.ParentId);
        var unitIds = unitFolderParent.Keys.ToList();
        var grandchildren = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId != null && unitIds.Contains(n.ParentId.Value) && n.Kind == archive && n.Availability != tombstoned)
            .Select(n => new { ParentId = n.ParentId!.Value, n.DisplayName })
            .ToListAsync(ct);

        var direct = children.Where(c => c.Kind == archive).ToLookup(c => c.ParentId, c => c.DisplayName);
        var inUnit = grandchildren.ToLookup(c => c.ParentId, c => c.DisplayName);
        var unitsOf = unitFolderParent.ToLookup(kv => kv.Value, kv => kv.Key);

        var result = new Dictionary<long, MissingUnitsResult>();
        foreach (var row in rows)
        {
            var folders = new List<IReadOnlyList<string>> { direct[row.NodeId].ToList() };
            foreach (var unit in unitsOf[row.NodeId])
                folders.Add(inUnit[unit].ToList());
            result[row.NodeId] = MissingUnits.Evaluate(folders, TotalsOf(row));
        }
        return result;
    }

    private static PublishedTotals TotalsOf(LinkedRow row)
    {
        var english = MetadataJson.ReadList<MetadataJson.Publisher>(row.PublishersJson)
            .Where(p => string.Equals(p.Kind, "english", StringComparison.Ordinal))
            .ToList();
        return new PublishedTotals(
            EnglishVolumes: english.Max(p => p.Volumes),
            EnglishChapters: english.Max(p => p.Chapters),
            OriginVolumes: row.OriginVolumes,
            OriginChapters: MangaUpdatesStatusParser.Parse(row.StatusText).Chapters,
            LatestChapter: row.LatestChapter);
    }

    private async Task<List<MissingSeriesDto>> ToDtosAsync(IReadOnlyList<(LinkedRow Row, MissingUnitsResult Result)> page, CancellationToken ct)
    {
        if (page.Count == 0)
            return [];
        var libraryIds = page.Select(p => p.Row.LibraryId).Distinct().ToList();
        var libraries = await _db.Libraries.AsNoTracking().Where(l => libraryIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PublicId, l.DisplayName), ct);
        var covers = await Catalog.FolderCovers.ResolveAsync(_db, page.Select(p => p.Row.NodeId).ToList(), ct);
        return page.Select(p =>
        {
            var library = libraries.GetValueOrDefault(p.Row.LibraryId);
            var english = MetadataJson.ReadList<MetadataJson.Publisher>(p.Row.PublishersJson)
                .Where(x => string.Equals(x.Kind, "english", StringComparison.Ordinal)).ToList();
            return new MissingSeriesDto
            {
                NodeId = p.Row.PublicId,
                DisplayName = p.Row.DisplayName,
                LibraryId = library.PublicId ?? string.Empty,
                LibraryName = library.DisplayName ?? string.Empty,
                CoverUrl = covers.TryGetValue(p.Row.NodeId, out var cover) ? Catalog.FolderCovers.ArchiveCoverUrl(cover) : null,
                Provider = p.Row.Provider,
                RecordTitle = p.Row.Title,
                LinkState = (SeriesLinkState)p.Row.State,
                Verdict = p.Result.Verdict,
                Volumes = ToDto(p.Result.Volumes),
                Chapters = ToDto(p.Result.Chapters),
                MixedFolders = p.Result.MixedFolders,
                EnglishTotalUnknown = english.Count > 0 && english.All(x => x.Volumes is null && x.Chapters is null),
                StatusText = p.Row.StatusText,
                FetchedAt = p.Row.FetchedAt,
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
    };
}
