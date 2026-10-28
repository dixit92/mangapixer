namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The tag index's reads of the stored sources (1.45.0 lane F): projected, no-tracking queries that turn records, ComicInfo rows, declared
/// facts and link rows into the pure Core inputs. No network; names and titles stay in memory (never logged).
/// </summary>
internal static class FacetIndexData
{
    /// <summary>The ComicInfo state "parsed" (<c>embedded_metadata.State</c>).</summary>
    public const int ComicInfoParsed = 1;

    public sealed record RecordRow(
        long Id, string Provider, string Title, int? Origin, int? Format, bool? Webtoon, int? StartYear,
        string? CreatorsJson, string? GenresJson, string? PublishersJson, string? PublicationsJson, string? CategoriesJson = null);

    public sealed record ComicInfoRow(long NodeId, long LibraryId, string? Series, string? CreatorsJson, string? GenresJson,
        string? Publisher, string? Imprint, int? Year, string? TagsJson = null);

    public sealed record LinkRow(long NodeId, long LibraryId, int State, long? RecordId);

    public sealed record DeclaredRow(long LibraryId, long? NodeId, string Key, string Value, string? Role, int Position, string? ExternalRef);

    public sealed record NodeRow(long Id, long? ParentId, long LibraryId, bool IsFolder);

    /// <summary>The record's credits with their provider reference (a MangaUpdates credit's <c>author_id</c>).</summary>
    public static IReadOnlyList<FacetCredit> Credits(string provider, string? creatorsJson) =>
        MetadataJson.ReadList<MetadataJson.Creator>(creatorsJson)
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new FacetCredit(c.Name, c.Role, FacetText.ProviderRef(provider, c.ProviderId)))
            .ToList();

    /// <summary>A ComicInfo row's credits (no provider reference).</summary>
    public static IReadOnlyList<FacetCredit> ComicInfoCredits(string? creatorsJson) =>
        MetadataJson.ReadList<MetadataJson.Creator>(creatorsJson)
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new FacetCredit(c.Name, c.Role))
            .ToList();

    public static FacetRecord ToRecord(RecordRow r) => new(
        r.Id,
        r.Title,
        r.Origin is { } o && Enum.IsDefined((MetadataOrigin)o) ? (MetadataOrigin)o : null,
        r.Format is { } f && Enum.IsDefined((MetadataFormat)f) ? (MetadataFormat)f : null,
        r.Webtoon,
        r.StartYear,
        Credits(r.Provider, r.CreatorsJson),
        MetadataJson.ReadList<string>(r.GenresJson),
        MetadataJson.ReadList<MetadataJson.Publisher>(r.PublishersJson)
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => new FacetPublisher(p.Name, p.Kind))
            .ToList(),
        MetadataJson.ReadList<MetadataJson.Publication>(r.PublicationsJson)
            .Where(p => !string.IsNullOrWhiteSpace(p.Name))
            .Select(p => p.Name)
            .ToList(),
        Categories(r.CategoriesJson));

    /// <summary>1.49.0 (lane T): a record's stored categories (<c>[{name, votes}]</c>), blank names left out.</summary>
    public static IReadOnlyList<FacetCategory> Categories(string? categoriesJson) =>
        MetadataJson.ReadList<MetadataJson.Category>(categoriesJson)
            .Where(c => !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => new FacetCategory(c.Name, c.Votes))
            .ToList();

    public static FacetComicInfo ToComicInfo(ComicInfoRow r) => new(
        r.NodeId, r.Series, ComicInfoCredits(r.CreatorsJson), MetadataJson.ReadList<string>(r.GenresJson), r.Publisher, r.Imprint, r.Year,
        MetadataJson.ReadList<string>(r.TagsJson));

    public static FacetLink? ToLink(LinkRow r) =>
        Enum.IsDefined((SeriesLinkState)r.State) ? new FacetLink((SeriesLinkState)r.State, r.RecordId) : null;

    public static FacetDeclaredFact ToFact(DeclaredRow r) => new(r.NodeId, r.Key, r.Value, r.Role, r.Position, r.ExternalRef);

    /// <summary>Records by id (projected; the JSON columns the index reads).</summary>
    public static async Task<List<RecordRow>> RecordsAsync(MangaPixerDbContext db, IReadOnlyCollection<long>? ids, CancellationToken ct)
    {
        var query = db.MetadataRecords.AsNoTracking();
        if (ids is not null)
        {
            if (ids.Count == 0)
                return [];
            var rows = new List<RecordRow>(ids.Count);
            foreach (var chunk in ids.Chunk(500))
                rows.AddRange(await Project(query.Where(r => chunk.Contains(r.Id))).ToListAsync(ct));
            return rows;
        }
        return await Project(query).ToListAsync(ct);

        static IQueryable<RecordRow> Project(IQueryable<Persistence.Entities.MetadataRecordEntity> q) =>
            q.Select(r => new RecordRow(r.Id, r.Provider, r.Title, r.Origin, r.Format, r.Webtoon, r.StartYear,
                r.CreatorsJson, r.GenresJson, r.PublishersJson, r.PublicationsJson, r.CategoriesJson));
    }

    /// <summary>The CURRENT parsed ComicInfo rows of live archives (of one library, or all): the row of the archive's content version.</summary>
    public static async Task<List<ComicInfoRow>> ComicInfoAsync(MangaPixerDbContext db, long? libraryId, CancellationToken ct)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var archive = (int)CatalogNodeKind.Archive;
        var nodes = db.CatalogNodes.AsNoTracking().Where(n => n.Kind == archive && n.Availability != tombstoned);
        if (libraryId is { } id)
            nodes = nodes.Where(n => n.LibraryId == id);
        return await (from n in nodes
                      join a in db.ArchiveItems.AsNoTracking() on n.Id equals a.NodeId
                      join e in db.EmbeddedMetadata.AsNoTracking() on n.Id equals e.NodeId
                      where e.State == ComicInfoParsed && e.ContentVersion == a.ContentVersion
                      select new ComicInfoRow(n.Id, n.LibraryId, e.Series, e.CreatorsJson, e.GenresJson, e.Publisher, e.Imprint, e.Year, e.TagsJson))
            .ToListAsync(ct);
    }

    public static async Task<List<LinkRow>> LinksAsync(MangaPixerDbContext db, long? libraryId, CancellationToken ct)
    {
        var query = db.NodeSeriesLinks.AsNoTracking();
        if (libraryId is { } id)
            query = query.Where(l => l.LibraryId == id);
        return await query.Select(l => new LinkRow(l.NodeId, l.LibraryId, l.State, l.RecordId)).ToListAsync(ct);
    }

    /// <summary>The declared type and creator rows (the keys the index reads).</summary>
    public static async Task<List<DeclaredRow>> DeclaredAsync(MangaPixerDbContext db, long? libraryId, CancellationToken ct)
    {
        var query = db.DeclaredFacts.AsNoTracking().Where(f => f.Key == DeclaredFactKeys.Type || f.Key == DeclaredFactKeys.Creator);
        if (libraryId is { } id)
            query = query.Where(f => f.LibraryId == id);
        return await query.Select(f => new DeclaredRow(f.LibraryId, f.NodeId, f.Key, f.Value, f.Role, f.Position, f.ExternalRef)).ToListAsync(ct);
    }

    /// <summary>Every live node of every library (ids and shape only - no names): the trees the creator pass walks.</summary>
    public static async Task<List<NodeRow>> AllNodesAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        return await db.CatalogNodes.AsNoTracking()
            .Where(n => n.Availability != tombstoned)
            .Select(n => new NodeRow(n.Id, n.ParentId, n.LibraryId, n.Kind == folder))
            .ToListAsync(ct);
    }
}
