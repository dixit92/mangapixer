namespace com.lifepixer.mangapixer.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>A credit after creator resolution: the live creator, the indexed roles, the spelling.</summary>
public sealed record FacetResolvedCredit(long CreatorId, FacetRoles Roles, string Name);

/// <summary>
/// Everything one LOCATION of a work contributes (D1 2.3: entries are per location, so a value only one location's ComicInfo carries is
/// visible only with that location).
/// </summary>
/// <param name="Record">The work's linked record (every location of a linked work carries it), or null.</param>
/// <param name="RecordCreators">The record's indexed credits, resolved.</param>
/// <param name="ComicInfo">The current ComicInfo of the location's archives.</param>
/// <param name="ComicInfoCreators">Their indexed credits, resolved.</param>
/// <param name="DeclaredType">The declared type in effect at the location, or null.</param>
/// <param name="DeclaredCreators">The declared creators in effect at the location, resolved.</param>
/// <param name="DeclaredCreatorsConflict">The declared creators disagree with the record's (no declared name is one of the record's -
/// <see cref="DeclaredFactsComparer.CreatorsConflict"/>): their chips are flagged (owner 2026-09-27 answer 3).</param>
/// <param name="About">The record (id, title) of the "Collection about" folder the location sits in, or null.</param>
public sealed record FacetLocationInput(
    FacetRecord? Record,
    IReadOnlyList<FacetResolvedCredit> RecordCreators,
    IReadOnlyList<FacetComicInfo> ComicInfo,
    IReadOnlyList<FacetResolvedCredit> ComicInfoCreators,
    DeclaredType? DeclaredType,
    IReadOnlyList<FacetResolvedCredit> DeclaredCreators,
    bool DeclaredCreatorsConflict,
    (long RecordId, string Title)? About);

/// <summary>
/// One value at one location: the bucket, the stable value key (unique per bucket), the display text, the creator of a creator value,
/// where it came from, the creator's roles and the conflict flag.
/// </summary>
public sealed record FacetEntryDraft(
    FacetBucket Bucket,
    string ValueKey,
    string Display,
    long? CreatorId,
    FacetSources Sources,
    FacetRoles Roles,
    bool Conflict);

/// <summary>
/// The values of one location per bucket (1.45.0 lane F, D1 2.4). Pure and deterministic (record first, then ComicInfo in archive order,
/// then declared). Value keys:
/// <list type="bullet">
/// <item>Creators <c>c:&lt;creator id&gt;</c> - record credits (web), ComicInfo writer / penciller / inker / cover artist (owner Q11) and
/// declared creators; roles merged per creator ("Story &amp; art" = both).</item>
/// <item>Magazine <c>m:&lt;folded name&gt;</c> - the record's publications.</item>
/// <item>Publisher <c>p:&lt;folded name&gt;</c> - the record's publishers (original, English, other) and ComicInfo Publisher / Imprint.</item>
/// <item>Year <c>y:&lt;year&gt;</c> - the record's start year, else the earliest ComicInfo Year.</item>
/// <item>Genres <c>g:&lt;folded name&gt;</c> - MangaUpdates genres + ComicInfo Genre, case-folded.</item>
/// <item>Type <c>t:&lt;slug&gt;</c> - the declared type, else from the record's origin / format / webtoon flag (Japan manga, Korea manhwa,
/// China / Taiwan manhua, a Western origin comic; a webtoon record also webtoon; a novel only novel). A declared type the record contradicts
/// (<see cref="DeclaredFactsComparer.TypeConflicts"/>) shows BOTH, the declared one flagged.</item>
/// <item>About <c>a:&lt;record id&gt;</c> - works inside a "Collection about" folder; the display is the series' title (Q8).</item>
/// <item>Tags <c>tg:&lt;tag key&gt;</c> (1.49.0, lane T; owner decision 8 a + a) - the record's stored categories, strongest (most net
/// votes) first, then the ComicInfo <c>Tags</c> in archive order; one value per <see cref="FacetText.TagKey"/>.</item>
/// </list>
/// Not indexed: the record's <c>other</c> credits and the minor ComicInfo roles.
/// </summary>
public static class FacetExtractor
{
    public static IReadOnlyList<FacetEntryDraft> Extract(FacetLocationInput input, Func<long, string> creatorDisplay)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(creatorDisplay);
        var entries = new Dictionary<(FacetBucket, string), FacetEntryDraft>();
        var order = new List<(FacetBucket, string)>();

        void Add(FacetBucket bucket, string? key, string? display, FacetSources source, FacetRoles roles = FacetRoles.None,
            bool conflict = false, long? creatorId = null)
        {
            if (key is null || display is null)
                return;
            if (key.Length > FacetText.MaxValueKeyLength)
                key = key[..FacetText.MaxValueKeyLength];
            var id = (bucket, key);
            if (entries.TryGetValue(id, out var existing))
            {
                var sources = existing.Sources | source;
                entries[id] = existing with
                {
                    Sources = sources,
                    Roles = existing.Roles | roles,
                    // A value the record also gives is not a conflict, whoever else declared it.
                    Conflict = (existing.Conflict || conflict) && !sources.HasFlag(FacetSources.Web),
                };
                return;
            }
            entries[id] = new FacetEntryDraft(bucket, key, display, creatorId, source, roles, conflict && source != FacetSources.Web);
            order.Add(id);
        }

        void Creators(IEnumerable<FacetResolvedCredit> credits, FacetSources source, bool conflict = false)
        {
            foreach (var credit in credits)
            {
                if (credit.CreatorId <= 0)
                    continue;
                var display = FacetText.Display(creatorDisplay(credit.CreatorId)) ?? FacetText.Display(credit.Name);
                Add(FacetBucket.Creators, "c:" + FacetText.Invariant(credit.CreatorId), display, source, credit.Roles, conflict, credit.CreatorId);
            }
        }

        var record = input.Record;
        var comicInfo = input.ComicInfo.OrderBy(c => c.ArchiveId).ToList();

        // 1. Creators.
        Creators(input.RecordCreators, FacetSources.Web);
        Creators(input.ComicInfoCreators, FacetSources.ComicInfo);
        Creators(input.DeclaredCreators, FacetSources.Declared, input.DeclaredCreatorsConflict && record is not null);

        // 3. Magazine.
        foreach (var magazine in record?.Magazines ?? [])
            Add(FacetBucket.Magazine, Key("m:", magazine), FacetText.Display(magazine), FacetSources.Web);

        // 4. Publisher / imprint (original first, then English, then others; ComicInfo after the record).
        foreach (var publisher in (record?.Publishers ?? []).OrderBy(p => PublisherRank(p.Kind)))
            Add(FacetBucket.Publisher, Key("p:", publisher.Name), FacetText.Display(publisher.Name), FacetSources.Web);
        foreach (var row in comicInfo)
        {
            Add(FacetBucket.Publisher, Key("p:", row.Publisher), FacetText.Display(row.Publisher), FacetSources.ComicInfo);
            Add(FacetBucket.Publisher, Key("p:", row.Imprint), FacetText.Display(row.Imprint), FacetSources.ComicInfo);
        }

        // 5. Year started: the record's, else the earliest ComicInfo year.
        if (FacetText.Year(record?.StartYear) is { } startYear)
        {
            Add(FacetBucket.Year, "y:" + FacetText.Invariant(startYear), FacetText.Invariant(startYear), FacetSources.Web);
        }
        else if (comicInfo.Select(c => FacetText.Year(c.Year)).Where(y => y is not null).Min() is { } earliest)
        {
            Add(FacetBucket.Year, "y:" + FacetText.Invariant(earliest), FacetText.Invariant(earliest), FacetSources.ComicInfo);
        }

        // 6. Genres.
        foreach (var genre in record?.Genres ?? [])
            Add(FacetBucket.Genres, Key("g:", genre), FacetText.Display(genre), FacetSources.Web);
        foreach (var genre in comicInfo.SelectMany(c => c.Genres))
            Add(FacetBucket.Genres, Key("g:", genre), FacetText.Display(genre), FacetSources.ComicInfo);

        // 7. Type / origin / format.
        IReadOnlyList<DeclaredType> recordTypes = record is null ? [] : TypesOf(record);
        if (input.DeclaredType is { } declared)
        {
            var conflicts = record is not null && DeclaredFactsComparer.TypeConflicts(declared, record.Origin, record.Format);
            Add(FacetBucket.Type, TypeKey(declared), TypeName(declared), FacetSources.Declared, conflict: conflicts);
            if (conflicts)
            {
                foreach (var type in recordTypes)
                    Add(FacetBucket.Type, TypeKey(type), TypeName(type), FacetSources.Web);
            }
            else if (recordTypes.Contains(declared))
            {
                Add(FacetBucket.Type, TypeKey(declared), TypeName(declared), FacetSources.Web);
            }
        }
        else
        {
            foreach (var type in recordTypes)
                Add(FacetBucket.Type, TypeKey(type), TypeName(type), FacetSources.Web);
        }

        // 8. About.
        if (input.About is { } about && FacetText.Display(about.Title) is { } aboutTitle)
            Add(FacetBucket.About, "a:" + FacetText.Invariant(about.RecordId), aboutTitle, FacetSources.Web);

        // 10. Tags (1.49.0): the record's categories by net votes (a stable order: ties by name), then ComicInfo Tags.
        foreach (var category in (record?.Categories ?? []).OrderByDescending(c => c.Votes).ThenBy(c => c.Name, StringComparer.Ordinal))
            Add(FacetBucket.Tags, TagValueKey(category.Name), FacetText.Display(category.Name), FacetSources.Web);
        foreach (var tag in comicInfo.SelectMany(c => c.Tags ?? []))
            Add(FacetBucket.Tags, TagValueKey(tag), FacetText.Display(tag), FacetSources.ComicInfo);

        return order.Select(id => entries[id]).ToList();
    }

    /// <summary>1.49.0: the value key of a tag (<c>tg:&lt;tag key&gt;</c>), or null for a blank tag.</summary>
    public static string? TagValueKey(string? tag) => FacetText.TagKey(tag) is { } key ? "tg:" + key : null;

    /// <summary>The types a record's origin, format and webtoon flag give (none when it says nothing).</summary>
    public static IReadOnlyList<DeclaredType> TypesOf(FacetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Format == MetadataFormat.Novel)
            return [DeclaredType.Novel];
        var types = new List<DeclaredType>(2);
        DeclaredType? byOrigin = record.Origin switch
        {
            MetadataOrigin.Japan => DeclaredType.Manga,
            MetadataOrigin.Korea => DeclaredType.Manhwa,
            MetadataOrigin.ChinaTaiwan => DeclaredType.Manhua,
            MetadataOrigin.EnglishOriginal or MetadataOrigin.French or MetadataOrigin.Spanish or MetadataOrigin.German
                or MetadataOrigin.Nordic or MetadataOrigin.Italian or MetadataOrigin.Dutch => DeclaredType.Comic,
            _ => null,
        };
        if (byOrigin is { } origin)
            types.Add(origin);
        if (record.Webtoon == true)
            types.Add(DeclaredType.Webtoon);
        return types;
    }

    /// <summary>The value key of a type (<c>t:&lt;slug&gt;</c>).</summary>
    public static string TypeKey(DeclaredType type) => "t:" + DeclaredFactKeys.TypeSlug(type);

    /// <summary>The chip text of a type.</summary>
    public static string TypeName(DeclaredType type) => type switch
    {
        DeclaredType.GraphicNovel => "Graphic novel",
        _ => type.ToString(),
    };

    private static string? Key(string prefix, string? text) => FacetText.Fold(text) is { } folded ? prefix + folded : null;

    private static int PublisherRank(string? kind) => kind switch
    {
        "original" => 0,
        "english" => 1,
        _ => 2,
    };
}
