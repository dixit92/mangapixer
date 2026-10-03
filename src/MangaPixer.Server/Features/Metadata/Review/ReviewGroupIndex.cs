namespace com.lifepixer.mangapixer.Server.Features.Metadata.Review;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// The "Same author" and "Same folder" groups of the works waiting in Needs review (1.33.0), built from local names at read
/// time (nothing stored, nothing sent). Two works are by the same author when any of their names share a key
/// (<see cref="ReviewAuthorNames"/>: a circle and its artist are both names, so <c>[Circle (Artist)]</c> joins the works of
/// <c>[Circle]</c> and of <c>[Artist]</c>); the groups are the connected works, so every work is in at most one group.
/// Same folder = the same parent folder. Pure once built.
/// </summary>
internal sealed class ReviewGroupIndex
{
    /// <summary>A waiting work: its author names (may be empty), its parent folder, and whether it is set aside.</summary>
    public sealed record Work(long NodeId, IReadOnlyList<ReviewAuthorNames.Name> Names, long? ParentId, bool Later);

    /// <summary>An author group: the filter key (the shown name's key), the name to show, the members, how many are Later.</summary>
    public sealed record AuthorGroup(string Key, string Label, IReadOnlyList<long> NodeIds, int Later);

    private readonly Dictionary<long, AuthorGroup> _byNode = [];
    private readonly Dictionary<string, AuthorGroup> _byKey = new(StringComparer.Ordinal);
    private readonly Dictionary<long, int> _perFolder = [];

    public IReadOnlyList<AuthorGroup> Groups { get; }

    public ReviewGroupIndex(IReadOnlyList<Work> works)
    {
        // Union-find over the works: a work joins every work that shares one of its keys (either kind).
        var parent = Enumerable.Range(0, works.Count).ToArray();
        int Root(int i) => parent[i] == i ? i : parent[i] = Root(parent[i]);
        var firstWithKey = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < works.Count; i++)
        {
            foreach (var name in works[i].Names)
            {
                foreach (var key in new[] { "k:" + name.Key, "o:" + name.OrderKey })
                {
                    if (firstWithKey.TryGetValue(key, out var other))
                        parent[Root(i)] = Root(other);
                    else
                        firstWithKey[key] = i;
                }
            }
            if (works[i].ParentId is { } folder)
                _perFolder[folder] = _perFolder.GetValueOrDefault(folder) + 1;
        }

        var groups = new List<AuthorGroup>();
        foreach (var members in Enumerable.Range(0, works.Count).Where(i => works[i].Names.Count > 0).GroupBy(Root))
        {
            var names = members.SelectMany(i => works[i].Names).ToList();
            // The most frequent spelling (case-insensitive), then the shortest, then ordinal - deterministic.
            var label = names.GroupBy(n => n.Label, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key.Length).ThenBy(g => g.Key, StringComparer.Ordinal)
                .First().Key;
            var group = new AuthorGroup(
                names.First(n => n.Label.Equals(label, StringComparison.OrdinalIgnoreCase)).Key, // the shown name's own key
                label,
                members.Select(i => works[i].NodeId).ToList(),
                members.Count(i => works[i].Later));
            groups.Add(group);
            foreach (var i in members)
                _byNode[works[i].NodeId] = group;
            foreach (var name in names)
            {
                _byKey.TryAdd(name.Key, group);
                _byKey.TryAdd(name.OrderKey, group);
            }
        }
        Groups = groups;
    }

    /// <summary>The author group of a work, or null when its names give none.</summary>
    public AuthorGroup? AuthorOf(long nodeId) => _byNode.GetValueOrDefault(nodeId);

    /// <summary>The group a filter key names (its own key, or any key of a member's name); null when none.</summary>
    public AuthorGroup? Find(string? key) => string.IsNullOrEmpty(key) ? null : _byKey.GetValueOrDefault(key);

    /// <summary>Waiting works in a folder.</summary>
    public int InFolder(long folderId) => _perFolder.GetValueOrDefault(folderId);
}
