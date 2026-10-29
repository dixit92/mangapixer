namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Data.Common;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;

/// <summary>
/// Words spread over folder levels (1.50.0, lane S; owner decision 4 of 1.50.0 reversed 1.49.0's "names only"): <c>berserk 12</c> finds
/// <c>Berserk/Vol 12.cbz</c> - MangaList's renamer and other libraries name volumes and chapters without the series title.
/// <para>
/// The rule: every word (and quoted phrase) of the search matches the node's own name or the name of one of its ancestor folders in its
/// library, and at least one of them matches the node's OWN name (so <c>berserk</c> alone still lists the folder with "+N inside", not
/// every volume below it). A node whose own name holds every word is a name hit, as in 1.49.0; the others are cross-level hits, listed
/// after the name hits. Each word matches a name as the plain search matches it (3+ characters: contained; 1-2 characters, or a search
/// with no word of 3+: starts a word; a phrase in order). A <c>-word</c> term leaves out a node whose own name OR an ancestor's name holds
/// it (and so everything inside a folder it names).
/// </para>
/// <para>
/// Mechanism (measured in the lane note): a query-time walk, no index change. The viewer's visible live FOLDERS are read once per search
/// (id, parent, name, sort key - a few thousand rows), folded, and each folder gets the set of words its own name and its ancestors' names
/// cover. Cross-level folders come from that map; cross-level archives are read only below folders that cover a word, narrowed in SQL by
/// the words their folders leave missing. Ancestors are always in the node's own library, and only visible libraries are read: a library
/// the viewer cannot see (or a Private one under Incognito) never contributes a word.
/// </para>
/// </summary>
public sealed partial class CatalogBrowseService
{
    /// <summary>The rank of a cross-level hit: after every name tier (<see cref="SearchText.RankContains"/>) in its group.</summary>
    internal const int RankCrossLevel = 10;

    /// <summary>The most words + phrases a search may have for the cross-level pass (one bit each).</summary>
    private const int MaxLevelUnits = 30;

    /// <summary>A visible live folder: parent, folded name, sort key.</summary>
    private sealed record LevelFolder(long? Parent, string Folded, string SortKey);

    /// <summary>
    /// The visible folders of one search and, per folder, the words (bits) its own name covers, the words it and its ancestors cover, and
    /// whether it or an ancestor holds a <c>-word</c> term.
    /// </summary>
    private sealed class LevelTree
    {
        private readonly Dictionary<long, LevelFolder> _folders;
        private readonly Dictionary<long, (int Own, int Covered, bool Excluded)> _memo = [];
        private readonly SearchQuery _query;
        private readonly IReadOnlyList<LevelUnit> _units;

        public LevelTree(Dictionary<long, LevelFolder> folders, SearchQuery query, IReadOnlyList<LevelUnit> units)
        {
            _folders = folders;
            _query = query;
            _units = units;
            All = units.Count == 0 ? 0 : (int)((1L << units.Count) - 1);
        }

        /// <summary>Every word's bit.</summary>
        public int All { get; }

        public IReadOnlyDictionary<long, LevelFolder> Folders => _folders;

        /// <summary>The words a name covers (bits).</summary>
        public int Own(string folded)
        {
            var mask = 0;
            for (var i = 0; i < _units.Count; i++)
            {
                if (_units[i].Matches(folded))
                    mask |= 1 << i;
            }
            return mask;
        }

        /// <summary>The words a folder and its ancestors cover, and whether one of them is left out; (0, false) for an unknown id.</summary>
        public (int Own, int Covered, bool Excluded) Of(long? folderId)
        {
            if (folderId is not { } id || !_folders.TryGetValue(id, out var folder))
                return (0, 0, false);
            if (_memo.TryGetValue(id, out var known))
                return known;
            // Iterative up-walk (no recursion on deep trees): collect the chain to the first known ancestor, then fill it top-down.
            var chain = new List<long> { id };
            var parent = folder.Parent;
            while (parent is { } p && _folders.ContainsKey(p) && !_memo.ContainsKey(p) && chain.Count < 512)
            {
                chain.Add(p);
                parent = _folders[p].Parent;
            }
            var (_, covered, excluded) = parent is { } top && _memo.TryGetValue(top, out var above) ? above : (0, 0, false);
            for (var i = chain.Count - 1; i >= 0; i--)
            {
                var f = _folders[chain[i]];
                var own = Own(f.Folded);
                covered |= own;
                excluded |= _query.Negated.Count > 0 && SearchText.IsExcluded(_query, f.Folded);
                _memo[chain[i]] = (own, covered, excluded);
            }
            return _memo[id];
        }
    }

    /// <summary>One word or quoted phrase of the search, matched against one folded name as the plain search matches it.</summary>
    private sealed record LevelUnit(string Text, bool WordStart)
    {
        public bool Matches(string folded) =>
            WordStart ? SearchText.HasWordStartingWith(folded, Text) : folded.Contains(Text, StringComparison.Ordinal);
    }

    /// <summary>The search's words and phrases as level units, distinct; empty when there are none or too many.</summary>
    private static List<LevelUnit> LevelUnits(SearchQuery parsed)
    {
        var shortSearch = SearchText.IsShort(parsed);
        var units = parsed.Words.Select(w => new LevelUnit(w.Folded, shortSearch || w.Length < SearchText.TrigramLength))
            .Concat(parsed.Phrases.Select(p => new LevelUnit(string.Join(' ', p.Select(w => w.Folded)), shortSearch)))
            .Distinct()
            .ToList();
        return units.Count > MaxLevelUnits ? [] : units;
    }

    /// <summary>
    /// The level tree of a search, or null when it needs none: a search of one word (or phrase) and no <c>-word</c> term matches names
    /// only. One query over the visible live folders.
    /// </summary>
    private static async Task<LevelTree?> LevelTreeAsync(DbConnection connection, SearchQuery parsed, string libIds, CancellationToken ct)
    {
        var units = LevelUnits(parsed);
        if (units.Count < 2 && parsed.Negated.Count == 0)
            return null;
        var folders = new Dictionary<long, LevelFolder>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT Id, ParentId, DisplayName, SortKey FROM catalog_nodes
            WHERE LibraryId IN ({libIds}) AND Kind = {(int)CatalogNodeKind.Folder} AND Availability != 5
            """;
        using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            folders[reader.GetInt64(0)] = new LevelFolder(
                reader.IsDBNull(1) ? null : reader.GetInt64(1), SearchText.Fold(reader.GetString(2)), reader.GetString(3));
        }
        return new LevelTree(folders, parsed, units.Count >= 2 ? units : []);
    }

    /// <summary>Whether a hit sits inside a folder (any ancestor) a <c>-word</c> term leaves out.</summary>
    private static bool InsideExcluded(LevelTree? tree, long? parentId) => tree is not null && tree.Of(parentId).Excluded;

    /// <summary>
    /// The cross-level hits (group 1, <see cref="RankCrossLevel"/>): folders from the tree, archives read below folders that cover a word
    /// (chunks of 500 parents per query), narrowed in SQL by the words still missing there - or, where the folders cover every word, by
    /// "holds at least one of them". Names already listed (<paramref name="listed"/>) are skipped.
    /// </summary>
    private static async Task<List<SearchHit>> CrossLevelHitsAsync(
        DbConnection connection, SearchQuery parsed, LevelTree tree, HashSet<long> listed, CancellationToken ct)
    {
        var hits = new List<SearchHit>();
        if (tree.All == 0)
            return hits;

        // Folders: own name covers some but not every word, its ancestors the rest.
        var parentsByMissing = new Dictionary<int, List<long>>();
        foreach (var (id, folder) in tree.Folders)
        {
            var (own, covered, excluded) = tree.Of(id);
            if (covered == 0 || excluded)
                continue;
            // Archives directly inside this folder need the words it leaves missing (and at least one word of their own).
            var missing = tree.All & ~covered;
            if (!parentsByMissing.TryGetValue(missing, out var parents))
                parentsByMissing[missing] = parents = [];
            parents.Add(id);
            if (own == 0 || own == tree.All || listed.Contains(id))
                continue;
            if ((own | tree.Of(folder.Parent).Covered) == tree.All && !SearchText.IsExcluded(parsed, folder.Folded))
                hits.Add(new SearchHit(id, (int)CatalogNodeKind.Folder, folder.SortKey, 1, RankCrossLevel));
        }

        var units = LevelUnits(parsed);
        foreach (var (missing, parents) in parentsByMissing)
        {
            var narrowing = missing == 0
                ? AnyWordPrefilter(units)
                : ContainsPrefilter(parsed.Words.Concat(parsed.Phrases.SelectMany(p => p))
                    .Where(w => units.FindIndex(u => u.Text == w.Folded) is var i && i >= 0 && (missing & (1 << i)) != 0));
            foreach (var chunk in parents.Chunk(500))
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"""
                    SELECT cn.Id, cn.ParentId, cn.DisplayName, cn.SortKey FROM catalog_nodes cn
                    WHERE cn.ParentId IN ({string.Join(",", chunk.Select(id => id.ToString(CultureInfo.InvariantCulture)))})
                    AND cn.Kind = {(int)CatalogNodeKind.Archive} AND cn.Availability != 5
                    {narrowing}
                    """;
                using var reader = await command.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0);
                    if (listed.Contains(id))
                        continue;
                    var folded = SearchText.FoldName(reader.GetString(2), isArchive: true);
                    var own = tree.Own(folded);
                    if (own == 0 || own == tree.All || (own | tree.Of(reader.GetInt64(1)).Covered) != tree.All
                        || SearchText.IsExcluded(parsed, folded))
                        continue;
                    hits.Add(new SearchHit(id, (int)CatalogNodeKind.Archive, reader.GetString(3), 1, RankCrossLevel));
                }
            }
        }
        return hits;
    }

    /// <summary>
    /// SQL narrowing for "the name holds at least one of these words": an OR of <c>LIKE</c>s (plain ASCII words), or nothing when a word
    /// cannot be narrowed in SQL (then every name passes and C# decides). Names with a character outside printable ASCII always pass.
    /// </summary>
    private static string AnyWordPrefilter(IReadOnlyList<LevelUnit> units)
    {
        var words = units.Select(u => u.Text).ToList();
        if (words.Count == 0 || words.Any(w => !w.All(c => char.IsAsciiLetterOrDigit(c) || c == ' ')))
            return "";
        // A phrase is narrowed by its first word (its words are letters and digits; the space is not a LIKE wildcard).
        var parts = words.Select(w => $"cn.DisplayName LIKE '%{w.Split(' ')[0]}%'");
        return "AND (" + string.Join(" OR ", parts) + " OR cn.DisplayName GLOB '*[^ -~]*')";
    }
}
