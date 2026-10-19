namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Facets;

/// <summary>
/// A synthetic library for the pure index tests: folders and archives with increasing ids (children in insertion order), link rows,
/// ComicInfo and declared facts; folders are classified by the real <see cref="WorkDetector"/> over a shape built from the tree (names
/// and counts, as the server's tree snapshot gives it). Synthetic names only.
/// </summary>
public sealed class FacetTestLibrary
{
    private readonly List<FacetNode> _nodes = [];
    private readonly Dictionary<long, FacetLink> _links = [];
    private readonly List<FacetDeclaredFact> _declared = [];
    private readonly WorkDetector _detector = new();
    private long _next;

    public FacetTestLibrary(long firstId = 1) => _next = firstId;

    public Dictionary<long, FacetComicInfo> ComicInfo { get; } = [];
    public Dictionary<long, string> RecordTitles { get; } = [];

    public long Folder(string name, long? parent = null)
    {
        var id = _next++;
        _nodes.Add(new FacetNode(id, parent, true, name));
        return id;
    }

    public long Archive(string name, long? parent)
    {
        var id = _next++;
        _nodes.Add(new FacetNode(id, parent, false, name));
        return id;
    }

    public long[] Archives(long parent, string format, int count) =>
        Enumerable.Range(1, count)
            .Select(i => Archive(string.Format(System.Globalization.CultureInfo.InvariantCulture, format, i), parent))
            .ToArray();

    public void Link(long nodeId, SeriesLinkState state, long? recordId = null, string? title = null)
    {
        _links[nodeId] = new FacetLink(state, recordId);
        if (recordId is { } r && title is not null)
            RecordTitles[r] = title;
    }

    public void Ci(long archiveId, string? series, params FacetCredit[] creators) =>
        ComicInfo[archiveId] = new FacetComicInfo(archiveId, series, creators, [], null, null, null);

    public void Declare(long? nodeId, string key, string value, string? role = null, int position = 0, string? externalRef = null) =>
        _declared.Add(new FacetDeclaredFact(nodeId, key, value, role, position, externalRef));

    public FacetTree Tree() => new(_nodes, _links, _declared);

    public WorkClassification Classify(FacetTree tree, long folderId)
    {
        var folder = tree.Find(folderId)!;
        var depth = 1;
        for (var p = folder.ParentId; p is { } id && tree.Find(id) is { } parent; p = parent.ParentId)
            depth++;
        var children = tree.ChildrenOf(folderId);
        var link = tree.OwnLink(folderId);
        var shape = new FolderShape(
            folder.Name,
            depth,
            children.Where(c => !c.IsFolder).Select(c => c.Name).ToList(),
            children.Where(c => c.IsFolder).Select(c => new ChildFolderShape(c.Name, DescendantArchives(tree, c.Id))).ToList(),
            folder.ParentId is { } pid ? tree.Find(pid)?.Name : null,
            IsCollection: link?.State == SeriesLinkState.CollectionAbout,
            IsMarkedArtistFolder: link?.State == SeriesLinkState.ArtistFolder);
        return _detector.Classify(shape);
    }

    private static int DescendantArchives(FacetTree tree, long folderId) =>
        tree.ChildrenOf(folderId).Sum(c => c.IsFolder ? DescendantArchives(tree, c.Id) : 1);

    /// <summary>Resolves the works with the given creator resolution for ComicInfo credits (none: no creators, so no ComicInfo keys).</summary>
    public IReadOnlyList<FacetResolvedWork> Works(CreatorResolver? creators = null, IReadOnlyList<FacetComicInfoWorkKey>? others = null)
    {
        var tree = Tree();
        var counter = 1000L;
        var made = new Dictionary<CreatorHandle, long>();
        long IdOf(CreatorHandle h) => h.IsNew ? (made.TryGetValue(h, out var id) ? id : made[h] = ++counter) : h.Id;
        IReadOnlyCollection<long> CreatorsOf(long archiveId)
        {
            if (creators is null || !ComicInfo.TryGetValue(archiveId, out var ci))
                return [];
            var context = tree.EffectiveDeclared(archiveId).Creators
                .Select(f => f.ExternalRef).OfType<string>().ToList();
            return ci.Creators
                .Where(c => FacetText.RolesOf(c.Role) is not null)
                .Select(c => creators.Resolve(c, context))
                .OfType<CreatorHandle>()
                .Select(IdOf)
                .ToHashSet();
        }
        return WorkResolver.Resolve(new WorkResolverInput(tree, id => Classify(tree, id), ComicInfo, RecordTitles, CreatorsOf, others));
    }
}
