namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// Favorites stacking (1.27.0): turns a user's visible favorites into the entries the
/// Favorites page and the Home Favorites row show. Starred ARCHIVES that share a direct
/// parent folder collapse into one stack entry for that folder once at least
/// <see cref="MinimumStackSize"/> of them are starred; every other favorite (a lone
/// archive, an archive at a library root, any starred folder) stays its own entry.
///
/// Recently favorited (the default): each entry is ordered by its representative favorite - its own, or a stack's NEWEST
/// member - newest first, ties broken by the favorite id. The keyset cursor is that
/// representative's (time, id), so a stack appears exactly once, at its newest
/// favorite's place, and never splits across pages. Pure and in-memory: the caller
/// loads the narrow key rows in one query.
///
/// 1.50.0 lane F - the other sorts (<see cref="Order"/>), each a TOTAL order with its own keyset position:
/// <list type="bullet">
/// <item>Name (A-Z / Z-A): the entry's <c>SortKey</c> (a stack's is its folder's), then the entry discriminator (a single before a
/// stack), then the node id. <c>SortKey</c> is unique only among siblings, and a starred folder and the stack of its own starred
/// children share one node and one key - so the name position is the triple, never the raw key (browse's raw-key cursor would skip or
/// repeat rows here). Z-A reverses the whole triple.</item>
/// <item>Recently added: the node's own <c>CreatedAt</c>; a stack's is its NEWEST member's (owner decision 8). Newest first.</item>
/// <item>Recently updated: a folder's <c>LatestDescendantAddedAt</c> (browse's rule; null = no archive yet, last), an archive's own
/// <c>CreatedAt</c>, a stack's newest member's <c>CreatedAt</c>. Newest first.</item>
/// </list>
/// <item>My rating (1.52.0 lane R): the viewer's own score of the entry's node - a stack's is its FOLDER's (a stack is a view of the
/// folder, not a node of its own). Highest first, then by name; not rated last, by name.</item>
/// <item>Recently read (1.51.0 lane F, owner decision 2): the newest reading activity of the viewer (progress or read mark) - an archive's
/// own, a starred folder's over its whole subtree (browse's rule), a stack's most recently read STARRED member. Never read = last, by name.
/// Newest first.</item>
/// Both date sorts break ties by the name triple. Under Name the catalog key's kind prefix puts folders (and stacks) before archives,
/// as in browse (Z-A reverses that too, as browse's full reversal); under the date sorts folders and archives interleave (the favorites
/// list is one flat list across libraries, not a folder level).
/// </summary>
internal static class FavoriteStacks
{
    /// <summary>Starred archives in one folder needed to form a stack.</summary>
    public const int MinimumStackSize = 2;

    /// <summary>The favorites sorts (1.50.0 lane F).</summary>
    public enum Order
    {
        Favorited,
        NameAscending,
        NameDescending,
        RecentlyAdded,
        RecentlyUpdated,

        /// <summary>1.51.0 lane F: the viewer's newest reading activity, newest first; never read last by name.</summary>
        RecentlyRead,

        /// <summary>1.52.0 lane R: the viewer's own rating, highest first, then by name; not rated last, by name.</summary>
        MyRating,
    }

    /// <summary>
    /// One visible favorite: the favorite row's keys plus its node's parent and kind. 1.50.0: plus what the sorts and filters read - the
    /// node's <paramref name="SortKey"/>, its parent folder's (<paramref name="ParentSortKey"/>, a stack's name), its
    /// <paramref name="AddedAt"/> (<c>CreatedAt</c>), a folder's <paramref name="LatestDescendantAddedAt"/> and its
    /// <paramref name="LibraryId"/>. 1.51.0: plus the viewer's newest reading activity on the node (<paramref name="ReadStamp"/>, the stored
    /// order-preserving value; null = never read), filled only for the Recently read sort.
    /// </summary>
    public readonly record struct Key(
        long FavoriteId,
        DateTimeOffset FavoritedAt,
        long NodeId,
        long? ParentId,
        int Kind,
        string SortKey = "",
        string ParentSortKey = "",
        DateTimeOffset? AddedAt = null,
        DateTimeOffset? LatestDescendantAddedAt = null,
        long LibraryId = 0,
        long? ReadStamp = null);

    /// <summary>
    /// One card: <see cref="NodeId"/> is the favorite's node, or for a stack the shared
    /// parent folder; <see cref="StackCount"/> is null for a single favorite. The
    /// representative (time, id) is the recently-favorited keyset position; <see cref="SortKey"/>, <see cref="AddedTicks"/> and
    /// <see cref="UpdatedTicks"/> are the other sorts' (1.50.0). <see cref="Kind"/> is the node's (a stack's: a folder). 1.51.0:
    /// <see cref="LibraryId"/> (the group of <c>group=library</c>; a stack never spans libraries - one parent folder) and
    /// <see cref="ReadStamp"/> (Recently read).
    /// </summary>
    public readonly record struct Entry(
        long NodeId,
        int? StackCount,
        long RepresentativeTicks,
        long RepresentativeId,
        string SortKey = "",
        long? AddedTicks = null,
        long? UpdatedTicks = null,
        int Kind = (int)CatalogNodeKind.Archive,
        long LibraryId = 0,
        long? ReadStamp = null,
        int? Rating = null)
    {
        /// <summary>The entry discriminator of the name position: 0 = a single favorite, 1 = a stack (which shares its folder's node).</summary>
        public int Discriminator => StackCount is null ? 0 : 1;

        /// <summary>A stack of starred archives (1.27.0).</summary>
        public bool IsStack => StackCount is not null;
    }

    /// <summary>Groups the keys into entries, newest representative first.</summary>
    public static List<Entry> Group(IEnumerable<Key> keys) => Group(keys, Order.Favorited);

    /// <summary>Groups the keys into entries, sorted by <paramref name="order"/>.</summary>
    public static List<Entry> Group(IEnumerable<Key> keys, Order order)
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
            // A stack sits at its newest member under the date sorts (owner decision 8): its members are archives, so both date sorts
            // read their CreatedAt.
            var newestAdded = members.Max(m => m.AddedAt?.UtcTicks);
            // 1.51.0: Recently read - the stack's most recently read starred member (null when none was read).
            var newestRead = members.Max(m => m.ReadStamp);
            entries.Add(new Entry(parent, members.Count, newest.FavoritedAt.UtcTicks, newest.FavoriteId,
                SortKey: members[0].ParentSortKey, AddedTicks: newestAdded, UpdatedTicks: newestAdded, Kind: (int)CatalogNodeKind.Folder,
                LibraryId: members[0].LibraryId, ReadStamp: newestRead));
        }

        Sort(entries, order);
        return entries;
    }

    /// <summary>Sorts <paramref name="entries"/> in place by <paramref name="order"/>.</summary>
    public static void Sort(List<Entry> entries, Order order) => entries.Sort((a, b) => Compare(a, b, order));

    /// <summary>
    /// 1.51.0 lane F (group by library): sorts in place by the library's rank (<paramref name="libraryRank"/>: the picker order), then by
    /// <paramref name="order"/>. Null rank = ungrouped.
    /// </summary>
    public static void Sort(List<Entry> entries, Order order, IReadOnlyDictionary<long, int>? libraryRank)
        => entries.Sort((a, b) => Compare(a, b, order, libraryRank));

    /// <summary>Entries strictly after the cursor position in the newest-first order.</summary>
    public static IEnumerable<Entry> After(IEnumerable<Entry> ordered, long cursorTicks, long cursorId)
        => ordered.Where(e => e.RepresentativeTicks < cursorTicks
            || (e.RepresentativeTicks == cursorTicks && e.RepresentativeId < cursorId));

    /// <summary>
    /// Entries strictly after <paramref name="position"/> in <paramref name="order"/> (1.50.0). The position is a probe entry carrying the
    /// fields that order reads (<see cref="PositionOf"/>), so an entry that vanished between two pages still resumes at the right place.
    /// </summary>
    public static IEnumerable<Entry> After(IEnumerable<Entry> ordered, Order order, Entry position)
        => ordered.Where(e => Compare(e, position, order) > 0);

    /// <summary>
    /// 1.51.0: entries strictly after <paramref name="position"/> in the grouped (or, with a null rank, plain) order.
    /// </summary>
    public static IEnumerable<Entry> After(IEnumerable<Entry> ordered, Order order, Entry position, IReadOnlyDictionary<long, int>? libraryRank)
        => ordered.Where(e => Compare(e, position, order, libraryRank) > 0);

    /// <summary>The fields of <paramref name="entry"/> that <paramref name="order"/>'s keyset position needs (the rest zeroed; 1.51.0: the
    /// library is kept - the grouped order reads it).</summary>
    public static Entry PositionOf(Entry entry, Order order) => order switch
    {
        Order.Favorited => new Entry(0, null, entry.RepresentativeTicks, entry.RepresentativeId, LibraryId: entry.LibraryId),
        Order.NameAscending or Order.NameDescending => new Entry(entry.NodeId, entry.StackCount, 0, 0, entry.SortKey, LibraryId: entry.LibraryId),
        Order.RecentlyAdded => new Entry(entry.NodeId, entry.StackCount, 0, 0, entry.SortKey, AddedTicks: entry.AddedTicks, LibraryId: entry.LibraryId),
        Order.RecentlyRead => new Entry(entry.NodeId, entry.StackCount, 0, 0, entry.SortKey, LibraryId: entry.LibraryId, ReadStamp: entry.ReadStamp),
        Order.MyRating => new Entry(entry.NodeId, entry.StackCount, 0, 0, entry.SortKey, LibraryId: entry.LibraryId, Rating: entry.Rating),
        _ => new Entry(entry.NodeId, entry.StackCount, 0, 0, entry.SortKey, UpdatedTicks: entry.UpdatedTicks, LibraryId: entry.LibraryId),
    };

    /// <summary>
    /// 1.51.0 lane F: the grouped total order - the library's rank first (a library missing from <paramref name="libraryRank"/> after every
    /// ranked one, by id), then <paramref name="order"/>. A null rank is the plain order.
    /// </summary>
    public static int Compare(Entry a, Entry b, Order order, IReadOnlyDictionary<long, int>? libraryRank)
    {
        if (libraryRank is not null && a.LibraryId != b.LibraryId)
        {
            var ra = libraryRank.TryGetValue(a.LibraryId, out var x) ? x : int.MaxValue;
            var rb = libraryRank.TryGetValue(b.LibraryId, out var y) ? y : int.MaxValue;
            return ra != rb ? ra.CompareTo(rb) : a.LibraryId.CompareTo(b.LibraryId);
        }
        return Compare(a, b, order);
    }

    /// <summary>The total order of <paramref name="order"/>: negative when <paramref name="a"/> comes first.</summary>
    public static int Compare(Entry a, Entry b, Order order)
    {
        switch (order)
        {
            case Order.Favorited:
                return a.RepresentativeTicks != b.RepresentativeTicks
                    ? b.RepresentativeTicks.CompareTo(a.RepresentativeTicks)
                    : b.RepresentativeId.CompareTo(a.RepresentativeId);
            case Order.NameAscending:
                return CompareName(a, b);
            case Order.NameDescending:
                return CompareName(b, a);
            case Order.RecentlyAdded:
                return ThenByName(CompareNewestFirst(a.AddedTicks, b.AddedTicks), a, b);
            case Order.RecentlyRead:
                return ThenByName(CompareNewestFirst(a.ReadStamp, b.ReadStamp), a, b);
            case Order.MyRating:
                // 1.52.0 lane R: highest first, not rated last - the same "largest first, missing last" rule as the date sorts.
                return ThenByName(CompareNewestFirst(a.Rating, b.Rating), a, b);
            default:
                return ThenByName(CompareNewestFirst(a.UpdatedTicks, b.UpdatedTicks), a, b);
        }
    }

    private static int ThenByName(int byDate, Entry a, Entry b) => byDate != 0 ? byDate : CompareName(a, b);

    /// <summary>Name order: SortKey (ordinal, as the catalog's keys are built for), then single before stack, then node id.</summary>
    private static int CompareName(Entry a, Entry b)
    {
        var byKey = string.CompareOrdinal(a.SortKey, b.SortKey);
        if (byKey != 0)
            return byKey;
        if (a.Discriminator != b.Discriminator)
            return a.Discriminator.CompareTo(b.Discriminator);
        return a.NodeId.CompareTo(b.NodeId);
    }

    /// <summary>Newest first; a missing value (a folder with no archive yet) after every dated one.</summary>
    private static int CompareNewestFirst(long? a, long? b)
    {
        if (a == b)
            return 0;
        if (a is null)
            return 1;
        if (b is null)
            return -1;
        return b.Value.CompareTo(a.Value);
    }

    private static Entry Single(Key k)
    {
        var added = k.AddedAt?.UtcTicks;
        var updated = k.Kind == (int)CatalogNodeKind.Folder ? k.LatestDescendantAddedAt?.UtcTicks : added;
        return new(k.NodeId, null, k.FavoritedAt.UtcTicks, k.FavoriteId, k.SortKey, added, updated, k.Kind, k.LibraryId, k.ReadStamp);
    }
}
