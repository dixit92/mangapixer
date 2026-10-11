namespace com.lifepixer.mangapixer.Server.Features.Reading.Suggestions;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Reading-mode suggestions (1.41.0, admin): folders whose LOCAL evidence points to another reader mode than the one they resolve to
/// today, and Apply, which writes the existing folder reader defaults (<c>folder_reader_defaults</c>) - never automatically.
/// <para>
/// Units: a live folder that directly holds live archives (a work folder); a work folder below a linked SERIES folder (the nearest link
/// row above it is Confirmed / Auto, <see cref="SeriesLinkStates.IsSeries"/>) counts with that series folder - one row per series with
/// volume or chapter subfolders - unless an admin set a folder default between the two. Evidence per unit (no provider request, nothing
/// stored): the tall-strip sample of each work folder (<see cref="TallStripRule"/>, one aggregate query over all folders), ComicInfo
/// <c>Manga</c> counts, and the linked record's origin / webtoon flag (a hint). <see cref="ReadingModeSuggestionRules"/> decides.
/// </para>
/// <para>
/// A unit is suggested only when the suggestion differs from what it resolves to without users' own overrides (nearest folder default,
/// then the library default). A unit with a folder default on itself or above was decided by an admin: never suggested; listed under
/// "set by an admin" when its evidence disagrees. Right to left is not suggested over Double page (a layout, not a direction).
/// </para>
/// </summary>
public sealed class ReadingModeSuggestionService
{
    /// <summary>The most rows a list returns (per list).</summary>
    public const int MaxRows = 500;

    /// <summary>The most items one apply takes.</summary>
    public const int MaxApplyItems = 500;

    private const int MaxWalkDepth = 64;

    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly ILogger<ReadingModeSuggestionService> _logger;

    public ReadingModeSuggestionService(MangaPixerDbContext db, AuditService audit, ILogger<ReadingModeSuggestionService> logger)
    {
        _db = db;
        _audit = audit;
        _logger = logger;
    }

    private sealed record FolderRow(long Id, long? ParentId, long LibraryId);

    private sealed record PageSampleRow(long FolderId, int Measured, int Tall);

    private sealed record ArchivePagesRow(long ItemId, long FolderId, int Measured, int Tall);

    private sealed record PartialTallRow(long ItemId, int Tall);

    private sealed record LinkRow(long NodeId, int State, int? Origin, bool? Webtoon);

    private sealed class Unit(long id)
    {
        public long Id { get; } = id;
        public int Measured { get; set; }
        public int Tall { get; set; }
        public int Rtl { get; set; }
        public int Ltr { get; set; }
        public int Stated { get; set; }
        public int Subfolders { get; set; }
        public LinkRow? Record { get; set; }
    }

    public async Task<ReadingModeSuggestionsDto> ListAsync(CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folderKind = (int)CatalogNodeKind.Folder;
        var archiveKind = (int)CatalogNodeKind.Archive;

        var folders = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Kind == folderKind && n.Availability != tombstoned)
            .Select(n => new FolderRow(n.Id, n.ParentId, n.LibraryId))
            .ToDictionaryAsync(f => f.Id, ct);

        // Work folders: folders that directly hold live archives.
        var workFolders = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Kind == archiveKind && n.Availability != tombstoned && n.ParentId != null)
            .Select(n => n.ParentId!.Value)
            .Distinct()
            .ToListAsync(ct);

        var pages = (await PageSamplesAsync(ct)).ToDictionary(r => r.FolderId);

        var comicInfo = (await (
                from e in _db.EmbeddedMetadata.AsNoTracking()
                where e.State == 1 && e.MangaDirection != null
                join n in _db.CatalogNodes.AsNoTracking() on e.NodeId equals n.Id
                where n.Kind == archiveKind && n.Availability != tombstoned && n.ParentId != null
                group e.MangaDirection by n.ParentId!.Value into g
                select new
                {
                    FolderId = g.Key,
                    Rtl = g.Count(m => m == 2),
                    Ltr = g.Count(m => m == 0),
                    Stated = g.Count(),
                })
            .ToListAsync(ct))
            .ToDictionary(r => r.FolderId);

        var links = await (
                from l in _db.NodeSeriesLinks.AsNoTracking()
                join r in _db.MetadataRecords.AsNoTracking() on l.RecordId equals r.Id into rs
                from r in rs.DefaultIfEmpty()
                select new LinkRow(l.NodeId, l.State, r != null ? r.Origin : null, r != null ? r.Webtoon : null))
            .ToDictionaryAsync(l => l.NodeId, ct);

        var defaults = await _db.FolderReaderDefaults.AsNoTracking()
            .ToDictionaryAsync(f => f.NodeId, f => f.ReaderMode, ct);
        var libraries = await _db.Libraries.AsNoTracking()
            .ToDictionaryAsync(l => l.Id, l => new { l.PublicId, l.DisplayName, l.DefaultReaderMode }, ct);

        var libraryDefaults = libraries.ToDictionary(l => l.Key, l => l.Value.DefaultReaderMode);

        // Group work folders into units.
        var units = new Dictionary<long, Unit>();
        foreach (var folderId in workFolders)
        {
            if (!folders.ContainsKey(folderId))
                continue;
            var (unitId, record) = UnitOf(folderId, folders, links, defaults);
            if (!units.TryGetValue(unitId, out var unit))
                units[unitId] = unit = new Unit(unitId) { Record = record };
            if (unitId != folderId)
                unit.Subfolders++;
            if (pages.TryGetValue(folderId, out var p))
            {
                unit.Measured += p.Measured;
                unit.Tall += p.Tall;
            }
            if (comicInfo.TryGetValue(folderId, out var c))
            {
                unit.Rtl += c.Rtl;
                unit.Ltr += c.Ltr;
                unit.Stated += c.Stated;
            }
        }

        var suggested = new List<(Unit Unit, ReadingModeVerdict Verdict, ReaderMode? Current, ReaderModeSource Source)>();
        var adminSet = new List<(Unit Unit, ReadingModeVerdict Verdict, ReaderMode? Current, ReaderModeSource Source)>();
        foreach (var unit in units.Values)
        {
            var verdict = ReadingModeSuggestionRules.Suggest(new ReadingModeEvidence(
                unit.Measured, unit.Tall, unit.Rtl, unit.Ltr, unit.Stated,
                unit.Record?.Origin is { } o ? (MetadataOrigin)o : null, unit.Record?.Webtoon));
            if (verdict is null)
                continue;
            var (current, source) = CurrentOf(unit.Id, folders, defaults, libraryDefaults);
            if (current == verdict.Mode)
                continue;
            if (verdict.Mode == ReaderMode.PagedRtl && current == ReaderMode.DoubleSpread)
                continue;
            if (source is ReaderModeSource.ThisFolder or ReaderModeSource.FolderAbove)
                adminSet.Add((unit, verdict, current, source));
            else
                suggested.Add((unit, verdict, current, source));
        }

        // Names for the rows shown (and their parents).
        var shown = suggested.Concat(adminSet).Select(s => s.Unit.Id).ToList();
        var parentIds = shown.Select(id => folders[id].ParentId).OfType<long>().Distinct().ToList();
        var nameIds = shown.Concat(parentIds).Distinct().ToList();
        var names = new Dictionary<long, (string PublicId, string Name)>();
        foreach (var chunk in nameIds.Chunk(500))
        {
            var rows = await _db.CatalogNodes.AsNoTracking().Where(n => chunk.Contains(n.Id))
                .Select(n => new { n.Id, n.PublicId, n.DisplayName }).ToListAsync(ct);
            foreach (var r in rows)
                names[r.Id] = (r.PublicId, r.DisplayName);
        }

        ReadingModeSuggestionDto ToDto((Unit Unit, ReadingModeVerdict Verdict, ReaderMode? Current, ReaderModeSource Source) s)
        {
            var folder = folders[s.Unit.Id];
            var library = libraries[folder.LibraryId];
            return new ReadingModeSuggestionDto
            {
                NodeId = names[s.Unit.Id].PublicId,
                LibraryId = library.PublicId,
                LibraryName = library.DisplayName,
                FolderName = names[s.Unit.Id].Name,
                ParentName = folder.ParentId is { } pid && names.TryGetValue(pid, out var parent) ? parent.Name : null,
                SuggestedMode = s.Verdict.Mode,
                CurrentMode = s.Current,
                CurrentSource = s.Source,
                Confidence = s.Verdict.Confidence,
                Signal = s.Verdict.Signal,
                MeasuredPages = s.Unit.Measured,
                TallPages = s.Unit.Tall,
                ComicInfoRightToLeft = s.Unit.Rtl,
                ComicInfoStated = s.Unit.Stated,
                LinkedOrigin = s.Unit.Record?.Origin is { } o ? (MetadataOrigin)o : null,
                LinkedWebtoon = s.Unit.Record?.Webtoon,
                Subfolders = s.Unit.Subfolders,
            };
        }

        static IEnumerable<ReadingModeSuggestionDto> Ordered(IEnumerable<ReadingModeSuggestionDto> rows) => rows
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.LibraryName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.FolderName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.NodeId, StringComparer.Ordinal);

        var items = Ordered(suggested.Where(s => names.ContainsKey(s.Unit.Id)).Select(ToDto)).ToList();
        var setByAdmin = Ordered(adminSet.Where(s => names.ContainsKey(s.Unit.Id)).Select(ToDto)).Take(MaxRows).ToList();
        watch.Stop();
        _logger.LogInformation(LogEvents.Administration.ReadingModeSuggestionsListed,
            "Reading-mode suggestions: {Units} folders checked, {Suggestions} suggested, {AdminSet} set by an admin, {ElapsedMs} ms",
            units.Count, items.Count, adminSet.Count, watch.ElapsedMilliseconds);
        return new ReadingModeSuggestionsDto
        {
            Items = items.Take(MaxRows).ToList(),
            SetByAdmin = setByAdmin,
            FoldersChecked = units.Count,
            Total = items.Count,
            Limit = MaxRows,
        };
    }

    /// <summary>
    /// The unit a work folder counts in, and the linked record that speaks for it: the walk (self, then ancestors) stops at the first
    /// link row that stops inheritance; a SERIES row above the folder makes that folder the unit - unless an admin's folder default sits
    /// on the way (on the work folder or between it and the series folder), which keeps the work folder on its own.
    /// </summary>
    private static (long UnitId, LinkRow? Record) UnitOf(
        long folderId, Dictionary<long, FolderRow> folders, Dictionary<long, LinkRow> links, Dictionary<long, int> defaults)
    {
        long? id = folderId;
        var defaultOnTheWay = false;
        for (var depth = 0; id is { } current && depth < MaxWalkDepth && folders.TryGetValue(current, out var row); depth++)
        {
            if (links.TryGetValue(current, out var link) && SeriesLinkStates.StopsInheritance((SeriesLinkState)link.State))
            {
                if (!SeriesLinkStates.IsSeries((SeriesLinkState)link.State))
                    return (folderId, null);
                return current == folderId || !defaultOnTheWay ? (current, link) : (folderId, link);
            }
            if (defaults.ContainsKey(current))
                defaultOnTheWay = true;
            id = row.ParentId;
        }
        return (folderId, null);
    }

    /// <summary>What a unit resolves to without users' overrides: nearest folder default (self, then above), then the library default.</summary>
    private static (ReaderMode? Mode, ReaderModeSource Source) CurrentOf(
        long unitId, Dictionary<long, FolderRow> folders, Dictionary<long, int> defaults, Dictionary<long, int?> libraryDefaults)
    {
        long? id = unitId;
        for (var depth = 0; id is { } current && depth < MaxWalkDepth; depth++)
        {
            if (defaults.TryGetValue(current, out var mode))
                return ((ReaderMode)mode, current == unitId ? ReaderModeSource.ThisFolder : ReaderModeSource.FolderAbove);
            id = folders.TryGetValue(current, out var row) ? row.ParentId : null;
        }
        return libraryDefaults.TryGetValue(folders[unitId].LibraryId, out var lib) && lib is { } libMode
            ? ((ReaderMode)libMode, ReaderModeSource.Library)
            : (null, ReaderModeSource.None);
    }

    /// <summary>
    /// The tall-strip sample of every folder's direct live archives: the first <see cref="TallStripRule.SamplePages"/> measured pages
    /// per folder in archive-id, then page order - exactly the sample <see cref="TallStripSampler"/> takes - counted and tall-counted.
    /// Two queries for the whole catalog (no per-folder round trip): a per-archive aggregate, then, for the one archive per folder that
    /// the cap cuts, the tall count of its first pages. (A single window query over every page took about 3.5 s on 1.76 M synthetic
    /// pages; this takes about 1 s.)
    /// </summary>
    private async Task<List<PageSampleRow>> PageSamplesAsync(CancellationToken ct)
    {
        // Constants only (no request input) are formatted into the SQL.
        var ratio = TallStripRule.Ratio.ToString("0.0###", CultureInfo.InvariantCulture);
        var tombstoned = ((int)CatalogNodeAvailability.Tombstoned).ToString(CultureInfo.InvariantCulture);
        var archive = ((int)CatalogNodeKind.Archive).ToString(CultureInfo.InvariantCulture);
#pragma warning disable EF1002
        var perArchive = await _db.Database.SqlQueryRaw<ArchivePagesRow>($"""
            SELECT p."ItemId" AS "ItemId", n."ParentId" AS "FolderId", COUNT(*) AS "Measured",
                   SUM(CASE WHEN p."Height" >= p."Width" * {ratio} THEN 1 ELSE 0 END) AS "Tall"
            FROM page_entries p JOIN catalog_nodes n ON n."Id" = p."ItemId"
            WHERE n."Kind" = {archive} AND n."Availability" != {tombstoned} AND n."ParentId" IS NOT NULL
              AND p."Width" IS NOT NULL AND p."Height" IS NOT NULL AND p."Width" > 0
            GROUP BY p."ItemId"
            """).ToListAsync(ct);
#pragma warning restore EF1002

        var samples = new List<PageSampleRow>();
        var partial = new Dictionary<long, (long FolderId, int Need)>();
        foreach (var folder in perArchive.GroupBy(a => a.FolderId))
        {
            int measured = 0, tall = 0;
            foreach (var a in folder.OrderBy(a => a.ItemId))
            {
                var room = TallStripRule.SamplePages - measured;
                if (room <= 0)
                    break;
                if (a.Measured <= room)
                {
                    measured += a.Measured;
                    tall += a.Tall;
                }
                else
                {
                    measured += room;
                    partial[a.ItemId] = (folder.Key, room);
                }
            }
            samples.Add(new PageSampleRow(folder.Key, measured, tall));
        }
        if (partial.Count == 0)
            return samples;

        var extraTall = new Dictionary<long, int>();
        foreach (var chunk in partial.Chunk(500))
        {
            var values = string.Join(',', chunk.Select(p =>
                string.Create(CultureInfo.InvariantCulture, $"({p.Key}, {p.Value.Need})")));
#pragma warning disable EF1002
            var rows = await _db.Database.SqlQueryRaw<PartialTallRow>($"""
                WITH need(item, k) AS (VALUES {values})
                SELECT s."ItemId" AS "ItemId", SUM(CASE WHEN s."Height" >= s."Width" * {ratio} THEN 1 ELSE 0 END) AS "Tall"
                FROM (
                    SELECT p."ItemId" AS "ItemId", p."Width" AS "Width", p."Height" AS "Height",
                           ROW_NUMBER() OVER (PARTITION BY p."ItemId" ORDER BY p."Ordinal") AS rn
                    FROM page_entries p
                    WHERE p."ItemId" IN (SELECT item FROM need)
                      AND p."Width" IS NOT NULL AND p."Height" IS NOT NULL AND p."Width" > 0
                ) s JOIN need ON need.item = s."ItemId"
                WHERE s.rn <= need.k
                GROUP BY s."ItemId"
                """).ToListAsync(ct);
#pragma warning restore EF1002
            foreach (var r in rows)
                extraTall[partial[r.ItemId].FolderId] = extraTall.GetValueOrDefault(partial[r.ItemId].FolderId) + r.Tall;
        }
        return samples
            .Select(s => extraTall.TryGetValue(s.FolderId, out var t) ? s with { Tall = s.Tall + t } : s)
            .ToList();
    }

    /// <summary>
    /// Writes the listed folders' reader defaults (the folder reader-default PUT's effect). Returns an error code for a request that is
    /// refused as a whole (<c>no_items</c>, <c>too_many_items</c>, <c>duplicate_items</c>, <c>invalid_mode</c>); otherwise every item is
    /// re-validated - unknown, removed or non-folder nodes are skipped with a reason.
    /// </summary>
    public async Task<(string? Error, ReadingModeApplyResultDto? Result)> ApplyAsync(
        ReadingModeApplyRequest request, string? actorUserName, CancellationToken ct)
    {
        var items = request?.Items;
        string? error = null;
        if (items is null || items.Count == 0)
            error = "no_items";
        else if (items.Count > MaxApplyItems)
            error = "too_many_items";
        else if (items.Any(i => i is null || string.IsNullOrWhiteSpace(i.NodeId)))
            error = "invalid_node";
        else if (items.Select(i => i.NodeId).Distinct(StringComparer.Ordinal).Count() != items.Count)
            error = "duplicate_items";
        else if (items.Any(i => !Enum.IsDefined(i.ReaderMode)))
            error = "invalid_mode";
        if (error is not null)
        {
            _logger.LogInformation(LogEvents.Administration.ReadingModeSuggestionsRefused,
                "Reading-mode apply refused: {Reason}, {Count} items", error, items?.Count ?? 0);
            return (error, null);
        }

        var ids = items!.Select(i => i.NodeId).ToList();
        var nodes = await _db.CatalogNodes
            .Where(n => ids.Contains(n.PublicId))
            .Select(n => new { n.Id, n.PublicId, n.Kind, n.Availability, n.LibraryId })
            .ToDictionaryAsync(n => n.PublicId, StringComparer.Ordinal, ct);
        var nodeIds = nodes.Values.Select(n => n.Id).ToList();
        var existing = await _db.FolderReaderDefaults.Where(f => nodeIds.Contains(f.NodeId)).ToDictionaryAsync(f => f.NodeId, ct);

        var skipped = new List<ReadingModeApplySkipDto>();
        var applied = new List<(long NodeId, long LibraryId)>();
        foreach (var item in items!)
        {
            if (!nodes.TryGetValue(item.NodeId, out var node) || node.Availability == (int)CatalogNodeAvailability.Tombstoned)
            {
                skipped.Add(new ReadingModeApplySkipDto { NodeId = item.NodeId, Reason = "not_found" });
                continue;
            }
            if (node.Kind != (int)CatalogNodeKind.Folder)
            {
                skipped.Add(new ReadingModeApplySkipDto { NodeId = item.NodeId, Reason = "not_a_folder" });
                continue;
            }
            if (existing.TryGetValue(node.Id, out var row))
                row.ReaderMode = (int)item.ReaderMode;
            else
                _db.FolderReaderDefaults.Add(new FolderReaderDefaultEntity { NodeId = node.Id, ReaderMode = (int)item.ReaderMode });
            applied.Add((node.Id, node.LibraryId));
        }
        if (applied.Count > 0)
        {
            await _db.SaveChangesAsync(ct);
            var libraryIds = applied.Select(a => a.LibraryId).Distinct().ToList();
            await _audit.RecordAsync(AuditActions.ReadingModeSuggestionsApply,
                "applied_" + applied.Count.ToString(CultureInfo.InvariantCulture), actorUserName, ct: ct,
                targetLibraryId: libraryIds.Count == 1 ? libraryIds[0] : null,
                targetItemId: applied.Count == 1 ? applied[0].NodeId : null);
        }
        _logger.LogInformation(LogEvents.Administration.ReadingModeSuggestionsApplied,
            "Reading-mode suggestions applied: {Applied} folders, {Skipped} skipped", applied.Count, skipped.Count);
        return (null, new ReadingModeApplyResultDto { Applied = applied.Count, Skipped = skipped });
    }
}
