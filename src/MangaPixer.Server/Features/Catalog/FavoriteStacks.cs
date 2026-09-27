namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// Favorites stacking (1.27.0): turns a user's visible favorites into the entries the
/// Favorites page and the Home Favorites row show. Starred ARCHIVES that share a direct
/// parent folder collapse into one stack entry for that folder once at least
/// <see cref="MinimumStackSize"/> of them are starred; every other favorite (a lone
/// archive, an archive at a library root, any starred folder) stays its own entry.
///
/// Each entry is ordered by its representative favorite - its own, or a stack's NEWEST
/// member - newest first, ties broken by the favorite id. The keyset cursor is that
/// representative's (time, id), so a stack appears exactly once, at its newest
/// favorite's place, and never splits across pages. Pure and in-memory: the caller
/// loads the narrow key rows in one query.
/// </summary>
internal static class FavoriteStacks
{
    /// <summary>Starred archives in one folder needed to form a stack.</summary>
    public const int MinimumStackSize = 2;

    /// <summary>One visible favorite: the favorite row's keys plus its node's parent and kind.</summary>
    public readonly record struct Key(long FavoriteId, DateTimeOffset FavoritedAt, long NodeId, long? ParentId, int Kind);

    /// <summary>
    /// One card: <see cref="NodeId"/> is the favorite's node, or for a stack the shared
    /// parent folder; <see cref="StackCount"/> is null for a single favorite. The
    /// representative (time, id) is the keyset position.
    /// </summary>
    public readonly record struct Entry(long NodeId, int? StackCount, long RepresentativeTicks, long RepresentativeId);

    /// <summary>Groups the keys into entries, newest representative first.</summary>
    public static List<Entry> Group(IEnumerable<Key> keys)
    {
        var entries = new List<Entry>();
        var byParent = new Dictionary<long, List<Key>>();
        foreach (var k in keys)
        {
            if (k.Kind == (int)CatalogNodeKind.Archive && k.ParentId is long parent)
            {
                if (!byParent.TryGetValue(parent, out var members))
                    byParent[parent] = members = [];
                members.Add(k);
            }
            else
            {
                entries.Add(Single(k));
            }
        }

        foreach (var (parent, members) in byParent)
        {
            if (members.Count < MinimumStackSize)
            {
                entries.AddRange(members.Select(Single));
                continue;
            }
            var newest = members
                .OrderByDescending(m => m.FavoritedAt.UtcTicks)
                .ThenByDescending(m => m.FavoriteId)
                .First();
            entries.Add(new Entry(parent, members.Count, newest.FavoritedAt.UtcTicks, newest.FavoriteId));
        }

        entries.Sort((a, b) => a.RepresentativeTicks != b.RepresentativeTicks
            ? b.RepresentativeTicks.CompareTo(a.RepresentativeTicks)
            : b.RepresentativeId.CompareTo(a.RepresentativeId));
        return entries;
    }

    /// <summary>Entries strictly after the cursor position in the newest-first order.</summary>
    public static IEnumerable<Entry> After(IEnumerable<Entry> ordered, long cursorTicks, long cursorId)
        => ordered.Where(e => e.RepresentativeTicks < cursorTicks
            || (e.RepresentativeTicks == cursorTicks && e.RepresentativeId < cursorId));

    private static Entry Single(Key k) => new(k.NodeId, null, k.FavoritedAt.UtcTicks, k.FavoriteId);
}
