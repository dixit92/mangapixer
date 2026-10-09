namespace com.lifepixer.mangapixer.Core.Catalog;

// Stories collected in one volume (1.37.0, "tankoubon stacks"). PURE: the rows of one folder and the record each archive is linked to
// in, ordered browse entries out. No database, no clock, no path, no link state - the server decides which folders qualify (neither a
// series nor a collection) and which links count (Confirmed / Auto); this file only decides WHICH archives share one stack.

/// <summary>
/// Two or more archives of one folder that are each linked to the same collected-volume record: one stacked card. <see cref="Key"/> is
/// the record's opaque key (the server passes the record's public id); <see cref="Members"/> are in folder order (SortKey, then id).
/// </summary>
public sealed record StoryCollection(string Key, IReadOnlyList<GroupingRow> Members);

/// <summary>
/// The entries of a folder with its story collections: the collections as <see cref="VolumeEntryKind.CollectionStack"/> entries plus
/// everything else as <see cref="VolumeGrouping"/> lists it. <see cref="StackCount"/> counts the volume stacks only.
/// </summary>
public sealed record StoryGroupingResult(IReadOnlyList<VolumeEntry> Entries, int StackCount, int CollectionCount);

/// <summary>The pure grouping of stories collected in one volume.</summary>
public static class StoryCollectionGrouping
{
    /// <summary>A record forms a stack only with at least this many archives here; one archive alone stays a normal card.</summary>
    public const int MinStories = 2;

    /// <summary>The position id prefix of a collection entry (<c>cs:&lt;key&gt;</c>, the last tie-breaker of the position).</summary>
    public const string EntryIdPrefix = "cs:";

    /// <summary>
    /// Splits the rows into the story collections (archives only, at least <see cref="MinStories"/> per record) and the rest, both in
    /// folder order. <paramref name="recordKeyByRowId"/>: the record key of each archive that counts as linked (row id -> key); rows
    /// without an entry, and folders, are never collected.
    /// </summary>
    public static (IReadOnlyList<StoryCollection> Collections, IReadOnlyList<GroupingRow> Others) Split(
        IReadOnlyList<GroupingRow> rows, IReadOnlyDictionary<string, string> recordKeyByRowId)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(recordKeyByRowId);
        if (recordKeyByRowId.Count == 0)
            return ([], rows);

        var byKey = rows
            .Where(r => r.Kind == GroupingRowKind.Archive && recordKeyByRowId.TryGetValue(r.Id, out var key) && !string.IsNullOrEmpty(key))
            .GroupBy(r => recordKeyByRowId[r.Id], StringComparer.Ordinal)
            .Where(g => g.Count() >= MinStories)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.SortKey, StringComparer.Ordinal).ThenBy(r => r.Id, StringComparer.Ordinal).ToList(),
                StringComparer.Ordinal);
        if (byKey.Count == 0)
            return ([], rows);

        var collected = byKey.Values.SelectMany(m => m).Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
        var collections = byKey
            .Select(kv => new StoryCollection(kv.Key, kv.Value))
            .OrderBy(c => c.Members[0].SortKey, StringComparer.Ordinal)
            .ThenBy(c => c.Members[0].Id, StringComparer.Ordinal)
            .ToList();
        return (collections, rows.Where(r => !collected.Contains(r.Id)).ToList());
    }

    /// <summary>
    /// The entries of a folder that is neither a series nor a collection: each story collection becomes one entry among the loose
    /// archives (rank 2), placed by its record's title (owner, 2026-10-08: <paramref name="titleByKey"/>, sorted like an archive of that
    /// name) - or, without a title, in the place of its first member - and the other rows go through
    /// <see cref="VolumeGrouping.Group"/> unchanged - with NO volume map and no missing-volume placeholders (a collected volume's record
    /// is not this folder's series: its volume total would invent missing volumes). With no collection the result is exactly
    /// <c>VolumeGrouping.Group(rows, null)</c>.
    /// </summary>
    public static StoryGroupingResult Group(IReadOnlyList<GroupingRow> rows, IReadOnlyDictionary<string, string> recordKeyByRowId,
        IReadOnlyDictionary<string, string>? titleByKey = null)
    {
        var (collections, rest) = Split(rows, recordKeyByRowId);
        var volumes = VolumeGrouping.Group(rest, map: null, markMissingVolumes: false);
        if (collections.Count == 0)
            return new StoryGroupingResult(volumes.Entries, volumes.StackCount, 0);

        var entries = volumes.Entries.ToList();
        foreach (var collection in collections)
        {
            entries.Add(new VolumeEntry
            {
                Kind = VolumeEntryKind.CollectionStack,
                Rank = 2,
                VolumeKey = string.Empty,
                SortKey = titleByKey is not null && titleByKey.TryGetValue(collection.Key, out var title) && !string.IsNullOrWhiteSpace(title)
                    ? Ordering.SortKey.ForNode(CatalogNodeKind.Archive, title)
                    : collection.Members[0].SortKey,
                Id = EntryIdPrefix + collection.Key,
                Collection = collection,
            });
        }
        entries.Sort(VolumeGrouping.Compare);
        return new StoryGroupingResult(entries, volumes.StackCount, collections.Count);
    }

    /// <summary>The keys of the story collections in position order (previous / next of the collection view).</summary>
    public static IReadOnlyList<string> CollectionKeys(IReadOnlyList<VolumeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        return entries.Where(e => e.Kind == VolumeEntryKind.CollectionStack).Select(e => e.Collection!.Key).ToList();
    }
}
