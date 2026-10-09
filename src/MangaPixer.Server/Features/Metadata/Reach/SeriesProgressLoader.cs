namespace com.lifepixer.mangapixer.Server.Features.Metadata.Reach;

using System.Globalization;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using com.lifepixer.mangapixer.Core.Metadata.Wikipedia;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>One linked series folder to evaluate: the folder with its own link and the linked record.</summary>
public sealed record SeriesProgressTarget(long NodeId, long RecordId, double? ChaptersPerVolume = null);

/// <summary>
/// One series' progress with the archive rows it was computed from (the linked folder and its unit subfolders) and the number
/// of volume / chapter archives (the Missing report's counts).
/// </summary>
public sealed record SeriesProgressEntry(
    ProgressResult Result, IReadOnlyList<GroupingRow> Rows, int VolumeArchives, int ChapterArchives, ListCreditDto? ListCredit = null)
{
    public SeriesProgressDto Dto => SeriesProgress.ToDto(Result) with { ListCredit = ListCredit };
}

/// <summary>
/// Loads the progress of linked series folders from stored rows only (1.30.0): the series scope (the linked folder plus its unit
/// subfolders - <c>Volumes</c>, <c>Chapters</c>, <c>Season N</c>, <c>Part N</c> up to <see cref="MaxUnitDepth"/> levels, not
/// linked on their own, not side material; the Missing report's shape), ComicInfo volume / number, the stored volume maps and the
/// records, then <see cref="SeriesProgress.Evaluate"/>. Batched: one query per tree level and one per table for the whole set.
/// Shared by the Volumes view, the Missing report and the Official releases tab so they agree. Never contacts a provider; logs
/// nothing (callers log counts).
/// </summary>
public sealed class SeriesProgressLoader(MangaPixerDbContext db)
{
    public const int MaxUnitDepth = 3;

    private sealed record Folder(long Id, long Series, string? Name);

    public async Task<IReadOnlyDictionary<long, SeriesProgressEntry>> LoadAsync(IReadOnlyList<SeriesProgressTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new Dictionary<long, SeriesProgressEntry>();
        if (targets.Count == 0)
            return result;

        // The series scope, one level at a time.
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var archive = (int)CatalogNodeKind.Archive;
        var folders = targets.DistinctBy(t => t.NodeId).ToDictionary(t => t.NodeId, t => new Folder(t.NodeId, t.NodeId, null));
        var archivesIn = new Dictionary<long, List<(long Id, string PublicId, string Name, string SortKey)>>();
        var frontier = folders.Keys.ToList();
        for (var depth = 0; depth <= MaxUnitDepth && frontier.Count > 0; depth++)
        {
            var level = frontier;
            var children = await db.CatalogNodes.AsNoTracking()
                .Where(n => n.ParentId != null && level.Contains(n.ParentId.Value) && n.Availability != tombstoned)
                .OrderBy(n => n.SortKey)
                .Select(n => new { n.Id, ParentId = n.ParentId!.Value, n.Kind, n.DisplayName, n.PublicId, n.SortKey })
                .ToListAsync(ct);
            foreach (var c in children.Where(c => c.Kind == archive))
            {
                if (!archivesIn.TryGetValue(c.ParentId, out var list))
                    archivesIn[c.ParentId] = list = [];
                list.Add((c.Id, c.PublicId, c.DisplayName, c.SortKey));
            }
            if (depth == MaxUnitDepth)
                break;
            var units = children
                .Where(c => c.Kind != archive && AutoMatchText.IsUnitFolderName(c.DisplayName) && !CountEvidence.IsSideFolderName(c.DisplayName))
                .ToList();
            var unitIds = units.Select(u => u.Id).ToList();
            var ownLinked = (await db.NodeSeriesLinks.AsNoTracking().Where(l => unitIds.Contains(l.NodeId)).Select(l => l.NodeId).ToListAsync(ct))
                .ToHashSet();
            frontier = [];
            foreach (var u in units.Where(u => !ownLinked.Contains(u.Id)))
            {
                folders[u.Id] = new Folder(u.Id, folders[u.ParentId].Series, u.DisplayName);
                frontier.Add(u.Id);
            }
        }

        var comicInfo = await ComicInfoAsync(folders.Keys.ToList(), ct);
        // 1.39.0: what an admin declared on each series folder itself (edition volumes, label, tracking) - own scope only.
        var editions = await Declared.DeclaredFactsResolution.LoadEditionAsync(db, targets.Select(t => t.NodeId).Distinct().ToList(), ct);
        var language = await ReleasedInLanguage.PreferredAsync(db, ct);
        var recordIds = targets.Select(t => t.RecordId).Distinct().ToList();
        var maps = (await db.SeriesVolumeMaps.AsNoTracking().Where(m => recordIds.Contains(m.RecordId)).ToListAsync(ct))
            .ToLookup(m => m.RecordId);
        var wikipediaPages = (await db.WikipediaLists.AsNoTracking().Where(l => recordIds.Contains(l.RecordId) && l.PagesJson != null)
                .Select(l => new { l.RecordId, l.PagesJson }).ToListAsync(ct))
            .ToDictionary(l => l.RecordId, l => l.PagesJson);
        var records = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .Select(r => new RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter, r.PublishersJson,
                r.LicensedEn, r.TranslationComplete, r.Webtoon))
            .ToDictionaryAsync(r => r.Id, ct);

        var bySeries = folders.Values.ToLookup(f => f.Series);
        foreach (var target in targets.DistinctBy(t => t.NodeId))
        {
            var rows = new List<GroupingRow>();
            var missingFolders = new List<MissingFolder>();
            foreach (var folder in bySeries[target.NodeId])
            {
                var archives = archivesIn.GetValueOrDefault(folder.Id) ?? [];
                missingFolders.Add(new MissingFolder(folder.Name, archives.Select(a => a.Name).ToList()));
                foreach (var a in archives)
                {
                    var ci = comicInfo.GetValueOrDefault(a.Id);
                    rows.Add(new GroupingRow(a.PublicId, GroupingRowKind.Archive, a.Name, a.SortKey, folder.Name, ci.Volume, ci.Number));
                }
            }
            var restarts = MissingUnits.Evaluate(missingFolders, new PublishedTotals()).Verdict == MissingVerdict.Restarts;
            var record = records.GetValueOrDefault(target.RecordId);
            var (map, facts) = MapAndFacts(maps[target.RecordId].ToList(), record, language, target.ChaptersPerVolume,
                editions.GetValueOrDefault(target.NodeId));
            var progress = SeriesProgress.Evaluate(rows, map, facts, restarts);
            var units = rows.Select(VolumeGrouping.UnitsOf).ToList();
            result[target.NodeId] = new SeriesProgressEntry(progress, rows,
                units.Count(u => u.Chapter is null && u.Volume is not null), units.Count(u => u.Chapter is not null),
                ListCreditOf(maps[target.RecordId].ToList(), wikipediaPages.GetValueOrDefault(target.RecordId)));
        }
        return result;
    }

    /// <summary>
    /// The Wikipedia credit of a series (1.32.0): set only when the list a view shows was actually completed from the page - the stored
    /// Wikipedia list placed a volume or chapter MangaDex's did not - linking the first page the list was read from.
    /// </summary>
    public static ListCreditDto? ListCreditOf(IReadOnlyList<SeriesVolumeMapEntity> maps, string? pagesJson)
    {
        if (!ExactList(maps).WikipediaFilled || Volumes.Wikipedia.WikipediaVolumeService.ReadPages(pagesJson) is not [var first, ..])
            return null;
        return new ListCreditDto { Name = "Wikipedia", Url = Volumes.Wikipedia.WikipediaApi.PageUrl(first.Title), Title = first.Title };
    }

    /// <summary>The stored record fields the progress reads (1.34.0: the MangaUpdates webtoon flag).</summary>
    public sealed record RecordRow(
        long Id, int? Origin, int? OriginStatus, int? OriginVolumes, string? StatusText, double? LatestChapter, string? PublishersJson,
        bool? LicensedEn, bool? TranslationComplete, bool? Webtoon = null);

    /// <summary>
    /// The volume map and the progress facts of one linked record (stored rows only): MangaDex's exact list when its map is Ok,
    /// the chapters-per-volume ratio (its average, else the AniList row's), the highest volume known, whether it still runs, and
    /// what is released in the preferred language. <paramref name="ratio"/> (an admin's AniList lookup) is preferred for totals.
    /// 1.34.0: a near-empty MangaDex map is no list (<see cref="Volumes.VolumeMapService.IsUsable"/>), and a webtoon / manhwa / manhua
    /// without a real list is in chapter mode (<see cref="VolumeListRules.ChaptersOnly"/>: the map's <c>ChaptersOnly</c>, no ratio).
    /// 1.39.0: <paramref name="edition"/> - what an admin declared on the series folder itself - goes into the facts only
    /// (<see cref="ProgressFacts.VolumeOverride"/>, <see cref="ProgressFacts.Edition"/>, <see cref="ProgressFacts.TrackingOff"/>): the map
    /// stays the regular edition's, so chapter answers and the Volumes view's stacks are unchanged while the volume answers count the
    /// declared edition's volumes 1..N. Callers that only want the map (the view map, the volume-cover pass) pass none.
    /// </summary>
    public static (VolumeMapInput Map, ProgressFacts Facts) MapAndFacts(
        IReadOnlyList<SeriesVolumeMapEntity> maps, RecordRow? record, string language, double? ratio = null, DeclaredEditionFacts? edition = null)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var mangadex = Volumes.VolumeMapService.UsableMangaDexMap(maps);
        var aniList = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.AniListRatio && m.State == (int)VolumeMapState.Ok);
        var wikipedia = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.WikipediaList && m.State == (int)VolumeMapState.Ok);
        // 1.34.0: a real list = at least two real MangaDex volumes, or a Wikipedia list; without one a webtoon / manhwa / manhua lists chapters.
        var hasRealList = wikipedia is not null
            || (mangadex is not null && VolumeListRules.RealVolumeCount(VolumeMapJson.Read(mangadex.VolumesJson)) >= VolumeListRules.MinRealVolumes);
        var chaptersOnly = VolumeListRules.ChaptersOnly(record?.Webtoon, (MetadataOrigin?)record?.Origin, hasRealList);
        // 1.32.0: MangaDex's exact list, completed by the Wikipedia list where MangaDex lacks volumes / chapters (VolumeListMerge).
        var exact = ExactList(maps);
        var volumes = ToVolumes(exact.Volumes);
        // Chapter mode: no chapters-per-volume ratio (a print edition's ratio says nothing about a webtoon's episodes).
        var mapRatio = chaptersOnly ? null : mangadex?.ChaptersPerVolume ?? wikipedia?.ChaptersPerVolume ?? aniList?.ChaptersPerVolume;
        var known = mangadex?.KnownVolumeCount ?? aniList?.KnownVolumeCount ?? record?.OriginVolumes;
        if (exact.WikipediaFilled && volumes.Count > 0)
            known = Math.Max(known ?? 0, (int)decimal.Truncate(volumes.Max(v => v.Volume)));
        var status = (MetadataOriginStatus?)record?.OriginStatus;
        var ongoing = status is not (MetadataOriginStatus.Complete or MetadataOriginStatus.Cancelled);
        var source = volumes.Count > 0 ? (exact.WikipediaFilled ? VolumeListSource.Wikipedia : VolumeListSource.MangaDex)
            : mapRatio is not null ? VolumeListSource.AniList : VolumeListSource.FileNames;
        var releasedMap = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.MangaDexAggregate && m.ReleasedLanguage is not null);
        var release = ReleasedInLanguage.For(language, record?.PublishersJson, releasedMap?.ReleasedLanguage, releasedMap?.ReleasedChaptersJson);
        var map = new VolumeMapInput(volumes, mapRatio, known, ongoing, chaptersOnly ? VolumeListSource.FileNames : source, release.Chapters,
            release.Volumes, release.Language, chaptersOnly);

        var english = ReleasedInLanguage.IsEnglish(release.Language);
        var official = ReleasedInLanguage.OfficialOf(release.Language, record?.PublishersJson);
        var facts = new ProgressFacts(
            release.Language,
            (MetadataOrigin?)record?.Origin,
            status is MetadataOriginStatus.Unknown ? null : status,
            record?.OriginVolumes,
            MangaUpdatesStatusParser.Parse(record?.StatusText).Chapters,
            official?.Publisher,
            release.Volumes,
            release.EnglishChapters,
            official?.Status,
            english ? record?.LicensedEn : null,
            english && record?.LatestChapter is { } latest && latest >= 1 ? (int)Math.Floor(latest) : null,
            english ? record?.TranslationComplete : null,
            release.Chapters,
            ratio ?? mapRatio,
            edition?.VolumeTotal,
            edition?.Edition,
            edition?.TrackingOff ?? false);
        return (map, facts);
    }

    /// <summary>The exact volume list of a series as every reader sees it, and whether Wikipedia completed it.</summary>
    public sealed record ExactVolumeList(IReadOnlyList<VolumeMapEntry> Volumes, IReadOnlyList<string> Unassigned, bool WikipediaFilled);

    /// <summary>
    /// The series' exact volume list from its stored maps (1.32.0, design 6.5): MangaDex's list when its map is Ok, completed by the Wikipedia
    /// list (<see cref="VolumeListMerge"/>: MangaDex wins where it places a chapter; Wikipedia fills the volumes and chapters it lacks); the
    /// Wikipedia list alone when MangaDex has none (or only a near-empty one, 1.34.0). Stored rows only.
    /// </summary>
    public static ExactVolumeList ExactList(IEnumerable<SeriesVolumeMapEntity> maps)
    {
        ArgumentNullException.ThrowIfNull(maps);
        var all = maps as IReadOnlyCollection<SeriesVolumeMapEntity> ?? maps.ToList();
        // 1.34.0: a near-empty MangaDex map (at most one real volume, mostly unassigned) is no list.
        var mangadex = Volumes.VolumeMapService.UsableMangaDexMap(all);
        var wikipedia = all.FirstOrDefault(m => m.Source == (int)VolumeMapSource.WikipediaList && m.State == (int)VolumeMapState.Ok);
        var fromMangaDex = VolumeMapJson.Read(mangadex?.VolumesJson);
        var unassigned = VolumeMapJson.ReadChapters(mangadex?.UnassignedJson);
        if (wikipedia is null)
            return new ExactVolumeList(fromMangaDex, unassigned, false);
        var merged = VolumeListMerge.Merge(fromMangaDex, unassigned, VolumeMapJson.Read(wikipedia.VolumesJson));
        return new ExactVolumeList(merged.Volumes, merged.Unassigned, merged.WikipediaFilled);
    }

    /// <summary>Stored volume entries as the pure grouping reads them (numbers); entries without a parseable number or chapter are skipped.</summary>
    public static List<VolumeMapVolume> ToVolumes(IReadOnlyList<VolumeMapEntry> entries) =>
        entries
            .Select(e => (Volume: VolumeMapJson.Parse(e.Volume), Chapters: e.Chapters.Select(c => VolumeMapJson.Parse(c)).OfType<decimal>().Order().ToList()))
            .Where(e => e.Volume is not null && e.Chapters.Count > 0)
            .Select(e => new VolumeMapVolume(e.Volume!.Value, e.Chapters))
            .ToList();

    /// <summary><c>[{"v":"3","c":["17","18","25.5"]}]</c> -> volumes; malformed entries are skipped.</summary>
    public static List<VolumeMapVolume> ParseVolumes(string? json)
    {
        var result = new List<VolumeMapVolume>();
        if (string.IsNullOrWhiteSpace(json))
            return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
                return result;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object
                    || !item.TryGetProperty("v", out var v) || !TryNumber(v.GetString(), out var volume)
                    || !item.TryGetProperty("c", out var c) || c.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }
                var chapters = new List<decimal>();
                foreach (var chapter in c.EnumerateArray())
                {
                    if (chapter.ValueKind == JsonValueKind.String && TryNumber(chapter.GetString(), out var n))
                        chapters.Add(n);
                }
                if (chapters.Count > 0)
                    result.Add(new VolumeMapVolume(volume, chapters.Order().ToList()));
            }
        }
        catch (JsonException)
        {
            return [];
        }
        return result;
    }

    private static bool TryNumber(string? text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && value >= 0;

    /// <summary>ComicInfo <c>Volume</c> / <c>Number</c> of the archives directly below the given folders (parsed ComicInfo only).</summary>
    private async Task<Dictionary<long, (int? Volume, string? Number)>> ComicInfoAsync(List<long> parentIds, CancellationToken ct)
    {
        var rows = await (
            from e in db.EmbeddedMetadata.AsNoTracking()
            join n in db.CatalogNodes.AsNoTracking() on e.NodeId equals n.Id
            where n.ParentId != null && parentIds.Contains(n.ParentId.Value) && e.State == 1 && (e.Volume != null || e.Number != null)
            select new { e.NodeId, e.Volume, e.Number }).ToListAsync(ct);
        return rows.ToDictionary(r => r.NodeId, r => (r.Volume, (string?)r.Number));
    }
}
