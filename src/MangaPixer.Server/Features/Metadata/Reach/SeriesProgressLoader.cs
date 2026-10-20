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

    public async Task<IReadOnlyDictionary<long, SeriesProgressEntry>> LoadAsync(IReadOnlyList<SeriesProgressTarget> targets, CancellationToken ct) =>
        (await LoadScopedAsync(targets, ct)).Entries;

    /// <summary>
    /// 1.46.0 lane C: the entries with the catalog part of what they were computed from - the scope folders (the linked folders and
    /// their unit subfolders) and the ComicInfo rows below them - so the Completion filter's answer cache can check those rows again.
    /// </summary>
    internal sealed record Loaded(
        IReadOnlyDictionary<long, SeriesProgressEntry> Entries, IReadOnlyList<long> ScopeFolders,
        IReadOnlyDictionary<long, (int? Volume, string? Number)> ComicInfo);

    /// <summary>
    /// <see cref="LoadAsync"/> with what it read (<see cref="Loaded"/>). 1.46.0 lane C: the volume contents scan's answers of all the
    /// series are read together (<see cref="VolumeScan.VolumeContentReader.LoadManyAsync"/>, the same answer per series), and each map
    /// row's JSON is parsed once (<see cref="StoredMaps"/>); only the map columns the progress reads are loaded.
    /// </summary>
    internal async Task<Loaded> LoadScopedAsync(IReadOnlyList<SeriesProgressTarget> targets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(targets);
        var result = new Dictionary<long, SeriesProgressEntry>();
        if (targets.Count == 0)
            return new Loaded(result, [], new Dictionary<long, (int? Volume, string? Number)>());

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

        var scopeFolders = folders.Keys.ToList();
        var comicInfo = await ComicInfoAsync(db, scopeFolders, ct);
        // 1.39.0: what an admin declared on each series folder itself (edition volumes, label, tracking) - own scope only.
        var editions = await Declared.DeclaredFactsResolution.LoadEditionAsync(db, targets.Select(t => t.NodeId).Distinct().ToList(), ct);
        var language = await ReleasedInLanguage.PreferredAsync(db, ct);
        var recordIds = targets.Select(t => t.RecordId).Distinct().ToList();
        // 1.46.0 lane C: only the columns the progress reads (not the readable chapter list, the hash or the schedule).
        var maps = (await db.SeriesVolumeMaps.AsNoTracking().Where(m => recordIds.Contains(m.RecordId))
                .Select(m => new SeriesVolumeMapEntity
                {
                    Id = m.Id,
                    RecordId = m.RecordId,
                    Source = m.Source,
                    State = m.State,
                    VolumesJson = m.VolumesJson,
                    UnassignedJson = m.UnassignedJson,
                    ChaptersPerVolume = m.ChaptersPerVolume,
                    KnownVolumeCount = m.KnownVolumeCount,
                    ReleasedLanguage = m.ReleasedLanguage,
                    ReleasedChaptersJson = m.ReleasedChaptersJson,
                    Version = m.Version,
                })
                .ToListAsync(ct))
            .ToLookup(m => m.RecordId);
        var parsed = new Dictionary<long, StoredMaps>();
        StoredMaps MapsOf(long recordId)
        {
            if (!parsed.TryGetValue(recordId, out var stored))
                parsed[recordId] = stored = StoredMaps.Of(maps[recordId].ToList());
            return stored;
        }
        var wikipediaPages = (await db.WikipediaLists.AsNoTracking().Where(l => recordIds.Contains(l.RecordId) && l.PagesJson != null)
                .Select(l => new { l.RecordId, l.PagesJson }).ToListAsync(ct))
            .ToDictionary(l => l.RecordId, l => l.PagesJson);
        var records = await db.MetadataRecords.AsNoTracking().Where(r => recordIds.Contains(r.Id))
            .Select(r => new RecordRow(r.Id, r.Origin, r.OriginStatus, r.OriginVolumes, r.StatusText, r.LatestChapter, r.PublishersJson,
                r.LicensedEn, r.TranslationComplete, r.Webtoon))
            .ToDictionaryAsync(r => r.Id, ct);

        // Each series' rows first, so the volume contents answers of all of them are read together.
        var bySeries = folders.Values.ToLookup(f => f.Series);
        var series = new List<(SeriesProgressTarget Target, List<GroupingRow> Built, Dictionary<long, string> ArchiveIds, bool Restarts)>();
        foreach (var target in targets.DistinctBy(t => t.NodeId))
        {
            var built = new List<GroupingRow>();
            var missingFolders = new List<MissingFolder>();
            var archiveIds = new Dictionary<long, string>();
            foreach (var folder in bySeries[target.NodeId])
            {
                var archives = archivesIn.GetValueOrDefault(folder.Id) ?? [];
                missingFolders.Add(new MissingFolder(folder.Name, archives.Select(a => a.Name).ToList()));
                foreach (var a in archives)
                {
                    var ci = comicInfo.GetValueOrDefault(a.Id);
                    built.Add(new GroupingRow(a.PublicId, GroupingRowKind.Archive, a.Name, a.SortKey, folder.Name, ci.Volume, ci.Number));
                    archiveIds[a.Id] = a.PublicId;
                }
            }
            var restarts = MissingUnits.Evaluate(missingFolders, new PublishedTotals()).Verdict == MissingVerdict.Restarts;
            series.Add((target, built, archiveIds, restarts));
        }
        // 1.44.0: the volume contents scan's answers, as the Volumes view reads them (VolumeContentReader, VolumeContents.Apply) - only for
        // a series with a volume file among its rows.
        var scanned = series.Where(s => VolumeContents.MayApply(s.Built)).ToList();
        var scans = await VolumeScan.VolumeContentReader.LoadManyAsync(db, scanned.Select(s => (IReadOnlyDictionary<long, string>)s.ArchiveIds).ToList(), ct);
        var scansOf = new Dictionary<long, IReadOnlyList<ScannedVolume>>();
        for (var i = 0; i < scanned.Count; i++)
            scansOf[scanned[i].Target.NodeId] = scans[i];

        foreach (var (target, built, _, restarts) in series)
        {
            var record = records.GetValueOrDefault(target.RecordId);
            var stored = MapsOf(target.RecordId);
            var (listMap, facts) = MapAndFacts(stored, record, language, target.ChaptersPerVolume, editions.GetValueOrDefault(target.NodeId));
            var (rows, scannedMap) = VolumeContents.Apply(built, listMap, scansOf.GetValueOrDefault(target.NodeId) ?? []);
            var map = scannedMap ?? listMap;
            var progress = SeriesProgress.Evaluate(rows, map, facts, restarts);
            var units = rows.Select(VolumeGrouping.UnitsOf).ToList();
            result[target.NodeId] = new SeriesProgressEntry(progress, rows,
                units.Count(u => u.Chapter is null && u.Volume is not null), units.Count(u => u.Chapter is not null),
                ListCreditOf(stored, wikipediaPages.GetValueOrDefault(target.RecordId)));
        }
        return new Loaded(result, scopeFolders, comicInfo);
    }


    /// <summary>
    /// The Wikipedia credit of a series (1.32.0): set only when the list a view shows was actually completed from the page - the stored
    /// Wikipedia list placed a volume or chapter MangaDex's did not - linking the first page the list was read from.
    /// </summary>
    public static ListCreditDto? ListCreditOf(IReadOnlyList<SeriesVolumeMapEntity> maps, string? pagesJson) =>
        ListCreditOf(StoredMaps.Of(maps), pagesJson);

    /// <summary><see cref="ListCreditOf(IReadOnlyList{SeriesVolumeMapEntity}, string?)"/> from maps already read (1.46.0 lane C).</summary>
    public static ListCreditDto? ListCreditOf(StoredMaps maps, string? pagesJson)
    {
        ArgumentNullException.ThrowIfNull(maps);
        if (!maps.Exact.WikipediaFilled || Volumes.Wikipedia.WikipediaVolumeService.ReadPages(pagesJson) is not [var first, ..])
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
        IReadOnlyList<SeriesVolumeMapEntity> maps, RecordRow? record, string language, double? ratio = null, DeclaredEditionFacts? edition = null) =>
        MapAndFacts(StoredMaps.Of(maps), record, language, ratio, edition);

    /// <summary>
    /// <see cref="MapAndFacts(IReadOnlyList{SeriesVolumeMapEntity}, RecordRow?, string, double?, DeclaredEditionFacts?)"/> from maps already
    /// read (1.46.0 lane C: each map's JSON parsed once).
    /// </summary>
    public static (VolumeMapInput Map, ProgressFacts Facts) MapAndFacts(
        StoredMaps stored, RecordRow? record, string language, double? ratio = null, DeclaredEditionFacts? edition = null)
    {
        ArgumentNullException.ThrowIfNull(stored);
        var maps = stored.Rows;
        var mangadex = stored.MangaDex;
        var aniList = maps.FirstOrDefault(m => m.Source == (int)VolumeMapSource.AniListRatio && m.State == (int)VolumeMapState.Ok);
        var wikipedia = stored.Wikipedia;
        // 1.34.0: a real list = at least two real MangaDex volumes, or a Wikipedia list; without one a webtoon / manhwa / manhua lists chapters.
        var hasRealList = wikipedia is not null
            || (mangadex is not null && VolumeListRules.RealVolumeCount(stored.MangaDexVolumes) >= VolumeListRules.MinRealVolumes);
        var chaptersOnly = VolumeListRules.ChaptersOnly(record?.Webtoon, (MetadataOrigin?)record?.Origin, hasRealList);
        // 1.32.0: MangaDex's exact list, completed by the Wikipedia list where MangaDex lacks volumes / chapters (VolumeListMerge).
        var exact = stored.Exact;
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
        return StoredMaps.Of(maps as IReadOnlyList<SeriesVolumeMapEntity> ?? maps.ToList()).Exact;
    }

    /// <summary>
    /// The stored maps of one record with each JSON column read once (1.46.0 lane C - before, one progress read the same MangaDex list
    /// four to five times): the usable MangaDex map (<see cref="Volumes.VolumeMapService.IsUsable"/>: Ok and not near-empty, 1.34.0) with
    /// its volumes and unassigned chapters, the Ok Wikipedia map, and the exact list (<see cref="ExactList"/>). Pure; the same values the
    /// per-call reads gave.
    /// </summary>
    public sealed class StoredMaps
    {
        private StoredMaps(IReadOnlyList<SeriesVolumeMapEntity> rows, SeriesVolumeMapEntity? mangaDex, IReadOnlyList<VolumeMapEntry> mangaDexVolumes,
            SeriesVolumeMapEntity? wikipedia, ExactVolumeList exact)
        {
            Rows = rows;
            MangaDex = mangaDex;
            MangaDexVolumes = mangaDexVolumes;
            Wikipedia = wikipedia;
            Exact = exact;
        }

        /// <summary>The map rows of the record, as read.</summary>
        public IReadOnlyList<SeriesVolumeMapEntity> Rows { get; }

        /// <summary>The usable MangaDex map, or null (<see cref="Volumes.VolumeMapService.UsableMangaDexMap"/>).</summary>
        public SeriesVolumeMapEntity? MangaDex { get; }

        /// <summary>The usable MangaDex map's volumes (empty without one).</summary>
        public IReadOnlyList<VolumeMapEntry> MangaDexVolumes { get; }

        /// <summary>The Ok Wikipedia map, or null.</summary>
        public SeriesVolumeMapEntity? Wikipedia { get; }

        /// <summary>The exact volume list every reader sees (<see cref="ExactList"/>).</summary>
        public ExactVolumeList Exact { get; }

        public static StoredMaps Of(IReadOnlyList<SeriesVolumeMapEntity> rows)
        {
            ArgumentNullException.ThrowIfNull(rows);
            SeriesVolumeMapEntity? mangadex = null;
            IReadOnlyList<VolumeMapEntry> volumes = [];
            IReadOnlyList<string> unassigned = [];
            // The first MangaDex map that is Ok and not near-empty (UsableMangaDexMap / IsUsable), its JSON read once.
            foreach (var m in rows)
            {
                if (m.Source != (int)VolumeMapSource.MangaDexAggregate || m.State != (int)VolumeMapState.Ok)
                    continue;
                var v = VolumeMapJson.Read(m.VolumesJson);
                var u = VolumeMapJson.ReadChapters(m.UnassignedJson);
                if (VolumeListRules.IsNearEmpty(v, u))
                    continue;
                (mangadex, volumes, unassigned) = (m, v, u);
                break;
            }
            var wikipedia = rows.FirstOrDefault(m => m.Source == (int)VolumeMapSource.WikipediaList && m.State == (int)VolumeMapState.Ok);
            ExactVolumeList exact;
            if (wikipedia is null)
            {
                exact = new ExactVolumeList(volumes, unassigned, false);
            }
            else
            {
                var merged = VolumeListMerge.Merge(volumes, unassigned, VolumeMapJson.Read(wikipedia.VolumesJson));
                exact = new ExactVolumeList(merged.Volumes, merged.Unassigned, merged.WikipediaFilled);
            }
            return new StoredMaps(rows, mangadex, volumes, wikipedia, exact);
        }
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
    internal static async Task<Dictionary<long, (int? Volume, string? Number)>> ComicInfoAsync(MangaPixerDbContext db, IReadOnlyList<long> parentIds,
        CancellationToken ct)
    {
        var ids = parentIds as List<long> ?? parentIds.ToList();
        var rows = await (
            from e in db.EmbeddedMetadata.AsNoTracking()
            join n in db.CatalogNodes.AsNoTracking() on e.NodeId equals n.Id
            where n.ParentId != null && ids.Contains(n.ParentId.Value) && e.State == 1 && (e.Volume != null || e.Number != null)
            select new { e.NodeId, e.Volume, e.Number }).ToListAsync(ct);
        return rows.ToDictionary(r => r.NodeId, r => (r.Volume, (string?)r.Number));
    }
}
