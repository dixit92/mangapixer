namespace com.lifepixer.mangapixer.Core.Metadata.Facets;

using System.Globalization;
using System.Text;
using com.lifepixer.mangapixer.Core.Catalog;

// 1.45.0 seam I0: the vocabulary of the tag index (D1 "Information Panel and Faceted Tags", sections 2.1-2.4). Every value below is
// STORED as its number (tables of migration AddFacetIndex): never renumber, only append.

/// <summary>
/// The buckets of the Info panel, in the owner's display order (decision 5, D1 2.4). Stored in <c>facet_values.Bucket</c>.
/// </summary>
public enum FacetBucket
{
    /// <summary>Writers and artists (a canonical creator; <c>ValueKey = c:&lt;creator id&gt;</c>).</summary>
    Creators = 1,

    /// <summary>The series a work belongs to (single work: its locations; container: the works below).</summary>
    Series = 2,

    /// <summary>The magazine(s) a series ran in (the record's publications).</summary>
    Magazine = 3,

    /// <summary>Publisher / imprint (original first, then English; ComicInfo Publisher / Imprint).</summary>
    Publisher = 4,

    /// <summary>The year the series started (record start year, else the earliest ComicInfo Year).</summary>
    Year = 5,

    /// <summary>Genres (MangaUpdates genres + ComicInfo Genre, case-folded).</summary>
    Genres = 6,

    /// <summary>Type / origin / format (the declared type, else from the record or ComicInfo).</summary>
    Type = 7,

    /// <summary>"Fan works of &lt;series&gt;" for works inside a "Collection about" folder (owner Q8, last of the web buckets).</summary>
    About = 8,

    /// <summary>Admin custom tags (decision 9, later; same tables, source <see cref="FacetSources.Admin"/>).</summary>
    Custom = 9,

    /// <summary>
    /// 1.49.0 (lane T, owner decision 8 a + a): tags - the stored MangaUpdates categories of the record (its top 20 by net votes) and the
    /// ComicInfo <c>Tags</c> of the location's archives, in one bucket; case and spacing folded so one tag is one value
    /// (<see cref="FacetText.TagKey"/>).
    /// </summary>
    Tags = 10,
}

/// <summary>Where an entry's value came from (<c>facet_entries.Sources</c>, a bit set: one entry may have several sources).</summary>
[Flags]
public enum FacetSources
{
    None = 0,

    /// <summary>A stored web record (MangaUpdates, GCD, MangaDex ...).</summary>
    Web = 1,

    /// <summary>The location's embedded ComicInfo.xml.</summary>
    ComicInfo = 2,

    /// <summary>A fact an admin declared on the folder or library (type, creators).</summary>
    Declared = 4,

    /// <summary>An admin's own tag (custom tags, later).</summary>
    Admin = 8,
}

/// <summary>A creator's roles on one work (<c>facet_entries.Roles</c>): both = "Story &amp; art" (D1 2.2; others not indexed in v1, Q11).</summary>
[Flags]
public enum FacetRoles
{
    None = 0,

    /// <summary>MangaUpdates <c>author</c>, ComicInfo <c>writer</c>.</summary>
    Writer = 1,

    /// <summary>MangaUpdates <c>artist</c>, ComicInfo <c>penciller</c> / <c>inker</c> / <c>coverartist</c>.</summary>
    Artist = 2,
}

/// <summary>How a work is identified (<c>facet_works.Kind</c>; the prefix of <see cref="FacetWorkKey"/>).</summary>
public enum FacetWorkKind
{
    /// <summary><c>rec:&lt;record id&gt;</c> - a linked series (Confirmed / Auto); one work however many folders or libraries link it.</summary>
    Record = 0,

    /// <summary><c>ci:&lt;series form&gt;#&lt;creator ids&gt;</c> - unlinked, its ComicInfo names one series and at least one creator.</summary>
    ComicInfo = 1,

    /// <summary><c>node:&lt;anchor node id&gt;</c> - any other work (the folder or archive is the work).</summary>
    Node = 2,
}

/// <summary>Where a creator's spelling came from (<c>creator_names.Source</c>, D1 2.2).</summary>
public enum CreatorNameSource
{
    /// <summary>A credit on a stored web record.</summary>
    Record = 0,

    /// <summary>A stored provider author record (<c>metadata_authors</c>: main name and other names).</summary>
    ProviderAuthor = 1,

    ComicInfo = 2,
    Declared = 3,
    Admin = 4,
}

/// <summary>Which rule chose a creator's display name (<c>creators.DisplayNameSource</c>): admin &gt; provider main name &gt; most used.</summary>
public enum CreatorDisplayNameSource
{
    /// <summary>The provider author record's main name.</summary>
    Provider = 0,

    /// <summary>The most used spelling among the creator's names (tie: the oldest).</summary>
    MostUsed = 1,

    /// <summary>An admin chose it (a <see cref="CreatorOverrideKind.DisplayName"/> override).</summary>
    Admin = 2,
}

/// <summary>An admin decision about creators (<c>creator_overrides.Kind</c>): input, never derived, never purged.</summary>
public enum CreatorOverrideKind
{
    /// <summary>The subject's creator is merged into the target (the merged creator keeps its row as a redirect).</summary>
    Merge = 0,

    /// <summary>The subject (a provider ref or a name key) is pinned to the target creator, so resolution never undoes a split.</summary>
    SplitPin = 1,

    /// <summary>The target creator shows the admin's display name.</summary>
    DisplayName = 2,
}

/// <summary>The script a creator spelling is written in (<c>creator_names.Script</c>, computed; the multilingual view picks by it later).</summary>
public enum CreatorNameScript
{
    Latin = 0,
    Cjk = 1,
    Hangul = 2,
    Other = 3,
}

/// <summary>
/// The parts of a <see cref="FacetWorkKey"/>: the kind, the record or node id (<see cref="Id"/>; 0 for a ComicInfo key), the series form
/// and the resolved creator ids (ComicInfo keys only; empty otherwise).
/// </summary>
public sealed record FacetWorkKeyParts(FacetWorkKind Kind, long Id, string? SeriesForm, IReadOnlyList<long> CreatorIds);

/// <summary>
/// The stable identity of a work in the tag index (<c>facet_works.WorkKey</c>, unique; D1 2.1). Three forms:
/// <list type="bullet">
/// <item><c>rec:&lt;record id&gt;</c> - a linked series (the same record linked from two folders or libraries is one work);</item>
/// <item><c>ci:&lt;series form&gt;#&lt;creator ids&gt;</c> - an unlinked work whose ComicInfo names one series and at least one creator.
/// The creator part is the RESOLVED creator ids (after the creator rules and admin overrides - owner Q5's condition), distinct,
/// ascending, comma-joined: two spellings of one creator give the same key, two creators sharing a spelling do not. The series form
/// is the caller's normalized ComicInfo series (lane F decides the normalization); it may contain <c>#</c> - the key splits at the
/// LAST <c>#</c>, the creator part never contains one;</item>
/// <item><c>node:&lt;anchor node id&gt;</c> - any other work.</item>
/// </list>
/// Ids are positive decimal int64 in canonical form (no sign, no leading zero). <see cref="TryParse"/> accepts only canonical keys, so
/// two keys are the same work exactly when the strings are equal (ordinal).
/// </summary>
public static class FacetWorkKey
{
    public const string RecordPrefix = "rec:";
    public const string ComicInfoPrefix = "ci:";
    public const string NodePrefix = "node:";

    /// <summary>The longest series form a ComicInfo key carries (= the stored ComicInfo Series column).</summary>
    public const int MaxSeriesFormLength = 512;

    /// <summary>The most creator ids a ComicInfo key carries.</summary>
    public const int MaxCreatorIds = 50;

    /// <summary>The longest key (<c>facet_works.WorkKey</c> column): <c>ci:</c> + the longest series form + <c>#</c> + 50 ids of 19 digits.</summary>
    public const int MaxLength = 3 + MaxSeriesFormLength + 1 + (MaxCreatorIds * 20) - 1;

    public static string Record(long recordId) => RecordPrefix + Positive(recordId, nameof(recordId));

    public static string Node(long nodeId) => NodePrefix + Positive(nodeId, nameof(nodeId));

    /// <summary>The ComicInfo key; the creator ids are de-duplicated and sorted here (at least one, at most <see cref="MaxCreatorIds"/>).</summary>
    public static string ComicInfo(string seriesForm, IEnumerable<long> creatorIds)
    {
        ArgumentNullException.ThrowIfNull(seriesForm);
        ArgumentNullException.ThrowIfNull(creatorIds);
        if (!IsValidSeriesForm(seriesForm))
            throw new ArgumentException("The series form must be 1-512 characters, without control characters or surrounding white space.", nameof(seriesForm));
        var ids = creatorIds.Distinct().Order().ToList();
        if (ids.Count == 0)
            throw new ArgumentException("A ComicInfo work key needs at least one resolved creator id.", nameof(creatorIds));
        if (ids.Count > MaxCreatorIds)
            throw new ArgumentException($"A ComicInfo work key carries at most {MaxCreatorIds} creator ids.", nameof(creatorIds));
        var builder = new StringBuilder(ComicInfoPrefix, ComicInfoPrefix.Length + seriesForm.Length + 1 + (ids.Count * 8));
        builder.Append(seriesForm).Append('#');
        for (var i = 0; i < ids.Count; i++)
        {
            if (i > 0)
                builder.Append(',');
            builder.Append(Positive(ids[i], nameof(creatorIds)));
        }
        return builder.ToString();
    }

    /// <summary>The key's kind and parts; false for anything that is not a canonical key of one of the three forms.</summary>
    public static bool TryParse(string? key, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out FacetWorkKeyParts? parts)
    {
        parts = null;
        if (string.IsNullOrEmpty(key) || key.Length > MaxLength)
            return false;
        if (key.StartsWith(RecordPrefix, StringComparison.Ordinal))
        {
            if (!TryCanonicalId(key.AsSpan(RecordPrefix.Length), out var id))
                return false;
            parts = new FacetWorkKeyParts(FacetWorkKind.Record, id, null, []);
            return true;
        }
        if (key.StartsWith(NodePrefix, StringComparison.Ordinal))
        {
            if (!TryCanonicalId(key.AsSpan(NodePrefix.Length), out var id))
                return false;
            parts = new FacetWorkKeyParts(FacetWorkKind.Node, id, null, []);
            return true;
        }
        if (!key.StartsWith(ComicInfoPrefix, StringComparison.Ordinal))
            return false;

        var hash = key.LastIndexOf('#');
        if (hash <= ComicInfoPrefix.Length)
            return false;
        var series = key[ComicInfoPrefix.Length..hash];
        if (!IsValidSeriesForm(series))
            return false;
        var tokens = key[(hash + 1)..].Split(',');
        if (tokens.Length > MaxCreatorIds)
            return false;
        var ids = new List<long>(tokens.Length);
        foreach (var token in tokens)
        {
            if (!TryCanonicalId(token, out var id) || (ids.Count > 0 && id <= ids[^1]))
                return false; // Not canonical: ids must be distinct and ascending.
            ids.Add(id);
        }
        parts = new FacetWorkKeyParts(FacetWorkKind.ComicInfo, 0, series, ids);
        return true;
    }

    /// <summary>A usable series form: 1-512 characters, no control characters, no leading or trailing white space.</summary>
    public static bool IsValidSeriesForm(string? seriesForm) =>
        seriesForm is { Length: > 0 and <= MaxSeriesFormLength }
        && !char.IsWhiteSpace(seriesForm[0]) && !char.IsWhiteSpace(seriesForm[^1])
        && !seriesForm.Any(char.IsControl);

    private static string Positive(long id, string name) =>
        id > 0 ? id.ToString(CultureInfo.InvariantCulture) : throw new ArgumentOutOfRangeException(name, "Ids in a work key are positive.");

    private static bool TryCanonicalId(ReadOnlySpan<char> text, out long id)
    {
        id = 0;
        if (text.Length is 0 or > 19 || text[0] == '0')
            return false;
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
                return false;
        }
        return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    }
}

/// <summary>
/// Public ids of creators (<c>cr_...</c>) and tag values (<c>fv_...</c>): the prefix + a random positive int64 in base36
/// (<see cref="OpaqueId"/>, as user public ids are made). Random, so a value id says nothing about how many values exist or in
/// which order they were made. Stored in <c>creators.PublicId</c> / <c>facet_values.PublicId</c> (unique; a collision on insert is
/// retried by the writer).
/// </summary>
public static class FacetPublicIds
{
    public const string CreatorPrefix = "cr_";
    public const string ValuePrefix = "fv_";

    /// <summary>The longest public id: the prefix + 13 base36 digits (<see cref="long.MaxValue"/>).</summary>
    public const int MaxLength = 16;

    public static string NewCreatorId() => CreatorPrefix + OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue));

    public static string NewValueId() => ValuePrefix + OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue));

    public static bool IsCreatorId(string? id) => IsWellFormed(id, CreatorPrefix);

    public static bool IsValueId(string? id) => IsWellFormed(id, ValuePrefix);

    private static bool IsWellFormed(string? id, string prefix)
    {
        if (id is null || id.Length <= prefix.Length || id.Length > MaxLength || !id.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var body = id.AsSpan(prefix.Length);
        if (body[0] == '0')
            return false;
        foreach (var c in body)
        {
            if (c is not ((>= '0' and <= '9') or (>= 'a' and <= 'z')))
                return false;
        }
        return true;
    }
}
