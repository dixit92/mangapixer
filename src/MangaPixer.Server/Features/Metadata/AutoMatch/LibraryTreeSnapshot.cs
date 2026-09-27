namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The live (non-tombstoned) catalog tree of one library, loaded with ONE projected
/// query (id, parent, kind, display name, sort key - no paths), from which the
/// auto-match plumbing builds the detector's <see cref="FolderShape"/>s. Pure reads;
/// never touches the filesystem. Top-level folders have depth 1 (the library root
/// is not a node).
/// </summary>
public sealed class LibraryTreeSnapshot
{
    public sealed record Node(long Id, long? ParentId, bool IsFolder, string Name, string SortKey);

    private readonly Dictionary<long, Node> _nodes;
    private readonly Dictionary<long, List<Node>> _children;
    private readonly Dictionary<long, int> _descendantArchives = [];

    private LibraryTreeSnapshot(long libraryId, long catalogRevision, List<Node> nodes)
    {
        LibraryId = libraryId;
        CatalogRevision = catalogRevision;
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

    public static async Task<LibraryTreeSnapshot> LoadAsync(MangaPixerDbContext db, long libraryId, CancellationToken ct)
    {
        var revision = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.CatalogRevision).FirstOrDefaultAsync(ct);
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var rows = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.Availability != tombstoned)
            .Select(n => new Node(n.Id, n.ParentId, n.Kind == folder, n.DisplayName, n.SortKey))
            .ToListAsync(ct);
        return new LibraryTreeSnapshot(libraryId, revision, rows);
    }

    /// <summary>For tests: a snapshot from in-memory rows.</summary>
    public static LibraryTreeSnapshot FromNodes(long libraryId, IEnumerable<Node> nodes) => new(libraryId, 0, nodes.ToList());

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

    /// <summary>
    /// The detector's view of a folder: display names only, direct archives in
    /// catalog order (<see cref="FolderShape.ArchiveNames"/> indexes map onto
    /// <see cref="ChildArchives"/>), direct subfolders with their archive counts.
    /// </summary>
    public FolderShape ShapeOf(long folderId)
    {
        var folder = Find(folderId) ?? throw new ArgumentException("Unknown folder.", nameof(folderId));
        var parent = folder.ParentId is { } p ? Find(p) : null;
        return new FolderShape(
            folder.Name,
            Depth(folderId),
            ChildArchives(folderId).Select(a => a.Name).ToList(),
            ChildFolders(folderId).Select(c => new ChildFolderShape(c.Name, DescendantArchiveCount(c.Id))).ToList(),
            parent?.Name,
            CategoryHint(folderId));
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
