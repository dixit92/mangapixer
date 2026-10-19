namespace com.lifepixer.mangapixer.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// One library's live tree with its link rows and declared facts, for the pure index code (1.45.0 lane F): the nearest-row walk the
/// series-information resolver uses (self, then ancestors; Needs review is skipped - <see cref="SeriesLinkStates.StopsInheritance"/>) and
/// the declared facts in effect at a node (nearest wins per key; a closer creator list replaces a farther one; the library's facts last).
/// </summary>
public sealed class FacetTree
{
    /// <summary>The longest ancestor walk (the resolver's bound).</summary>
    public const int MaxWalkDepth = 64;

    private readonly Dictionary<long, FacetNode> _nodes;
    private readonly Dictionary<long, List<FacetNode>> _children = [];
    private readonly IReadOnlyDictionary<long, FacetLink> _links;
    private readonly Dictionary<long, List<FacetDeclaredFact>> _declaredByNode = [];
    private readonly List<FacetDeclaredFact> _libraryFacts = [];

    public FacetTree(IEnumerable<FacetNode> nodes, IReadOnlyDictionary<long, FacetLink>? links = null,
        IEnumerable<FacetDeclaredFact>? declared = null)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var list = nodes.ToList();
        _nodes = new Dictionary<long, FacetNode>(list.Count);
        foreach (var n in list)
            _nodes[n.Id] = n;
        foreach (var n in list)
        {
            if (n.ParentId is { } parent && _nodes.ContainsKey(parent))
            {
                if (!_children.TryGetValue(parent, out var kids))
                    _children[parent] = kids = [];
                kids.Add(n);
            }
        }
        Roots = list.Where(n => n.ParentId is null || !_nodes.ContainsKey(n.ParentId.Value)).ToList();
        _links = links ?? new Dictionary<long, FacetLink>();
        foreach (var fact in declared ?? [])
        {
            if (fact.NodeId is { } nodeId)
            {
                if (!_declaredByNode.TryGetValue(nodeId, out var facts))
                    _declaredByNode[nodeId] = facts = [];
                facts.Add(fact);
            }
            else
            {
                _libraryFacts.Add(fact);
            }
        }
    }

    /// <summary>Top-level nodes (the library root is not a node), in the caller's order.</summary>
    public IReadOnlyList<FacetNode> Roots { get; }

    public int Count => _nodes.Count;

    public FacetNode? Find(long id) => _nodes.GetValueOrDefault(id);

    public IReadOnlyList<FacetNode> ChildrenOf(long id) => _children.TryGetValue(id, out var list) ? list : [];

    /// <summary>The node's own link row, or null.</summary>
    public FacetLink? OwnLink(long id) => _links.TryGetValue(id, out var link) ? link : null;

    /// <summary>The node's own row when it is a decision that stops the nearest-row walk (anything but Needs review), else null.</summary>
    public FacetLink? OwnStoppingLink(long id) =>
        _links.TryGetValue(id, out var link) && SeriesLinkStates.StopsInheritance(link.State) ? link : null;

    /// <summary>The record of the nearest stopping row at or above the node when that row is a linked series (Confirmed / Auto), else null.</summary>
    public long? NearestSeriesRecord(long nodeId)
    {
        var current = Find(nodeId);
        for (var depth = 0; current is not null && depth <= MaxWalkDepth; depth++)
        {
            if (OwnStoppingLink(current.Id) is { } link)
                return SeriesLinkStates.IsSeries(link.State) ? link.RecordId : null;
            current = current.ParentId is { } parent ? Find(parent) : null;
        }
        return null;
    }

    /// <summary>
    /// The declared facts in effect at a node (an archive reads its folder's): the nearest <c>type</c> row and the nearest non-empty
    /// <c>creator</c> list (a closer list replaces a farther one - lists are never merged), the library's rows last.
    /// </summary>
    public (FacetDeclaredFact? Type, IReadOnlyList<FacetDeclaredFact> Creators) EffectiveDeclared(long nodeId)
    {
        FacetDeclaredFact? type = null;
        IReadOnlyList<FacetDeclaredFact>? creators = null;
        var current = Find(nodeId);
        if (current is { IsFolder: false })
            current = current.ParentId is { } parent ? Find(parent) : null;
        for (var depth = 0; current is not null && depth <= MaxWalkDepth && (type is null || creators is null); depth++)
        {
            if (_declaredByNode.TryGetValue(current.Id, out var facts))
                Take(facts, ref type, ref creators);
            current = current.ParentId is { } parent ? Find(parent) : null;
        }
        if (type is null || creators is null)
            Take(_libraryFacts, ref type, ref creators);
        return (type, creators ?? []);
    }

    private static void Take(List<FacetDeclaredFact> facts, ref FacetDeclaredFact? type, ref IReadOnlyList<FacetDeclaredFact>? creators)
    {
        type ??= facts.FirstOrDefault(f => string.Equals(f.Key, DeclaredFactKeys.Type, StringComparison.Ordinal));
        if (creators is null)
        {
            var list = facts.Where(f => string.Equals(f.Key, DeclaredFactKeys.Creator, StringComparison.Ordinal))
                .OrderBy(f => f.Position)
                .ToList();
            if (list.Count > 0)
                creators = list;
        }
    }
}
