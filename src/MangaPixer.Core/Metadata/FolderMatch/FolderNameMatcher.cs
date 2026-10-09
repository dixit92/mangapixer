namespace com.lifepixer.mangapixer.Core.Metadata.FolderMatch;

using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>One creator credit on a stored series record: the input of <see cref="FolderNameMatcher.GroupArtists"/>.</summary>
/// <param name="Provider">The record's provider (<c>mangaupdates</c>, <c>gcd</c>).</param>
/// <param name="AuthorId">The provider's own person id (MangaUpdates <c>author_id</c>), or null (GCD, or a MangaUpdates credit without one).</param>
/// <param name="Name">The spelling on this record.</param>
/// <param name="Role">The stored role: <c>author</c>, <c>artist</c> (others are ignored).</param>
/// <param name="RecordId">The record this credit is on (a spelling counts once per record).</param>
/// <param name="FetchedAt">When the record was fetched (the tie-break for the main spelling).</param>
public sealed record CreatorCredit(string Provider, string? AuthorId, string Name, string Role, long RecordId, DateTimeOffset FetchedAt);

/// <summary>A stored author record (MangaUpdates): its main name and other names (pen names, other spellings and scripts).</summary>
public sealed record StoredAuthor(string AuthorId, string Name, IReadOnlyList<string> OtherNames);

/// <summary>
/// One known artist: every name it is known by on this server, and what a folder matched to it declares (<see cref="DeclaredName"/>,
/// <see cref="Role"/>).
/// </summary>
/// <param name="Key">Stable identity: <c>mangaupdates:&lt;author id&gt;</c>, or <c>&lt;provider&gt;:name:&lt;scoring form&gt;</c> for a credit without an id.</param>
/// <param name="DeclaredName">The main name: the stored author record's name, else the spelling on the most records (tie: the most recently fetched).</param>
/// <param name="Role"><c>author</c> ("Story &amp; art") when any credit is <c>author</c>, else <c>artist</c>.</param>
/// <param name="Names">Every name: the declared name first, then the other spellings and the author record's other names.</param>
/// <param name="Provider">The provider of the credits.</param>
/// <param name="AuthorId">The provider's person id, or null.</param>
/// <param name="RecordCount">How many stored records credit this artist.</param>
public sealed record KnownArtist(
    string Key, string DeclaredName, string Role, IReadOnlyList<string> Names, string Provider, string? AuthorId, int RecordCount);

/// <summary>A stored series record's titles, the input of <see cref="TitleNameIndex"/>.</summary>
public sealed record TitledRecord(long RecordId, string Title, IReadOnlyList<string> AltTitles);

/// <summary>A record whose title (or one alt title) equals the folder's name, and the title that matched.</summary>
public sealed record TitleMatch(long RecordId, string MatchedTitle);

/// <summary>
/// "Match folders by name" (1.38.0): pure name comparison of a folder with names stored on this server - artists (every creator spelling,
/// grouped by the provider's person id, plus the stored author records' other names) and series records (title + alt titles). Nothing is
/// fetched; the server feeds stored data in. Comparison reuses the matcher's own text rules: the folder name is cleaned by
/// <see cref="TitleNormalizer.Normalize"/> and compared in <see cref="TitleNormalizer.ScoringForm"/>; artists with
/// <see cref="AutoMatchText.NamesEqual"/>'s three rules, titles by exact equality (never a substring).
/// </summary>
public static partial class FolderNameMatcher
{
    /// <summary>The credit roles that make a creator an artist here (MangaUpdates / GCD store <c>author</c>, <c>artist</c>, <c>other</c>).</summary>
    public static readonly IReadOnlySet<string> ArtistRoles = new HashSet<string>(StringComparer.Ordinal) { "author", "artist" };

    /// <summary>
    /// The scoring forms a folder name is compared in: the name as written and the matcher's cleaned variants (brackets, tags, volume
    /// tokens and edition words removed; a trailing <c>[English Title]</c> as a second variant). Distinct, non-empty, written form first.
    /// </summary>
    public static IReadOnlyList<string> FolderForms(string? folderName)
    {
        var forms = new List<string>();
        void Add(string? text)
        {
            var form = TitleNormalizer.ScoringForm(text);
            if (form.Length > 0 && !forms.Contains(form, StringComparer.Ordinal))
                forms.Add(form);
        }
        Add(folderName);
        foreach (var variant in TitleNormalizer.Normalize(folderName).Variants)
            Add(variant);
        return forms;
    }

    /// <summary>
    /// The forms an ARTIST is looked up by (1.39.0): <see cref="FolderForms"/>, plus - for a folder named <c>Circle (Artist)</c> or
    /// <c>[Circle (Artist)]</c>, the doujin convention - the circle and the artist on their own, so either name finds its artist (both
    /// known = two matches, Ambiguous). A bracket part that is a year, a release tag or has no letters is not a name. Titles never use
    /// these forms.
    /// </summary>
    public static IReadOnlyList<string> ArtistForms(string? folderName)
    {
        var forms = FolderForms(folderName).ToList();
        if (string.IsNullOrWhiteSpace(folderName))
            return forms;
        var name = folderName.Trim();
        if (WholeBracket().Match(name) is { Success: true } whole)
            name = whole.Groups["inner"].Value.Trim();
        if (CircleArtist().Match(name) is not { Success: true } ca)
            return forms;
        foreach (var part in new[] { ca.Groups["circle"].Value, ca.Groups["artist"].Value })
        {
            var text = part.Trim();
            if (text.Length == 0 || !text.Any(char.IsLetter) || ArchiveNameAnatomy.IsReleaseTag(text))
                continue;
            var form = TitleNormalizer.ScoringForm(text);
            if (form.Length > 0 && !forms.Contains(form, StringComparer.Ordinal))
                forms.Add(form);
        }
        return forms;
    }

    [GeneratedRegex(@"^\[(?<inner>[^\[\]]+)\]$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeBracket();

    [GeneratedRegex(@"^(?<circle>[^()\[\]]*?)\s*\((?<artist>[^()]+)\)$", RegexOptions.CultureInvariant)]
    private static partial Regex CircleArtist();

    /// <summary>
    /// Groups creator credits into artists: one artist per provider person id; a credit without an id is its own artist per distinct name
    /// (scoring form) and provider. <paramref name="storedAuthors"/> (by author id) adds the main name and other names of a MangaUpdates
    /// author; it may be empty. Credits with another role than <see cref="ArtistRoles"/> are ignored.
    /// </summary>
    public static IReadOnlyList<KnownArtist> GroupArtists(
        IEnumerable<CreatorCredit> credits, IReadOnlyDictionary<string, StoredAuthor>? storedAuthors = null)
    {
        storedAuthors ??= new Dictionary<string, StoredAuthor>();
        var groups = new Dictionary<string, List<CreatorCredit>>(StringComparer.Ordinal);
        var order = new List<string>();
        foreach (var credit in credits)
        {
            if (!ArtistRoles.Contains(credit.Role) || string.IsNullOrWhiteSpace(credit.Name))
                continue;
            var form = TitleNormalizer.ScoringForm(credit.Name);
            if (form.Length == 0)
                continue;
            var key = string.IsNullOrWhiteSpace(credit.AuthorId)
                ? $"{credit.Provider}:name:{form}"
                : $"{credit.Provider}:{credit.AuthorId.Trim()}";
            if (!groups.TryGetValue(key, out var list))
            {
                groups[key] = list = [];
                order.Add(key);
            }
            list.Add(credit);
        }

        var artists = new List<KnownArtist>(order.Count);
        foreach (var key in order)
        {
            var list = groups[key];
            var first = list[0];
            var authorId = string.IsNullOrWhiteSpace(first.AuthorId) ? null : first.AuthorId.Trim();
            var stored = authorId is not null && storedAuthors.TryGetValue(authorId, out var a) ? a : null;

            // Spellings by the number of records that use them; tie: the most recently fetched record's spelling.
            var spellings = list
                .GroupBy(c => c.Name.Trim(), StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Records: g.Select(c => c.RecordId).Distinct().Count(), Latest: g.Max(c => c.FetchedAt)))
                .OrderByDescending(s => s.Records)
                .ThenByDescending(s => s.Latest)
                .ThenBy(s => s.Name, StringComparer.Ordinal)
                .Select(s => s.Name)
                .ToList();
            var declared = stored is not null && !string.IsNullOrWhiteSpace(stored.Name) ? stored.Name.Trim() : spellings[0];

            var names = new List<string>();
            void AddName(string? name)
            {
                name = name?.Trim();
                if (!string.IsNullOrEmpty(name) && !names.Contains(name, StringComparer.Ordinal))
                    names.Add(name);
            }
            AddName(declared);
            spellings.ForEach(AddName);
            foreach (var other in stored?.OtherNames ?? [])
                AddName(other);

            var role = list.Any(c => c.Role == "author") ? "author" : "artist";
            artists.Add(new KnownArtist(key, declared, role, names, first.Provider, authorId,
                list.Select(c => c.RecordId).Distinct().Count()));
        }
        return artists;
    }

    /// <summary>The two lookup keys of a scoring form that together give <see cref="AutoMatchText.NamesEqual"/>: sorted tokens, no spaces.</summary>
    internal static (string Sorted, string NoSpace) NameKeys(string form) =>
        (string.Join(' ', form.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal)),
         form.Replace(" ", string.Empty, StringComparison.Ordinal));
}

/// <summary>
/// The known artists by name: <see cref="Match"/> gives the artists with a name that <see cref="AutoMatchText.NamesEqual"/> one of the
/// folder's forms (<see cref="FolderNameMatcher.ArtistForms"/>). Built once per request; lookups are dictionary hits.
/// </summary>
public sealed class ArtistNameIndex
{
    private readonly Dictionary<string, List<int>> _bySorted = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byNoSpace = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<KnownArtist> _artists;

    public ArtistNameIndex(IReadOnlyList<KnownArtist> artists)
    {
        _artists = artists;
        for (var i = 0; i < artists.Count; i++)
        {
            foreach (var name in artists[i].Names)
            {
                var form = TitleNormalizer.ScoringForm(name);
                if (form.Length == 0)
                    continue;
                var (sorted, noSpace) = FolderNameMatcher.NameKeys(form);
                Add(_bySorted, sorted, i);
                Add(_byNoSpace, noSpace, i);
            }
        }
    }

    public int Count => _artists.Count;

    /// <summary>
    /// The artists one of whose names equals the folder's name (any of its forms), in index order, distinct. Artists that would declare the
    /// same name with the same role are kept once (the first) - they apply identically.
    /// </summary>
    public IReadOnlyList<KnownArtist> Match(string? folderName)
    {
        var hits = new SortedSet<int>();
        foreach (var form in FolderNameMatcher.ArtistForms(folderName))
        {
            var (sorted, noSpace) = FolderNameMatcher.NameKeys(form);
            if (_bySorted.TryGetValue(sorted, out var a))
                hits.UnionWith(a);
            if (_byNoSpace.TryGetValue(noSpace, out var b))
                hits.UnionWith(b);
        }
        var result = new List<KnownArtist>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var i in hits)
        {
            var artist = _artists[i];
            if (seen.Add(artist.Role + "\n" + TitleNormalizer.ScoringForm(artist.DeclaredName)))
                result.Add(artist);
        }
        return result;
    }

    private static void Add(Dictionary<string, List<int>> map, string key, int index)
    {
        if (key.Length == 0)
            return;
        if (!map.TryGetValue(key, out var list))
            map[key] = list = [];
        if (list.Count == 0 || list[^1] != index)
            list.Add(index);
    }
}

/// <summary>
/// Stored series records by title: <see cref="Match"/> gives the records whose title or one alt title equals one of the folder's forms in
/// scoring form (exact equality, never a substring), in the order the records were given, each once with the title that matched.
/// </summary>
public sealed class TitleNameIndex
{
    private readonly Dictionary<string, List<(int Index, string Title)>> _byForm = new(StringComparer.Ordinal);
    private readonly IReadOnlyList<TitledRecord> _records;

    public TitleNameIndex(IReadOnlyList<TitledRecord> records)
    {
        _records = records;
        for (var i = 0; i < records.Count; i++)
        {
            var record = records[i];
            foreach (var title in record.AltTitles.Prepend(record.Title))
            {
                var form = TitleNormalizer.ScoringForm(title);
                if (form.Length == 0)
                    continue;
                if (!_byForm.TryGetValue(form, out var list))
                    _byForm[form] = list = [];
                if (list.Count == 0 || list[^1].Index != i)
                    list.Add((i, title));
            }
        }
    }

    public int Count => _records.Count;

    public IReadOnlyList<TitleMatch> Match(string? folderName)
    {
        var hits = new SortedDictionary<int, string>();
        foreach (var form in FolderNameMatcher.FolderForms(folderName))
        {
            if (!_byForm.TryGetValue(form, out var list))
                continue;
            foreach (var (index, title) in list)
                hits.TryAdd(index, title);
        }
        return hits.Select(h => new TitleMatch(_records[h.Key].RecordId, h.Value)).ToList();
    }
}
