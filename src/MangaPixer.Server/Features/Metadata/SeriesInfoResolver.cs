namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Read-time series-info resolution (1.24.0). Nothing is merged in storage; this
/// is where web data and ComicInfo.xml meet, per field, every time:
/// 1. <b>Web link</b>: walk the node ITSELF, then its parent, grandparent...
///    (bounded 64, nearest first). The first link row wins; "Don't match" means no
///    web series and stops inheritance.
/// 2. <b>ComicInfo</b>: an archive uses its own row; a folder aggregates its direct
///    child archives (or, when none of those carry ComicInfo, the archives one
///    level deeper), max 500 rows. The folder's series is the most common Series
///    when it covers at least 60% of the items that name one, else the folder is
///    <b>mixed</b> (an anthology/magazine: several series, never one).
/// 3. <b>Precedence</b>: nearest folder override (self first, then ancestors) ->
///    library override -> web first.
/// 4. <b>Merge</b>: series-level fields from the primary source, falling back to the
///    other per field; per-item fields (number, volume, issue title/summary) always
///    come from ComicInfo. <see cref="SeriesInfoDto.FieldSources"/> attributes each.
/// When "Show series information" is off for the node's library (globally or per
/// library) the result is <see cref="SeriesInfoState.None"/>.
/// </summary>
public sealed class SeriesInfoResolver
{
    public const int MaxWalkDepth = 64;
    public const int MaxAggregateRows = 500;
    public const double MajorityShare = 0.6;
    private const int MaxListEntries = 20;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataSettingsService _settings;
    private readonly MetadataProviderRegistry _providers;

    public SeriesInfoResolver(MangaPixerDbContext db, MetadataSettingsService settings, MetadataProviderRegistry providers)
    {
        _db = db;
        _settings = settings;
        _providers = providers;
    }

    public async Task<SeriesInfoDto> ResolveAsync(CatalogNodeEntity node, bool includeItems = false, CancellationToken ct = default)
    {
        var libraryPublicId = await _db.Libraries.Where(l => l.Id == node.LibraryId).Select(l => l.PublicId).FirstAsync(ct);

        if (await _settings.IsSeriesInfoHiddenAsync(node.LibraryId, ct))
            return Empty(node, libraryPublicId);

        var chain = await WalkChainAsync(node, ct);
        var chainIds = chain.Select(c => c.Id).ToList();

        // 1. Nearest link row, self first. needs_review (stage 2) is not a decision
        //    yet: it neither shows nor blocks, so the walk skips it.
        var links = await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => chainIds.Contains(l.NodeId) && l.State != (int)SeriesLinkState.NeedsReview)
            .ToListAsync(ct);
        NodeSeriesLinkEntity? nearestLink = null;
        ChainEntry? linkHolder = null;
        foreach (var entry in chain)
        {
            nearestLink = links.FirstOrDefault(l => l.NodeId == entry.Id);
            if (nearestLink is not null) { linkHolder = entry; break; }
        }

        MetadataRecordEntity? record = null;
        if (nearestLink is { RecordId: { } recordId } && nearestLink.State != (int)SeriesLinkState.DontMatch)
            record = await _db.MetadataRecords.AsNoTracking().FirstOrDefaultAsync(r => r.Id == recordId, ct);

        // 3. Precedence.
        var (precedence, precedenceSource) = await ResolvePrecedenceAsync(node.LibraryId, chainIds, chain, ct);

        // 2. ComicInfo.
        ComicInfoAggregate? ci;
        EmbeddedMetadataEntity? ownRow = null;
        if (node.Kind == (int)CatalogNodeKind.Archive)
        {
            ownRow = await CurrentParsedRowAsync(node.Id, ct);
            ci = ownRow is null ? null : ComicInfoAggregate.From(new List<EmbeddedMetadataEntity> { ownRow }, itemsTotal: 1);
        }
        else
        {
            ci = await AggregateFolderAsync(node.Id, includeItems, ct);
        }

        // Anchor: the link holder when a web record applies; else the ComicInfo
        // folder (an archive whose parent folder agrees on its series); else self.
        var self = chain[0];
        var anchor = self;
        if (record is not null && linkHolder is not null)
        {
            anchor = linkHolder;
        }
        else if (node.Kind == (int)CatalogNodeKind.Archive && ci?.SeriesName is { } seriesName && chain.Count > 1)
        {
            var parentAggregate = await AggregateFolderAsync(chain[1].Id, includeItems: false, ct);
            if (parentAggregate is { IsMixed: false, SeriesName: { } parentSeries }
                && string.Equals(Fold(parentSeries), Fold(seriesName), StringComparison.Ordinal))
                anchor = chain[1];
        }

        var hasCi = ci is not null;
        var mixed = ci?.IsMixed == true;
        var state = (record, hasCi, mixed) switch
        {
            (not null, true, false) => SeriesInfoState.WebAndComicInfo,
            (not null, _, _) => SeriesInfoState.Web,
            (null, true, true) => SeriesInfoState.Mixed,
            (null, true, false) => SeriesInfoState.ComicInfo,
            _ => nearestLink?.State == (int)SeriesLinkState.DontMatch ? SeriesInfoState.DontMatch : SeriesInfoState.None,
        };

        var dto = Merge(record, mixed ? null : ci, precedence);
        var title = dto.Title;
        var sources = new Dictionary<string, MetadataFieldSource>(dto.Sources);
        if (title is null && state is not SeriesInfoState.None and not SeriesInfoState.DontMatch)
            title = mixed ? null : anchor.DisplayName;

        return new SeriesInfoDto
        {
            NodeId = node.PublicId,
            NodeKind = (CatalogNodeKind)node.Kind,
            AnchorNodeId = anchor.PublicId,
            AnchorKind = (CatalogNodeKind)anchor.Kind,
            AnchorDisplayName = anchor.DisplayName,
            LibraryId = libraryPublicId,
            State = state,
            Title = title,
            AltTitles = dto.AltTitles,
            Description = dto.Description,
            Creators = dto.Creators,
            Genres = dto.Genres,
            Origin = (MetadataOrigin?)record?.Origin,
            Format = (MetadataFormat?)record?.Format,
            Webtoon = record?.Webtoon,
            StartYear = dto.StartYear,
            OriginStatus = (MetadataOriginStatus?)record?.OriginStatus,
            OriginVolumes = record?.OriginVolumes,
            LatestChapter = record?.LatestChapter,
            StatusText = record?.StatusText,
            LicensedEn = record?.LicensedEn,
            TranslationComplete = record?.TranslationComplete,
            Publishers = dto.Publishers,
            FieldSources = sources,
            Item = ownRow is null ? null : new SeriesInfoItemDto
            {
                Number = ownRow.Number,
                Volume = ownRow.Volume,
                Title = ownRow.Title,
                Summary = ownRow.Summary,
                Year = ownRow.Year,
                Month = ownRow.Month,
            },
            MixedSeries = mixed ? ci!.SeriesCounts : [],
            Web = record is null ? null : new SeriesInfoWebDto
            {
                Provider = record.Provider,
                ProviderName = _providers.DisplayNameFor(record.Provider),
                SiteUrl = record.SiteUrl,
                FetchedAt = record.FetchedAt,
                // The poster is lane B2's (image store + series-info/image endpoint).
                HasImage = false,
                ImageUrl = null,
            },
            ComicInfo = ci is null ? null : new SeriesInfoComicInfoDto
            {
                ItemsWithComicInfo = ci.ItemsWithComicInfo,
                ItemsTotal = ci.ItemsTotal,
                Count = ci.Count,
                WebLinks = ci.WebLinks,
            },
            Link = nearestLink is null || linkHolder is null ? null : new SeriesLinkInfoDto
            {
                State = (SeriesLinkState)nearestLink.State,
                NodeId = linkHolder.PublicId,
                Inherited = linkHolder.Id != node.Id,
                LinkedAt = nearestLink.UpdatedAt,
            },
            Precedence = precedence,
            PrecedenceSource = precedenceSource,
            Items = includeItems && ci is not null ? ci.Items : [],
        };
    }

    private static SeriesInfoDto Empty(CatalogNodeEntity node, string libraryPublicId) => new()
    {
        NodeId = node.PublicId,
        NodeKind = (CatalogNodeKind)node.Kind,
        AnchorNodeId = node.PublicId,
        AnchorKind = (CatalogNodeKind)node.Kind,
        AnchorDisplayName = node.DisplayName,
        LibraryId = libraryPublicId,
        State = SeriesInfoState.None,
        Precedence = MetadataPrecedence.WebFirst,
        PrecedenceSource = MetadataPrecedenceSource.Default,
    };

    /// <summary>Self first, then ancestors, bounded to <see cref="MaxWalkDepth"/> levels above self.</summary>
    private async Task<List<ChainEntry>> WalkChainAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var chain = new List<ChainEntry> { new(node.Id, node.PublicId, node.Kind, node.DisplayName) };
        var parentId = node.ParentId;
        var guard = 0;
        while (parentId.HasValue && guard++ < MaxWalkDepth)
        {
            var parent = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.Id == parentId.Value)
                .Select(n => new { n.Id, n.PublicId, n.Kind, n.DisplayName, n.ParentId })
                .FirstOrDefaultAsync(ct);
            if (parent is null)
                break;
            chain.Add(new ChainEntry(parent.Id, parent.PublicId, parent.Kind, parent.DisplayName));
            parentId = parent.ParentId;
        }
        return chain;
    }

    private async Task<(MetadataPrecedence, MetadataPrecedenceSource)> ResolvePrecedenceAsync(
        long libraryId, List<long> chainIds, List<ChainEntry> chain, CancellationToken ct)
    {
        var overrides = await _db.FolderMetadataPrecedences.AsNoTracking()
            .Where(f => chainIds.Contains(f.NodeId))
            .Select(f => new { f.NodeId, f.Precedence })
            .ToListAsync(ct);
        foreach (var entry in chain)
        {
            var match = overrides.FirstOrDefault(o => o.NodeId == entry.Id);
            if (match is not null && Enum.IsDefined((MetadataPrecedence)match.Precedence))
                return ((MetadataPrecedence)match.Precedence, MetadataPrecedenceSource.Folder);
        }

        var libraryValue = await _db.Libraries.Where(l => l.Id == libraryId).Select(l => l.MetadataPrecedence).FirstOrDefaultAsync(ct);
        if (libraryValue is { } v && Enum.IsDefined((MetadataPrecedence)v))
            return ((MetadataPrecedence)v, MetadataPrecedenceSource.Library);
        return (MetadataPrecedence.WebFirst, MetadataPrecedenceSource.Default);
    }

    private async Task<EmbeddedMetadataEntity?> CurrentParsedRowAsync(long nodeId, CancellationToken ct) =>
        await (from e in _db.EmbeddedMetadata.AsNoTracking()
               join a in _db.ArchiveItems on e.NodeId equals a.NodeId
               where e.NodeId == nodeId && e.State == 1 && e.ContentVersion == a.ContentVersion
               select e).FirstOrDefaultAsync(ct);

    /// <summary>
    /// ComicInfo over a folder's direct child archives; when none of them carries
    /// ComicInfo, over the archives one level deeper. Null when nothing is found.
    /// </summary>
    private async Task<ComicInfoAggregate?> AggregateFolderAsync(long folderId, bool includeItems, CancellationToken ct)
    {
        var direct = await ParsedChildRowsAsync(child => child.ParentId == folderId, ct);
        if (direct.Rows.Count > 0)
            return ComicInfoAggregate.From(direct.Rows, direct.Total, includeItems);

        var childFolderIds = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId == folderId && n.Kind == (int)CatalogNodeKind.Folder && n.Availability != 5)
            .Select(n => n.Id)
            .ToListAsync(ct);
        if (childFolderIds.Count == 0)
            return null;

        var deeper = await ParsedChildRowsAsync(child => child.ParentId != null && childFolderIds.Contains(child.ParentId.Value), ct);
        return deeper.Rows.Count > 0 ? ComicInfoAggregate.From(deeper.Rows, deeper.Total, includeItems) : null;
    }

    private async Task<(List<AggregateRow> Rows, int Total)> ParsedChildRowsAsync(
        System.Linq.Expressions.Expression<Func<CatalogNodeEntity, bool>> scope, CancellationToken ct)
    {
        var archives = _db.CatalogNodes.AsNoTracking()
            .Where(scope)
            .Where(n => n.Kind == (int)CatalogNodeKind.Archive && n.Availability != 5);

        var total = await archives.CountAsync(ct);
        if (total == 0)
            return ([], 0);

        var rows = await (from n in archives
                          join a in _db.ArchiveItems on n.Id equals a.NodeId
                          join e in _db.EmbeddedMetadata on n.Id equals e.NodeId
                          where e.State == 1 && e.ContentVersion == a.ContentVersion
                          orderby n.SortKey
                          select new AggregateRow(n.PublicId, n.DisplayName, e))
            .Take(MaxAggregateRows)
            .ToListAsync(ct);
        return (rows, total);
    }

    private MergeResult Merge(MetadataRecordEntity? web, ComicInfoAggregate? ci, MetadataPrecedence precedence)
    {
        var sources = new Dictionary<string, MetadataFieldSource>();
        var webFirst = precedence == MetadataPrecedence.WebFirst;

        T? Pick<T>(string field, T? webValue, T? ciValue) where T : class
        {
            var (first, firstSource, second, secondSource) = webFirst
                ? (webValue, MetadataFieldSource.Web, ciValue, MetadataFieldSource.ComicInfo)
                : (ciValue, MetadataFieldSource.ComicInfo, webValue, MetadataFieldSource.Web);
            if (HasValue(first)) { sources[field] = firstSource; return first; }
            if (HasValue(second)) { sources[field] = secondSource; return second; }
            return null;
        }

        int? PickInt(string field, int? webValue, int? ciValue)
        {
            var (first, firstSource, second, secondSource) = webFirst
                ? (webValue, MetadataFieldSource.Web, ciValue, MetadataFieldSource.ComicInfo)
                : (ciValue, MetadataFieldSource.ComicInfo, webValue, MetadataFieldSource.Web);
            if (first is not null) { sources[field] = firstSource; return first; }
            if (second is not null) { sources[field] = secondSource; return second; }
            return null;
        }

        var webCreators = web is null ? null : MetadataJson.ReadList<MetadataJson.Creator>(web.CreatorsJson)
            .Select(c => new SeriesCreatorDto { Name = c.Name, Role = c.Role }).ToList();
        var webGenres = web is null ? null : MetadataJson.ReadList<string>(web.GenresJson).ToList();
        var webAlt = web is null ? null : MetadataJson.ReadList<string>(web.AltTitlesJson).ToList();
        var webPublishers = web is null ? null : MetadataJson.ReadList<MetadataJson.Publisher>(web.PublishersJson)
            .Select(p => new SeriesPublisherDto { Name = p.Name, Kind = p.Kind }).ToList();

        // Web-only fields (origin, format, status, ...) carry their source when present.
        if (web is not null)
        {
            if (web.Origin is not null) sources["origin"] = MetadataFieldSource.Web;
            if (web.Format is not null) sources["format"] = MetadataFieldSource.Web;
            if (web.Webtoon is not null) sources["webtoon"] = MetadataFieldSource.Web;
            if (web.OriginStatus is not null) sources["originStatus"] = MetadataFieldSource.Web;
            if (web.OriginVolumes is not null) sources["originVolumes"] = MetadataFieldSource.Web;
            if (web.LatestChapter is not null) sources["latestChapter"] = MetadataFieldSource.Web;
            if (web.LicensedEn is not null) sources["licensedEn"] = MetadataFieldSource.Web;
            if (web.TranslationComplete is not null) sources["translationComplete"] = MetadataFieldSource.Web;
        }

        return new MergeResult
        {
            Title = Pick("title", string.IsNullOrWhiteSpace(web?.Title) ? null : web.Title, ci?.SeriesName),
            AltTitles = Pick("altTitles", webAlt, ci?.AltTitles) ?? [],
            // ComicInfo has no series-level description (Summary is per issue).
            Description = Pick("description", web?.Description, null),
            Creators = Pick("creators", webCreators, ci?.Creators) ?? [],
            Genres = Pick("genres", webGenres, ci?.Genres) ?? [],
            StartYear = PickInt("startYear", web?.StartYear, ci?.MinYear),
            Publishers = Pick("publishers", webPublishers, ci?.Publishers) ?? [],
            Sources = sources,
        };
    }

    private static bool HasValue(object? value) => value switch
    {
        null => false,
        string s => !string.IsNullOrWhiteSpace(s),
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };

    internal static string Fold(string value) => value.Trim().ToUpperInvariant();

    private sealed record ChainEntry(long Id, string PublicId, int Kind, string DisplayName);

    internal sealed record AggregateRow(string NodePublicId, string DisplayName, EmbeddedMetadataEntity Row);

    private sealed record MergeResult
    {
        public string? Title { get; init; }
        public IReadOnlyList<string> AltTitles { get; init; } = [];
        public string? Description { get; init; }
        public IReadOnlyList<SeriesCreatorDto> Creators { get; init; } = [];
        public IReadOnlyList<string> Genres { get; init; } = [];
        public int? StartYear { get; init; }
        public IReadOnlyList<SeriesPublisherDto> Publishers { get; init; } = [];
        public required Dictionary<string, MetadataFieldSource> Sources { get; init; }
    }

    /// <summary>ComicInfo rolled up over one archive or a folder's items.</summary>
    internal sealed class ComicInfoAggregate
    {
        public string? SeriesName { get; private init; }
        public bool IsMixed { get; private init; }
        public IReadOnlyList<SeriesMixedEntryDto> SeriesCounts { get; private init; } = [];
        public List<string> AltTitles { get; private init; } = [];
        public List<SeriesCreatorDto> Creators { get; private init; } = [];
        public List<string> Genres { get; private init; } = [];
        public List<SeriesPublisherDto> Publishers { get; private init; } = [];
        public int? MinYear { get; private init; }
        public int? Count { get; private init; }
        public IReadOnlyList<string> WebLinks { get; private init; } = [];
        public int ItemsWithComicInfo { get; private init; }
        public int ItemsTotal { get; private init; }
        public IReadOnlyList<SeriesInfoItemRowDto> Items { get; private init; } = [];

        public static ComicInfoAggregate From(IReadOnlyList<EmbeddedMetadataEntity> rows, int itemsTotal) =>
            From(rows.Select(r => new AggregateRow(string.Empty, string.Empty, r)).ToList(), itemsTotal, includeItems: false);

        public static ComicInfoAggregate From(IReadOnlyList<AggregateRow> rows, int itemsTotal, bool includeItems)
        {
            // Series: the most common name (case-insensitive) among rows naming one.
            var named = rows.Where(r => !string.IsNullOrWhiteSpace(r.Row.Series)).Select(r => r.Row.Series!.Trim()).ToList();
            var groups = named
                .GroupBy(Fold)
                .Select(g => new { Name = g.GroupBy(n => n).OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).First().Key, Count = g.Count() })
                .OrderByDescending(g => g.Count)
                .ThenBy(g => g.Name, StringComparer.Ordinal)
                .ToList();
            string? series = null;
            var mixed = false;
            if (groups.Count > 0)
            {
                if (groups[0].Count >= MajorityShare * named.Count)
                    series = groups[0].Name;
                else
                    mixed = true;
            }

            static List<T> Top<T>(IEnumerable<T> items, Func<T, string> key, int max) =>
                items.GroupBy(key, StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .Take(max)
                    .Select(g => g.First())
                    .ToList();

            var creators = Top(
                rows.SelectMany(r => MetadataJson.ReadList<MetadataJson.Creator>(r.Row.CreatorsJson))
                    .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                    .Select(c => new SeriesCreatorDto { Name = c.Name, Role = c.Role }),
                c => c.Role + "\u0001" + c.Name, MaxListEntries);
            var genres = Top(rows.SelectMany(r => MetadataJson.ReadList<string>(r.Row.GenresJson)), g => g, MaxListEntries);
            var alt = Top(rows.Where(r => !string.IsNullOrWhiteSpace(r.Row.AlternateSeries)).Select(r => r.Row.AlternateSeries!), a => a, 5);
            var publishers = Top(rows.Where(r => !string.IsNullOrWhiteSpace(r.Row.Publisher)).Select(r => r.Row.Publisher!), p => p, 3)
                .Select(p => new SeriesPublisherDto { Name = p, Kind = "other" }).ToList();
            var webLinks = rows.SelectMany(r => MetadataJson.ReadList<string>(r.Row.WebUrlsJson))
                .Where(MetadataWebLinks.IsAllowed)
                .Distinct(StringComparer.Ordinal)
                .Take(10)
                .ToList();

            return new ComicInfoAggregate
            {
                SeriesName = series,
                IsMixed = mixed,
                SeriesCounts = mixed ? groups.Take(50).Select(g => new SeriesMixedEntryDto { Name = g.Name, Count = g.Count }).ToList() : [],
                AltTitles = alt,
                Creators = creators,
                Genres = genres,
                Publishers = publishers,
                MinYear = rows.Where(r => r.Row.Year is not null).Select(r => r.Row.Year).Min(),
                Count = rows.Where(r => r.Row.Count is not null).Select(r => r.Row.Count).Max(),
                WebLinks = webLinks,
                ItemsWithComicInfo = rows.Count,
                ItemsTotal = Math.Max(itemsTotal, rows.Count),
                Items = includeItems
                    ? rows.Select(r => new SeriesInfoItemRowDto
                    {
                        NodeId = r.NodePublicId,
                        DisplayName = r.DisplayName,
                        Number = r.Row.Number,
                        Volume = r.Row.Volume,
                        Title = r.Row.Title,
                        Year = r.Row.Year,
                    }).ToList()
                    : [],
            };
        }
    }
}
