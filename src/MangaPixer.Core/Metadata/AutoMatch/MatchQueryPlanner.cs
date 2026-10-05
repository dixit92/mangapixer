namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// Builds the provider queries for one work (metadata stage 2, design section 2): ordered,
/// de-duplicated query variants plus the local corroboration context. Pure; the variants are
/// derived from display names only and nothing here is sent anywhere - the caller decides how
/// many variants it sends (at most a few, stopping at a confident result).
///
/// Variant order (= <see cref="QueryVariantKind"/> order): ComicInfo series, folder primary,
/// trailing <c>[English Title]</c>, subtitle split, sequel-number split, archive-derived title,
/// and for doujin-shaped archives the MangaUpdates <c>&lt;parody&gt; dj - &lt;title&gt;</c> form. One exception
/// (1.27.0): an archive-derived title that extends the folder name word for word (the folder is the leading
/// part of a long title) is the second search, right after the folder's own names. Another (1.34.0): below a "Collection about"
/// folder (<see cref="FolderShape.CollectionSeries"/>), the parody form built from that series' title is the second search too, when
/// the name carries no <c>(parody)</c> of its own. A dated doujin name (<see cref="DatedDoujinName"/>, 1.34.2) is searched by its title
/// (the cleaned name already is), and the character it is about is the last, review-only search (<see cref="QueryVariantKind.CharacterName"/>).
/// Variants are
/// de-duplicated by their scoring form (a variant that differs only in case or punctuation is one
/// query).
/// </summary>
public sealed class MatchQueryPlanner : IMatchQueryPlanner
{
    /// <summary>Upper bound on the variants of one query (the caller sends fewer).</summary>
    public const int MaxVariants = 8;

    public MatchQuery PlanFolder(FolderShape folder, WorkClassification classification, string? comicInfoSeries = null)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(classification);
        var archives = folder.ArchiveNames ?? [];
        var variants = new VariantList();

        if (TitleNormalizer.Normalize(comicInfoSeries) is { Primary.Length: > 0 } ci)
            variants.Add(ci.Primary, QueryVariantKind.ComicInfoSeries);

        var name = TitleNormalizer.Normalize(folder.DisplayName);
        AddNameVariants(variants, name);
        AddCreatorSplits(variants, folder.DisplayName);

        if (TitleNormalizer.ArchiveTitle(archives) is { } archiveTitle)
        {
            // The archives carry a LONGER name that starts with the folder's (a folder named after the leading
            // words of a long title, 1.27.0): that name is the second search, right after the folder's own.
            var extends = name.Primary.Length > 0
                && TitleNormalizer.ScoringForm(archiveTitle).StartsWith(TitleNormalizer.ScoringForm(name.Primary) + " ", StringComparison.Ordinal);
            variants.Add(archiveTitle, QueryVariantKind.ArchiveDerivedTitle, extends ? SecondSearch : null);
        }
        else if (archives.Count == 1 && TitleNormalizer.Normalize(archives[0]).Primary is { Length: > 0 } single)
            variants.Add(single, QueryVariantKind.ArchiveDerivedTitle);

        // 1.34.0: a work inside a "Collection about" folder is a fan work of that series.
        if (CollectionParody(folder) is { } folderParody && name.Primary.Length > 0)
            variants.Add($"{folderParody} dj - {name.Primary}", QueryVariantKind.DoujinParodyForm, SecondSearch);
        AddCharacterFallback(variants, archives.Count == 1 ? [folder.DisplayName, archives[0]] : [folder.DisplayName]);

        var anatomies = archives.Select(ArchiveNameAnatomy.Parse).ToList();
        var authorTags = DominantCreatorTags(anatomies);
        if (classification.Class == WorkClass.ArtistCollection && name.Primary.Length > 0)
            AddDistinct(authorTags, name.Primary);
        if (folder.ParentDisplayName is { } parent && AutoMatchText.IsAuthorLike(parent, requireTwoTokens: true))
            AddDistinct(authorTags, TitleNormalizer.Normalize(parent).Primary);

        // Units are read with the folder's own "No. N" masked (1.32.0): in "Robot No. 9", "Robot No. 9.cbz" is not issue 9.
        var unitNames = archives.Select(a => AutoMatchText.MaskFolderTitleNumber(a, folder.DisplayName)).ToList();
        var volumeLike = unitNames.Count(AutoMatchText.IsVolumeLike);
        var chapterLike = unitNames.Count(AutoMatchText.IsChapterLike);
        var archiveCount = archives.Count;
        foreach (var sub in folder.Subfolders ?? [])
        {
            if (sub.DescendantArchiveCount <= 0 || !AutoMatchText.IsUnitFolderName(sub.DisplayName))
                continue;
            archiveCount += sub.DescendantArchiveCount;
            if (AutoMatchText.IsVolumeFolderName(sub.DisplayName))
                volumeLike += sub.DescendantArchiveCount;
            else if (AutoMatchText.IsChapterFolderName(sub.DisplayName))
                chapterLike += sub.DescendantArchiveCount;
        }
        // The count rule compares unit NUMBERS (1.27.0), and since 1.29.0 unit subfolders add the numbers their archive
        // names state, never their archive count (CountEvidence.LocalOf).
        var units = CountEvidence.LocalOf(unitNames, folder.Subfolders);

        var years = new List<int>();
        if (AutoMatchText.EarliestYear(archives) is { } archiveYear) years.Add(archiveYear);
        if (name.YearHint is { } folderYear) years.Add(folderYear);

        var context = new MatchContext(
            classification.Class,
            archiveCount,
            volumeLike,
            chapterLike,
            years.Count > 0 ? years.Min() : null,
            folder.CategoryHint,
            TallStrips: false,
            authorTags,
            string.IsNullOrWhiteSpace(comicInfoSeries) ? null : comicInfoSeries.Trim(),
            AutoMatchText.CreatorHints(folder.DisplayName),
            units.HighestVolume is > 0 ? units.HighestVolume : null,
            units.HighestChapter is > 0 ? units.HighestChapter : null,
            Units: units);
        return new MatchQuery(variants.ToList(), context);
    }

    public MatchQuery PlanArchiveGroup(FolderShape folder, WorkClassification classification, ArchiveGroup group)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(classification);
        ArgumentNullException.ThrowIfNull(group);
        var all = folder.ArchiveNames ?? [];
        var names = group.ArchiveIndexes.Where(i => i >= 0 && i < all.Count).Select(i => all[i]).ToList();
        var anatomies = names.Select(ArchiveNameAnatomy.Parse).ToList();
        var variants = new VariantList();

        // The group's query title first (a mini-series base, or the one archive's clean title),
        // then what the archive names add: an [English Title], splits, a common clean title.
        var groupTitle = TitleNormalizer.Normalize(group.QueryTitle);
        if (groupTitle.Primary.Length > 0)
            variants.Add(groupTitle.Primary, QueryVariantKind.Primary);
        variants.Add(groupTitle.PrimaryWithExclamation, QueryVariantKind.Primary);
        foreach (var n in names.Select(TitleNormalizer.Normalize))
        {
            if (n.Variants.Count > 1)
                variants.Add(n.Variants[1], QueryVariantKind.EnglishTitle);
        }
        foreach (var d in groupTitle.Derived)
            variants.Add(d.Text, d.Kind == DerivedTitleKind.SubtitleSplit ? QueryVariantKind.SubtitleSplit : QueryVariantKind.SequelNumberSplit);
        AddCreatorSplits(variants, group.QueryTitle);
        if (names.Count == 1)
            AddCreatorSplits(variants, names[0]);
        if (TitleNormalizer.ArchiveTitle(names) is { } archiveTitle)
            variants.Add(archiveTitle, QueryVariantKind.ArchiveDerivedTitle);

        var ownParody = false;
        foreach (var a in anatomies)
        {
            if (a.IsDoujinShaped && a.Parody is { } parody && a.Title.Length > 0)
            {
                var title = names.Count > 1 && groupTitle.Primary.Length > 0 ? groupTitle.Primary : a.Title;
                variants.Add($"{parody} dj - {title}", QueryVariantKind.DoujinParodyForm);
                ownParody = true;
                break;
            }
        }
        // 1.34.0: in a "Collection about" folder the series is the parody (unless the name states its own, e.g. a crossover).
        if (!ownParody && CollectionParody(folder) is { } collectionParody)
        {
            var title = groupTitle.Primary.Length > 0 ? groupTitle.Primary : anatomies.Select(a => a.Title).FirstOrDefault(t => t.Length > 0);
            if (!string.IsNullOrEmpty(title))
                variants.Add($"{collectionParody} dj - {title}", QueryVariantKind.DoujinParodyForm, SecondSearch);
        }

        AddCharacterFallback(variants, names);

        var authorTags = new List<string>();
        foreach (var tag in anatomies.SelectMany(a => a.CreatorTags))
        {
            if (AutoMatchText.IsAuthorLike(tag, requireTwoTokens: false))
                AddDistinct(authorTags, tag);
        }
        if (classification.Class == WorkClass.ArtistCollection
            && TitleNormalizer.Normalize(folder.DisplayName).Primary is { Length: > 0 } artist)
            AddDistinct(authorTags, artist);

        // A group of loose archives in a container or mixed folder is a work inside a collection
        // (owner, 2026-09-26): score it as one, so it may auto-link and the author veto applies.
        var workClass = classification.Class is WorkClass.CollectionLeaf or WorkClass.ArtistCollection
            ? classification.Class
            : WorkClass.CollectionLeaf;
        var context = new MatchContext(
            workClass,
            names.Count,
            names.Count(AutoMatchText.IsVolumeLike),
            names.Count(AutoMatchText.IsChapterLike),
            AutoMatchText.EarliestYear(names),
            folder.CategoryHint,
            TallStrips: false,
            authorTags,
            CreatorHints: names.SelectMany(AutoMatchText.CreatorHints).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            LocalVolumes: names.Select(AutoMatchText.VolumeNumberOf).Max(),
            LocalChapters: names.Select(AutoMatchText.ChapterNumberOf).Max(),
            Units: CountEvidence.LocalOf(names, null));
        return new MatchQuery(variants.ToList(), context);
    }

    /// <summary>The series title of the nearest "Collection about" folder, trimmed, or null.</summary>
    private static string? CollectionParody(FolderShape folder) =>
        string.IsNullOrWhiteSpace(folder.CollectionSeries) ? null : folder.CollectionSeries.Trim();

    private static void AddNameVariants(VariantList variants, NormalizedTitle name)
    {
        if (name.Primary.Length > 0)
            variants.Add(name.Primary, QueryVariantKind.Primary);
        // Second search text, only sent when the first finds nothing confident (the loop stops at 0.85).
        variants.Add(name.PrimaryWithExclamation, QueryVariantKind.Primary);
        if (name.Variants.Count > 1)
            variants.Add(name.Variants[1], QueryVariantKind.EnglishTitle);
        foreach (var d in name.Derived.Where(d => d.Kind == DerivedTitleKind.SubtitleSplit))
            variants.Add(d.Text, QueryVariantKind.SubtitleSplit);
        foreach (var d in name.Derived.Where(d => d.Kind == DerivedTitleKind.SequelNumberSplit))
            variants.Add(d.Text, QueryVariantKind.SequelNumberSplit);
    }

    /// <summary>
    /// The character the first dated doujin name with a title is about (1.34.2), as the last search: the name's own title is searched
    /// first, the character only when nothing confident was found - and it can never link on its own (the scorer caps it).
    /// </summary>
    private static void AddCharacterFallback(VariantList variants, IEnumerable<string> displayNames)
    {
        var character = displayNames.Select(DatedDoujinName.TryParse)
            .FirstOrDefault(d => d is { Title: not null, Character: not null })?.Character;
        if (TitleNormalizer.Normalize(character).Primary is { Length: > 0 } clean)
            variants.Add(clean, QueryVariantKind.CharacterName);
    }

    private static void AddCreatorSplits(VariantList variants, string? displayName)
    {
        foreach (var title in AutoMatchText.CreatorSplitTitles(displayName))
        {
            if (TitleNormalizer.Normalize(title).Primary is { Length: > 0 } clean)
                variants.Add(clean, QueryVariantKind.CreatorSplit);
        }
    }

    /// <summary>Creator tags carried by at least half of the archives (folder level: a tie-break only).</summary>
    private static List<string> DominantCreatorTags(List<ArchiveNameAnatomy> anatomies)
    {
        var result = new List<string>();
        if (anatomies.Count == 0)
            return result;
        var counts = new Dictionary<string, (string Spelling, int Count)>(StringComparer.Ordinal);
        foreach (var a in anatomies)
        {
            foreach (var tag in a.CreatorTags.Where(t => AutoMatchText.IsAuthorLike(t, requireTwoTokens: false)))
            {
                var key = TitleNormalizer.ScoringForm(tag);
                counts[key] = counts.TryGetValue(key, out var c) ? (c.Spelling, c.Count + 1) : (tag, 1);
            }
        }
        foreach (var (_, (spelling, count)) in counts.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (count * 2 >= anatomies.Count)
                result.Add(spelling);
        }
        return result;
    }

    private static void AddDistinct(List<string> list, string value)
    {
        if (value.Length > 0 && !list.Any(v => AutoMatchText.NamesEqual(v, value)))
            list.Add(value);
    }

    /// <summary>Order key between the folder's own names (<see cref="QueryVariantKind.Primary"/>) and the English title.</summary>
    private const double SecondSearch = (double)QueryVariantKind.Primary + 0.5;

    /// <summary>Variants in kind order (or an explicit order key), de-duplicated by scoring form, capped.</summary>
    private sealed class VariantList
    {
        private readonly List<(QueryVariant Variant, double Order)> _items = [];
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        public void Add(string? text, QueryVariantKind kind, double? order = null)
        {
            var t = text?.Trim();
            if (string.IsNullOrEmpty(t))
                return;
            var key = TitleNormalizer.ScoringForm(t);
            // A trailing "!" changes MangaUpdates' results although it scores the same: keep both.
            if (key.Length == 0 || !_keys.Add(t.EndsWith('!') ? key + "!" : key))
                return;
            _items.Add((new QueryVariant(t, kind), order ?? (int)kind));
        }

        public IReadOnlyList<QueryVariant> ToList() =>
            _items.Select((v, i) => (v, i))
                .OrderBy(x => x.v.Order)
                .ThenBy(x => x.i)
                .Select(x => x.v.Variant)
                .Take(MaxVariants)
                .ToList();
    }
}
