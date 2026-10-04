namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The live (non-tombstoned) catalog tree of one library, loaded with ONE projected
/// query (id, parent, kind, display name, sort key - no paths), from which the
/// auto-match plumbing builds the detector's <see cref="FolderShape"/>s. Pure reads;
/// never touches the filesystem. Top-level folders have depth 1 (the library root
/// is not a node). Optionally carries the <see cref="ProviderAuthorSet"/> of the
/// library (1.28.0): the authors of records already linked in it, read from the local
/// database only.
/// </summary>
public sealed class LibraryTreeSnapshot
{
    public sealed record Node(long Id, long? ParentId, bool IsFolder, string Name, string SortKey);

    /// <summary>
    /// Changes whenever a Confirmed / Auto link of the library is added, removed or changed, or a linked
    /// record is re-fetched: the key under which a cached <see cref="ProviderAuthorSet"/> stays valid.
    /// </summary>
    public readonly record struct LinkStamp(int Links, DateTimeOffset? LinksUpdated, DateTimeOffset? RecordsFetched);

    /// <summary>
    /// The author and artist names of the records linked (Confirmed or Auto) in one library, and the nodes
    /// carrying those links. Feeds <see cref="FolderShape.KnownAuthorNames"/> (the provider-author half of the
    /// artist-folder rule); a folder that carries a link itself gets no names.
    /// </summary>
    public sealed record ProviderAuthorSet(IReadOnlyList<string> Names, IReadOnlySet<long> LinkedNodeIds, LinkStamp Stamp)
    {
        public static ProviderAuthorSet None { get; } = new([], new HashSet<long>(), default);
    }

    private readonly Dictionary<long, Node> _nodes;
    private readonly Dictionary<long, List<Node>> _children;
    private readonly Dictionary<long, int> _descendantArchives;

    private LibraryTreeSnapshot(LibraryTreeSnapshot tree, ProviderAuthorSet authors, IReadOnlyDictionary<long, string> collections)
    {
        LibraryId = tree.LibraryId;
        CatalogRevision = tree.CatalogRevision;
        _nodes = tree._nodes;
        _children = tree._children;
        _descendantArchives = tree._descendantArchives;
        Roots = tree.Roots;
        Authors = authors;
        Collections = collections;
    }

    private LibraryTreeSnapshot(long libraryId, long catalogRevision, List<Node> nodes, ProviderAuthorSet? authors,
        IReadOnlyDictionary<long, string>? collections = null)
    {
        LibraryId = libraryId;
        CatalogRevision = catalogRevision;
        Authors = authors ?? ProviderAuthorSet.None;
        Collections = collections ?? NoCollections;
        _descendantArchives = [];
        _nodes = nodes.ToDictionary(n => n.Id);
        _children = [];
        foreach (var n in nodes)
        {
            if (n.ParentId is not { } parent || !_nodes.ContainsKey(parent))
                continue;
            if (!_children.TryGetValue(parent, out var list))
                _children[parent] = list = [];
            list.Add(n);
        }
        foreach (var list in _children.Values)
            list.Sort((a, b) => string.CompareOrdinal(a.SortKey, b.SortKey));
        Roots = nodes.Where(n => n.ParentId is null || !_nodes.ContainsKey(n.ParentId.Value))
            .OrderBy(n => n.SortKey, StringComparer.Ordinal)
            .ToList();
    }

    public long LibraryId { get; }
    public long CatalogRevision { get; }
    public IReadOnlyList<Node> Roots { get; }
    public int Count => _nodes.Count;

    /// <summary>The provider authors this snapshot hands to the detector (<see cref="ProviderAuthorSet.None"/> when not loaded).</summary>
    public ProviderAuthorSet Authors { get; }

    private static readonly IReadOnlyDictionary<long, string> NoCollections = new Dictionary<long, string>();

    /// <summary>
    /// 1.34.0: the library's "Collection about" folders and the title of the series each is about (from the local record). Feeds
    /// <see cref="FolderShape.IsCollection"/> and <see cref="FolderShape.CollectionSeries"/>.
    /// </summary>
    public IReadOnlyDictionary<long, string> Collections { get; }

    /// <summary>
    /// Loads the tree; with <paramref name="providerAuthors"/> also the library's <see cref="ProviderAuthorSet"/>
    /// (the automatic-matching paths that classify folders; other readers only need the tree).
    /// </summary>
    public static async Task<LibraryTreeSnapshot> LoadAsync(MangaPixerDbContext db, long libraryId, CancellationToken ct, bool providerAuthors = false)
    {
        var revision = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.CatalogRevision).FirstOrDefaultAsync(ct);
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var rows = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.Availability != tombstoned)
            .Select(n => new Node(n.Id, n.ParentId, n.Kind == folder, n.DisplayName, n.SortKey))
            .ToListAsync(ct);
        var authors = providerAuthors ? await LoadProviderAuthorsAsync(db, libraryId, ct) : null;
        return new LibraryTreeSnapshot(libraryId, revision, rows, authors, await LoadCollectionsAsync(db, libraryId, ct));
    }

    /// <summary>The same tree with another author set (a link changed, the catalog did not).</summary>
    public LibraryTreeSnapshot WithAuthors(ProviderAuthorSet authors) => new(this, authors, Collections);

    /// <summary>The same tree with another collection map (a "Collection about" row changed, the catalog did not).</summary>
    public LibraryTreeSnapshot WithCollections(IReadOnlyDictionary<long, string> collections) => new(this, Authors, collections);

    /// <summary>True when <paramref name="other"/> names the same collection folders with the same series titles.</summary>
    public bool SameCollections(IReadOnlyDictionary<long, string> other) =>
        other.Count == Collections.Count
        && other.All(kv => Collections.TryGetValue(kv.Key, out var title) && string.Equals(title, kv.Value, StringComparison.Ordinal));

    /// <summary>1.34.0: the library's "Collection about" folders with their series title (one indexed query on (LibraryId, State)).</summary>
    public static async Task<IReadOnlyDictionary<long, string>> LoadCollectionsAsync(MangaPixerDbContext db, long libraryId, CancellationToken ct)
    {
        var collection = (int)SeriesLinkState.CollectionAbout;
        return await db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.LibraryId == libraryId && l.State == collection && l.RecordId != null)
            .Select(l => new { l.NodeId, l.Record!.Title })
            .ToDictionaryAsync(l => l.NodeId, l => l.Title, ct);
    }

    private static IQueryable<NodeSeriesLinkEntity> AuthorLinks(MangaPixerDbContext db, long libraryId)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        return db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.LibraryId == libraryId && (l.State == confirmed || l.State == auto) && l.RecordId != null);
    }

    /// <summary>Three scalar reads (count, newest link change, newest record fetch) - no rows are loaded.</summary>
    public static async Task<LinkStamp> LinkStampAsync(MangaPixerDbContext db, long libraryId, CancellationToken ct)
    {
        var links = AuthorLinks(db, libraryId);
        var count = await links.CountAsync(ct);
        if (count == 0)
            return default;
        var updated = await links.MaxAsync(l => (DateTimeOffset?)l.UpdatedAt, ct);
        var fetched = await links.Select(l => (DateTimeOffset?)l.Record!.FetchedAt).MaxAsync(ct);
        return new LinkStamp(count, updated, fetched);
    }

    /// <summary>
    /// The author / artist names of the records linked in the library (parsed from the stored
    /// <c>CreatorsJson</c>; nothing is requested from a provider), de-duplicated, and the linked node ids.
    /// </summary>
    public static async Task<ProviderAuthorSet> LoadProviderAuthorsAsync(MangaPixerDbContext db, long libraryId, CancellationToken ct)
    {
        var stamp = await LinkStampAsync(db, libraryId, ct);
        if (stamp.Links == 0)
            return ProviderAuthorSet.None;
        var rows = await AuthorLinks(db, libraryId)
            .Select(l => new { l.NodeId, l.RecordId, l.Record!.CreatorsJson })
            .ToListAsync(ct);
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var json in rows.DistinctBy(r => r.RecordId).Select(r => r.CreatorsJson))
        {
            foreach (var creator in MetadataJson.ReadList<MetadataJson.Creator>(json))
            {
                if (creator.Role is not ("author" or "artist") || string.IsNullOrWhiteSpace(creator.Name))
                    continue;
                if (seen.Add(TitleNormalizer.ScoringForm(creator.Name)))
                    names.Add(creator.Name);
            }
        }
        return new ProviderAuthorSet(names, rows.Select(r => r.NodeId).ToHashSet(), stamp);
    }

    /// <summary>For tests: a snapshot from in-memory rows.</summary>
    public static LibraryTreeSnapshot FromNodes(long libraryId, IEnumerable<Node> nodes, ProviderAuthorSet? authors = null,
        IReadOnlyDictionary<long, string>? collections = null) =>
        new(libraryId, 0, nodes.ToList(), authors, collections);

    public Node? Find(long id) => _nodes.GetValueOrDefault(id);

    public IReadOnlyList<Node> ChildrenOf(long id) => _children.TryGetValue(id, out var list) ? list : [];

    public IEnumerable<Node> ChildFolders(long id) => ChildrenOf(id).Where(c => c.IsFolder);

    public IReadOnlyList<Node> ChildArchives(long id) => ChildrenOf(id).Where(c => !c.IsFolder).ToList();

    /// <summary>Ancestors, nearest first (bounded).</summary>
    public IEnumerable<Node> Ancestors(long id)
    {
        var current = Find(id);
        for (var i = 0; current?.ParentId is { } parent && i < SeriesInfoResolver.MaxWalkDepth; i++)
        {
            current = Find(parent);
            if (current is null)
                yield break;
            yield return current;
        }
    }

    public int Depth(long id) => Ancestors(id).Count() + 1;

    /// <summary>Live archives anywhere below a folder.</summary>
    public int DescendantArchiveCount(long id)
    {
        if (_descendantArchives.TryGetValue(id, out var cached))
            return cached;
        // Iterative post-order so a deep tree cannot overflow the stack.
        var stack = new Stack<(long Id, bool Expanded)>();
        stack.Push((id, false));
        while (stack.Count > 0)
        {
            var (current, expanded) = stack.Pop();
            if (_descendantArchives.ContainsKey(current))
                continue;
            if (!expanded)
            {
                stack.Push((current, true));
                foreach (var child in ChildFolders(current))
                    if (!_descendantArchives.ContainsKey(child.Id))
                        stack.Push((child.Id, false));
                continue;
            }
            var count = 0;
            foreach (var child in ChildrenOf(current))
                count += child.IsFolder ? _descendantArchives.GetValueOrDefault(child.Id) : 1;
            _descendantArchives[current] = count;
        }
        return _descendantArchives[id];
    }

    /// <summary>At most this many archive names of one unit subfolder reach the count rule (1.29.0).</summary>
    public const int MaxUnitArchiveNames = 500;

    /// <summary>Display names of the live archives below a folder, depth first in catalog order, at most <paramref name="max"/>.</summary>
    public IReadOnlyList<string> DescendantArchiveNames(long id, int max)
    {
        var names = new List<string>();
        var stack = new Stack<long>();
        stack.Push(id);
        while (stack.Count > 0 && names.Count < max)
        {
            var children = ChildrenOf(stack.Pop());
            foreach (var child in children)
            {
                if (child.IsFolder)
                    continue;
                if (names.Count >= max)
                    break;
                names.Add(child.Name);
            }
            for (var i = children.Count - 1; i >= 0; i--)
                if (children[i].IsFolder)
                    stack.Push(children[i].Id);
        }
        return names;
    }

    /// <summary>
    /// The detector's view of a folder: display names only, direct archives in
    /// catalog order (<see cref="FolderShape.ArchiveNames"/> indexes map onto
    /// <see cref="ChildArchives"/>), direct subfolders with their archive counts (unit subfolders also with their
    /// archive names, for the count rule), and
    /// the library's provider authors unless the folder carries a link itself, and (1.34.0) whether it is a "Collection about" folder and
    /// the series title of the nearest one (itself or an ancestor).
    /// </summary>
    public FolderShape ShapeOf(long folderId)
    {
        var folder = Find(folderId) ?? throw new ArgumentException("Unknown folder.", nameof(folderId));
        var parent = folder.ParentId is { } p ? Find(p) : null;
        var authors = Authors.Names.Count > 0 && !Authors.LinkedNodeIds.Contains(folderId) ? Authors.Names : null;
        return new FolderShape(
            folder.Name,
            Depth(folderId),
            ChildArchives(folderId).Select(a => a.Name).ToList(),
            ChildFolders(folderId).Select(c => new ChildFolderShape(c.Name, DescendantArchiveCount(c.Id),
                AutoMatchText.IsUnitFolderName(c.Name) ? DescendantArchiveNames(c.Id, MaxUnitArchiveNames) : null)).ToList(),
            parent?.Name,
            CategoryHint(folderId),
            authors,
            Collections.ContainsKey(folderId),
            CollectionSeriesOf(folderId));
    }

    /// <summary>The series title of the nearest "Collection about" folder: the folder itself, else its nearest ancestor; null when none.</summary>
    public string? CollectionSeriesOf(long folderId)
    {
        if (Collections.Count == 0)
            return null;
        if (Collections.TryGetValue(folderId, out var own))
            return own;
        foreach (var a in Ancestors(folderId))
            if (Collections.TryGetValue(a.Id, out var title))
                return title;
        return null;
    }

    /// <summary>
    /// The nearest ancestor named exactly like a category (<see cref="AutoMatchText.CategoryFolderWords"/>, e.g.
    /// <c>manhwa</c>), lower-cased; null when none. The library root is not a node, so it is never read.
    /// </summary>
    public string? CategoryHint(long folderId)
    {
        foreach (var node in Ancestors(folderId))
        {
            if (AutoMatchText.IsCategoryFolderName(node.Name))
                return node.Name.Trim().ToLowerInvariant();
        }
        return null;
    }
}
