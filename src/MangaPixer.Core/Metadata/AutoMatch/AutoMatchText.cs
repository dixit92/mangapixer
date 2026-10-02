namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// Small name helpers shared by the work detector, the query planner and the scorer
/// (metadata stage 2). Pure; display names only.
/// </summary>
public static partial class AutoMatchText
{
    // Unit subfolders: Volumes, Vol(s), Chapters, Ch, Extras, Specials, Side Stories, Oneshots,
    // Bonus, Omake, Raw(s), Color(ed), Season(s) - optionally numbered or a range - a bare number
    // or range, and Part / Arc / Book N WITHOUT a subtitle.
    [GeneratedRegex(@"^(?:(?:volumes?|vols?|chapters?|chaps?|ch|extras?|specials?|side\s*stor(?:y|ies)|one-?shots?|bonus(?:es)?|omake|raws?|colou?r(?:ed)?|seasons?)\.?(?:\s*\d+(?:\.\d+)?(?:\s*-\s*\d+(?:\.\d+)?)?)?|(?:part|arc|book|season)\s*\.?\s*(?:\d+|[ivx]{1,4})|\d+(?:\.\d+)?(?:\s*-\s*\d+(?:\.\d+)?)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitFolder();

    // Part / Arc / Book N followed by a subtitle: a separate work (numbered parts are separate records).
    [GeneratedRegex(@"(?:^|\s)(?:part|arc|book)\s*\.?\s*(?:\d+|[ivx]{1,4})\s*(?:[-:~–—]\s*)?\p{L}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartWithSubtitle();

    [GeneratedRegex(@"\[[^\[\]]*\]|\([^()]*\)|\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroup();

    [GeneratedRegex(@"[\(\[](19\d{2}|20\d{2})[\)\]]", RegexOptions.CultureInvariant)]
    private static partial Regex YearGroup();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:v|vol|vols|volume|volumes)\.?\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeToken();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?:ch|chap|chapter|chapters|ep|episode)\.?\s*\d+|c\d+|#\s*\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterToken();

    // A trailing "(disambiguator)" of a provider title: "Look Back (FUJIMOTO Tatsuki)", "Beyond (GYARO)".
    [GeneratedRegex(@"^(?<head>.*\S)\s*\((?<tag>[^()]{1,80})\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex TrailingDisambiguator();

    /// <summary>
    /// Category folder words (1.27.0: the ONE category list, shared with the server's tree snapshot): a folder
    /// named exactly one of these (whole name, case-insensitive) is the category hint of the folders below it.
    /// <c>manga</c> / <c>manhwa</c> / <c>manhua</c> / <c>webtoon(s)</c> also name an origin
    /// (<see cref="OriginsForCategory"/>); the hint only ever ADDS evidence (owner option a', 2026-09-27). 1.32.0: the comics
    /// words (<see cref="ComicsSignals.CategoryWords"/>: comic books, graphic novel(s), BD, bande(s) dessinee(s), fumetti, tebeos,
    /// historietas, stripboeken, US comics, European comics, eurocomics) - accents do not matter (<c>Bandes dessinées</c>).
    /// Not words on purpose (too ambiguous): strips, albums, webcomics.
    /// </summary>
    public static IReadOnlySet<string> CategoryFolderWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "manga", "manhwa", "manhua", "webtoon", "webtoons", "comic", "comics", "doujin", "doujinshi",
        "comic books", "graphic novel", "graphic novels", "bd", "bande dessinee", "bandes dessinees", "fumetti", "tebeos",
        "historietas", "stripboeken", "us comics", "european comics", "eurocomics",
    };

    /// <summary>
    /// Shelf words: generic sorting folders (status, format, "misc") that name no work and no creator. Never a
    /// category hint; with <see cref="CategoryFolderWords"/> they form the creator stop list (E3: "Manga" exists
    /// as an author name on the provider side).
    /// </summary>
    public static IReadOnlySet<string> ShelfWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "one shots", "oneshots", "one shot", "oneshot", "anthology", "anthologies", "magazine", "magazines",
        "ongoing", "completed", "complete", "finished", "misc", "other", "others", "various", "unsorted",
        "new", "read", "unread", "hentai", "adult", "artbook", "artbooks", "novel", "novels", "light novels",
    };

    /// <summary>Both subsets, compared by scoring form: a category or shelf word, never a creator.</summary>
    private static readonly HashSet<string> s_categoryWords =
        new(CategoryFolderWords.Concat(ShelfWords).Select(TitleNormalizer.ScoringForm), StringComparer.Ordinal);

    /// <summary>True when a folder name, whole and trimmed, is a category folder word (the library root is never asked).</summary>
    public static bool IsCategoryFolderName(string? name) =>
        name is not null && (CategoryFolderWords.Contains(name.Trim()) || CategoryFolderWords.Contains(WithoutAccents(name.Trim())));

    private static string WithoutAccents(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    /// <summary>True when the name is a unit subfolder (<c>Volumes</c>, <c>Chapters 1-50</c>, <c>Season 2</c>, <c>Part 3</c>, <c>12</c>).</summary>
    public static bool IsUnitFolderName(string? name)
    {
        var s = Bare(name);
        return s.Length > 0 && UnitFolder().IsMatch(s);
    }

    /// <summary>True when the name carries <c>Part|Arc|Book N</c> followed by a subtitle (a separate work).</summary>
    public static bool IsPartWithSubtitle(string? name) => PartWithSubtitle().IsMatch(Bare(name));

    /// <summary>Unit subfolder named like volumes (<c>Volumes</c>, <c>Vol 1-5</c>).</summary>
    public static bool IsVolumeFolderName(string? name) =>
        IsUnitFolderName(name) && Bare(name).StartsWith("vol", StringComparison.OrdinalIgnoreCase);

    /// <summary>Unit subfolder named like chapters (<c>Chapters</c>, <c>Ch 1-50</c>).</summary>
    public static bool IsChapterFolderName(string? name) =>
        IsUnitFolderName(name) && Bare(name).StartsWith("ch", StringComparison.OrdinalIgnoreCase);

    /// <summary>A category or generic shelf word ("Manga", "Ongoing", "Doujinshi"), by scoring form.</summary>
    public static bool IsCategoryWord(string? name) => s_categoryWords.Contains(TitleNormalizer.ScoringForm(name));

    /// <summary>
    /// A plausible creator name to compare with record authors: at least two tokens (or one token
    /// of 4+ characters for a tag the archive names carry) and not a category word.
    /// </summary>
    public static bool IsAuthorLike(string? name, bool requireTwoTokens)
    {
        var key = TitleNormalizer.ScoringForm(name);
        if (key.Length < 2 || s_categoryWords.Contains(key))
            return false;
        var tokens = key.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return requireTwoTokens ? tokens >= 2 : tokens >= 2 || key.Length >= 4;
    }

    /// <summary>
    /// Two creator names are the same person or circle: equal scoring forms, the same tokens in a
    /// different order ("Family Given" vs "Given Family"), or equal once spaces are removed.
    /// </summary>
    public static bool NamesEqual(string? a, string? b)
    {
        var x = TitleNormalizer.ScoringForm(a);
        var y = TitleNormalizer.ScoringForm(b);
        if (x.Length == 0 || y.Length == 0)
            return false;
        if (x == y)
            return true;
        var tx = x.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
        var ty = y.Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal);
        return tx.SequenceEqual(ty, StringComparer.Ordinal)
            || x.Replace(" ", string.Empty, StringComparison.Ordinal) == y.Replace(" ", string.Empty, StringComparison.Ordinal);
    }

    /// <summary>True when <paramref name="text"/> contains <paramref name="part"/> on token boundaries (scoring forms).</summary>
    public static bool ContainsTokens(string? text, string? part)
    {
        var t = TitleNormalizer.ScoringForm(text);
        var p = TitleNormalizer.ScoringForm(part);
        return p.Length > 0 && t.Length > 0 && (" " + t + " ").Contains(" " + p + " ", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"\.(?:cbz|zip|cbr|rar|cb7|7z|cbt|tar|pdf|epub)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ArchiveExtension();

    [GeneratedRegex(@"^(?:19|20)\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex YearOnly();

    [GeneratedRegex(@"^(?<circle>[^()]*?)\s*\((?<artist>[^()]+)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex InnerCircleArtist();

    /// <summary>
    /// Creator hints of a display name (owner review, 1.26.1): the text of every bracket group, anywhere
    /// in the name (<c>[Family Given] Title</c>, <c>Title [Family Given]</c>, <c>Title [English Title]
    /// (Family Given)</c>; <c>[Circle (Artist)]</c> gives both names), and of unmatched brackets
    /// (<c>Family Given] Title</c>, a YACReader jump-bar convention, and <c>Title [Family Given</c>).
    /// Years, release tags, unit markers and groups without letters are skipped; a name that is
    /// nothing but tags gives none. The scorer only uses a hint when a record's authors (or its
    /// <c>(AUTHOR Name)</c> disambiguator) name it - positive evidence only - so a scan group or an
    /// English title in brackets costs nothing. Since 1.27.0 plain separators are read too, in either order:
    /// <c>Title by Author</c>, <c>Title - Chapter | Author</c>, <c>Author - Title</c> (a name-like part of 1-4 words
    /// without digits next to the separator; a subtitle that looks like a name costs nothing either).
    /// </summary>
    public static IReadOnlyList<string> CreatorHints(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return [];
        var rest = ArchiveExtension().Replace(displayName.Normalize(NormalizationForm.FormKC).Trim(), string.Empty).Trim();
        var hints = new List<string>();
        void Add(string? text)
        {
            text = text?.Trim();
            if (string.IsNullOrEmpty(text) || !text.Any(char.IsLetter) || YearOnly().IsMatch(text)
                || ArchiveNameAnatomy.IsReleaseTag(text) || VolumeToken().IsMatch(text) || ChapterToken().IsMatch(text)
                || text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 5
                || hints.Contains(text, StringComparer.OrdinalIgnoreCase))
                return;
            hints.Add(text);
        }

        for (var round = 0; round < 4; round++)
        {
            var groups = BracketGroup().Matches(rest);
            if (groups.Count == 0)
                break;
            foreach (Match g in groups)
            {
                var inner = g.Value[1..^1];
                if (InnerCircleArtist().Match(inner) is { Success: true } ca)
                {
                    Add(ca.Groups["circle"].Value); // "[Circle (Artist)]" gives both names
                    Add(ca.Groups["artist"].Value);
                }
                else
                {
                    Add(inner);
                }
            }
            rest = BracketGroup().Replace(rest, " ");
        }
        rest = TitleNormalizer.SplitUnmatchedBracketTags(rest, out var leading, out var trailing);
        Add(leading);
        Add(trailing);

        // Plain separators, either order (1.27.0): "Title by Author", "Title - Chapter | Author", "Author - Title".
        foreach (var part in SeparatorNameParts(rest))
            Add(part);
        return rest.Any(char.IsLetter) ? hints : [];
    }

    [GeneratedRegex(@"\s*\|\s*", RegexOptions.CultureInvariant)]
    private static partial Regex PipeSeparator();

    [GeneratedRegex(@"\s+by\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BySeparator();

    [GeneratedRegex(@"\s+[-\u2013\u2014]\s+", RegexOptions.CultureInvariant)]
    private static partial Regex DashSeparator();

    /// <summary>A plausible creator name next to a plain separator: 1-4 words, letters, no digits, not a category word.</summary>
    private static bool IsNameLike(string? text)
    {
        var t = text?.Trim();
        return !string.IsNullOrEmpty(t) && t.Any(char.IsLetter) && !t.Any(char.IsDigit)
            && t.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length <= 4 && IsAuthorLike(t, requireTwoTokens: false)
            && !VolumeToken().IsMatch(t) && !ChapterToken().IsMatch(t);
    }

    /// <summary>The name-like parts next to a pipe, a " by " or a spaced dash (the text after them, or the dash's first part).</summary>
    private static IEnumerable<string> SeparatorNameParts(string rest)
    {
        var pipe = PipeSeparator().Split(rest);
        foreach (var part in pipe.Skip(1))
            if (IsNameLike(part)) yield return part.Trim();
        var head = pipe[0];
        var by = BySeparator().Matches(head);
        if (by.Count > 0 && head[(by[^1].Index + by[^1].Length)..] is var after && IsNameLike(after))
            yield return after.Trim();
        var dash = DashSeparator().Split(head);
        if (dash.Length >= 2)
        {
            if (IsNameLike(dash[0])) yield return dash[0].Trim();
            if (IsNameLike(dash[^1])) yield return dash[^1].Trim();
        }
    }

    /// <summary>
    /// The title part of a name whose author is written with a plain separator (1.27.0): the text before
    /// <c> | Author</c> or <c> by Author</c>, and the text after <c>Author - </c> (a name-like first part). Empty
    /// when the name has none. Retrieval only (<see cref="QueryVariantKind.CreatorSplit"/>).
    /// </summary>
    public static IReadOnlyList<string> CreatorSplitTitles(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return [];
        var rest = ArchiveExtension().Replace(displayName.Normalize(NormalizationForm.FormKC).Trim(), string.Empty).Trim();
        rest = Bare(rest);
        var result = new List<string>();
        void Add(string? title)
        {
            var t = title?.Trim();
            if (!string.IsNullOrEmpty(t) && t.Count(char.IsLetter) >= 2 && !result.Contains(t, StringComparer.OrdinalIgnoreCase))
                result.Add(t);
        }

        var pipe = PipeSeparator().Split(rest);
        if (pipe.Length >= 2 && pipe.Skip(1).Any(IsNameLike))
            Add(pipe[0]);
        var head = pipe[0];
        var by = BySeparator().Matches(head);
        if (by.Count > 0 && IsNameLike(head[(by[^1].Index + by[^1].Length)..]))
            Add(head[..by[^1].Index]);
        // "Author - Title" only when no other form named the author, the first part is a name of 2+ words, and
        // real title text follows (not just "Chapter 012").
        var dash = DashSeparator().Match(head);
        if (result.Count == 0 && dash.Success && dash.Index > 0 && IsNameLike(head[..dash.Index])
            && head[..dash.Index].Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 2
            && ChapterToken().Replace(VolumeToken().Replace(head[(dash.Index + dash.Length)..], " "), " ").Count(char.IsLetter) >= 2)
            Add(head[(dash.Index + dash.Length)..]);
        return result;
    }

    /// <summary>The trailing <c>(disambiguator)</c> of a provider title (<c>Sprite (OOBA Douzu)</c> -> <c>OOBA Douzu</c>), or null.</summary>
    public static string? DisambiguatorTag(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var m = TrailingDisambiguator().Match(title);
        return m.Success && m.Groups["tag"].Value.Any(char.IsLetter) && m.Groups["head"].Value.Any(char.IsLetterOrDigit)
            ? m.Groups["tag"].Value.Trim()
            : null;
    }

    /// <summary>
    /// A provider title without its trailing disambiguator (MangaUpdates names same-titled records
    /// <c>Look Back (FUJIMOTO Tatsuki)</c>, <c>Jigokuraku (KAKU Yuuji)</c>); null when there is none.
    /// </summary>
    public static string? WithoutDisambiguator(string? title)
    {
        if (string.IsNullOrWhiteSpace(title))
            return null;
        var m = TrailingDisambiguator().Match(title);
        return m.Success && m.Groups["tag"].Value.Any(char.IsLetterOrDigit) && m.Groups["head"].Value.Any(char.IsLetterOrDigit)
            ? m.Groups["head"].Value.Trim()
            : null;
    }

    /// <summary>
    /// A disambiguator that names a person the MangaUpdates way (1.30.0): at least two words, one of them an upper-case family name
    /// (<c>HATA Kenjiro</c>, <c>JO Yongseok</c>) - not a format or edition note (<c>Webtoon</c>, <c>Pre-serialization</c>, <c>Novel</c>).
    /// </summary>
    public static bool IsPersonTag(string? tag) =>
        IsAuthorLike(tag, requireTwoTokens: true)
        && tag!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(w => w.Length >= 2 && w.All(char.IsLetter) && w.All(char.IsUpper));

    /// <summary>
    /// Score factor of a title that matches only once its trailing <c>(disambiguator)</c> is removed and the tag is not known to
    /// name the record's own author (1.29.0, owner): MangaUpdates adds the author to every same-named title
    /// (<c>Fly Me to the Moon (HATA Kenjiro)</c>), so the stripped alias is real evidence - but several works share the name, so
    /// on its own it stays below every automatic-link threshold and a clear margin below a record whose own title matches (an exact
    /// stripped match scores 0.88 - still "Strong" in the Identify dialog).
    /// </summary>
    public const double DisambiguatedAliasFactor = 0.88;

    /// <summary>
    /// The stripped forms of a record's OTHER titles (alternative titles, a search hit's matched title) that carry a trailing
    /// <c>(disambiguator)</c>, each with its score factor (1.29.0): 1 when the tag names one of the record's
    /// <paramref name="authors"/> (the alias is this record's own name, like the stripped main title), else
    /// <see cref="DisambiguatedAliasFactor"/> - a tag that names someone else, or authors not known yet (a search hit), can
    /// never make a clean 1.00 (1.27.0: "Word (Other Name)" is often another work's name).
    /// </summary>
    public static IReadOnlyList<(string Title, double Factor)> DisambiguatedAliases(IEnumerable<string?> otherTitles, IEnumerable<string>? authors)
    {
        var known = (authors ?? []).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
        var result = new List<(string Title, double Factor)>();
        foreach (var title in otherTitles)
        {
            if (WithoutDisambiguator(title) is not { } bare || DisambiguatorTag(title) is not { } tag)
                continue;
            var factor = known.Any(a => NamesEqual(a, tag)) ? 1.0 : DisambiguatedAliasFactor;
            var i = result.FindIndex(r => string.Equals(r.Title, bare, StringComparison.OrdinalIgnoreCase));
            if (i < 0)
                result.Add((bare, factor));
            else if (factor > result[i].Factor)
                result[i] = (bare, factor);
        }
        return result;
    }

    /// <summary>
    /// The best title similarity of a record for display ranking (the Identify dialog, 1.29.0): its main and other titles as
    /// written, the main title without its disambiguator, and the other titles' <see cref="DisambiguatedAliases"/> with their
    /// factors.
    /// </summary>
    public static double BestTitleScore(IEnumerable<string> queries, string? mainTitle, IEnumerable<string?> otherTitles, IEnumerable<string>? authors = null)
    {
        var others = otherTitles.Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => t!).ToList();
        var plain = new List<string>();
        if (!string.IsNullOrWhiteSpace(mainTitle))
            plain.Add(mainTitle);
        plain.AddRange(others);
        if (WithoutDisambiguator(mainTitle) is { } strippedMain)
            plain.Add(strippedMain);
        var queryList = queries.ToList();
        var best = TitleSimilarity.Best(queryList, plain);
        foreach (var (title, factor) in DisambiguatedAliases(others, authors))
            best = Math.Max(best, factor * TitleSimilarity.Best(queryList, [title]));
        return best;
    }

    /// <summary>The earliest <c>(19xx|20xx)</c> / <c>[19xx|20xx]</c> year in the names, or null.</summary>
    public static int? EarliestYear(IEnumerable<string> names)
    {
        int? min = null;
        foreach (var name in names)
        {
            foreach (Match m in YearGroup().Matches(name ?? string.Empty))
            {
                var y = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                if (min is null || y < min) min = y;
            }
        }
        return min;
    }

    /// <summary>An archive name that names a volume (a volume token and no chapter token).</summary>
    public static bool IsVolumeLike(string? archiveName) =>
        archiveName is not null && VolumeToken().IsMatch(archiveName) && !ChapterToken().IsMatch(archiveName);

    /// <summary>
    /// An archive name that names a chapter: a chapter token, or a unit-named archive without a
    /// volume token (<c>001 [chapter title]</c>).
    /// </summary>
    public static bool IsChapterLike(string? archiveName)
    {
        if (archiveName is null)
            return false;
        if (ChapterToken().IsMatch(archiveName))
            return true;
        return !VolumeToken().IsMatch(archiveName) && TitleNormalizer.ArchiveBaseTitle(archiveName).Length == 0
            && archiveName.Any(char.IsDigit);
    }

    // Unit numbers (1.27.0 count rule): the number after a volume / chapter token, the upper end of a range.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:v|vol|vols|volume|volumes)\.?\s*(?<n>\d{1,4})(?:\.\d+)?(?:\s*-\s*(?<m>\d{1,4})(?:\.\d+)?)?(?![\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeNumber();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?:ch|chap|chapter|chapters|ep|episode)\.?\s*|c|#\s*)(?<n>\d{1,4})(?:\.\d+)?(?:\s*-\s*(?<m>\d{1,4})(?:\.\d+)?)?(?![\p{N}])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterNumber();

    [GeneratedRegex(@"^\s*(?<n>\d{1,4})(?:\.\d+)?(?![\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingNumber();

    /// <summary>
    /// The highest volume number a volume-like archive name states (<c>Title v03</c> -> 3, <c>Vol. 01-05</c> -> 5,
    /// <c>v02.5</c> -> 2, so an extra never inflates it), or null.
    /// </summary>
    public static int? VolumeNumberOf(string? archiveName) =>
        archiveName is null || !IsVolumeLike(archiveName) ? null : HighestNumber(VolumeNumber().Matches(archiveName));

    /// <summary>
    /// The highest chapter number a chapter-like archive name states (<c>Title - Chapter 012</c> -> 12,
    /// <c>c045.5</c> -> 45, <c>001 [Chapter Title]</c> -> 1), or null. A leading 19xx / 20xx is a year, not a chapter.
    /// </summary>
    public static int? ChapterNumberOf(string? archiveName)
    {
        if (archiveName is null || !IsChapterLike(archiveName))
            return null;
        if (HighestNumber(ChapterNumber().Matches(archiveName)) is { } n)
            return n;
        var bare = Bare(ArchiveExtension().Replace(archiveName, string.Empty));
        return LeadingNumber().Match(bare) is { Success: true } m && !YearOnly().IsMatch(m.Groups["n"].Value)
            ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    /// <summary>
    /// The bare leading number of a name without a volume / chapter token and without a title (<c>01.cbz</c>,
    /// <c>012 [Title]</c>) - the unit number of an archive inside a <c>Volumes</c> / <c>Chapters</c> subfolder - or null.
    /// </summary>
    public static int? BareNumberOf(string? archiveName)
    {
        if (archiveName is null || VolumeToken().IsMatch(archiveName) || ChapterToken().IsMatch(archiveName) || !IsChapterLike(archiveName))
            return null;
        var bare = Bare(ArchiveExtension().Replace(archiveName, string.Empty));
        return LeadingNumber().Match(bare) is { Success: true } m && !YearOnly().IsMatch(m.Groups["n"].Value)
            ? int.Parse(m.Groups["n"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    private static int? HighestNumber(MatchCollection matches)
    {
        int? best = null;
        foreach (Match m in matches)
        {
            var value = int.Parse(m.Groups["m"].Success ? m.Groups["m"].Value : m.Groups["n"].Value, CultureInfo.InvariantCulture);
            if (best is null || value > best)
                best = value;
        }
        return best;
    }

    // Unit numbers v2 (1.29.0): decimals kept, a range as start / end (the end may repeat the token: "v01-v05").
    // <t> is the token, so a bracketed single-letter token ("[v2]", a release revision) can be told apart.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?<t>volumes|volume|vols|vol|v)\.?\s*(?<n>\d{1,4}(?:\.\d{1,2})?)(?:\s*-\s*(?:(?:volumes|volume|vols|vol|v)\.?\s*)?(?<m>\d{1,4}(?:\.\d{1,2})?))?(?![\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeUnit();

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?<t>chapters|chapter|chap|ch|episode|ep)\.?\s*|(?<t>c)|(?<t>#)\s*)(?<n>\d{1,4}(?:\.\d{1,2})?)(?:\s*-\s*(?:(?:chapters|chapter|chap|ch|episode|ep)\.?\s*|c|#\s*)?(?<m>\d{1,4}(?:\.\d{1,2})?))?(?![\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterUnit();

    [GeneratedRegex(@"^\s*(?<n>\d{1,4}(?:\.\d{1,2})?)(?:\s*-\s*(?<m>\d{1,4}(?:\.\d{1,2})?))?(?![\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingUnit();

    /// <summary>
    /// Every unit number an archive name states (1.29.0; <see cref="UnitNumbers"/>): <c>Title v03 c012</c> -> volume 3,
    /// chapter 12; <c>c045.5</c> -> chapter 45.5, an extra; <c>Vol. 01-05</c> -> volumes 1 to 5; <c>001 [Chapter Title]</c>
    /// -> chapter 1 (a bare leading number of a name without a title, as <see cref="ChapterNumberOf"/>; a leading 19xx /
    /// 20xx is a year). Tokens inside brackets are read only when the rest of the name states none, and then never a
    /// single-letter token (<c>[v2]</c> is a release revision). A range whose end is a year is one number. Unlike
    /// <see cref="VolumeNumberOf"/> / <see cref="ChapterNumberOf"/> (the matcher's integers, unchanged), nothing is
    /// truncated and a name states both kinds.
    /// </summary>
    public static UnitNumbers UnitsOf(string? archiveName)
    {
        if (string.IsNullOrWhiteSpace(archiveName))
            return default;
        var name = ArchiveExtension().Replace(archiveName.Normalize(NormalizationForm.FormKC).Trim(), string.Empty);
        var outside = Bare(name);

        var volume = RangeOf(VolumeUnit().Matches(outside), allowShortToken: true);
        var chapter = RangeOf(ChapterUnits(outside, volume is not null), allowShortToken: true);
        if (volume is null && chapter is null)
        {
            // Only brackets name a unit ("Title (Vol. 3)"); a single letter there is a revision, not a unit.
            volume = RangeOf(VolumeUnit().Matches(name), allowShortToken: false);
            chapter = RangeOf(ChapterUnits(name, volume is not null), allowShortToken: false);
        }
        if (volume is null && chapter is null && IsChapterLike(archiveName)
            && LeadingUnit().Match(outside) is { Success: true } lead && !YearOnly().IsMatch(lead.Groups["n"].Value))
        {
            chapter = RangeOf([lead], allowShortToken: true);
        }

        var extra = chapter is { } c ? decimal.Truncate(c.Start) != c.Start
            : volume is { } v && decimal.Truncate(v.Start) != v.Start;
        return new UnitNumbers(volume?.Start, volume?.End, chapter?.Start, chapter?.End, extra);
    }

    /// <summary>
    /// The chapter tokens of a name (1.31.1): "Episode N" / "Ep N" next to a VOLUME token names a part or arc of the series, not a
    /// chapter (<c>Title - Episode 1 - Arc Title v01 (2-in-1 Edition)</c>: each arc's volumes restart at 1, like Season / Part
    /// folders), so it is not read as a chapter there; without a volume token it stays a chapter (webtoons: <c>Episode 45</c>).
    /// </summary>
    private static IEnumerable<Match> ChapterUnits(string text, bool statesVolume) =>
        ChapterUnit().Matches(text).Where(m => !(statesVolume && m.Groups["t"].Value.StartsWith("ep", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The lowest start and the highest end over the matches; the end is null when it is not above the start.</summary>
    private static (decimal Start, decimal? End)? RangeOf(IEnumerable<Match> matches, bool allowShortToken)
    {
        decimal? start = null, end = null;
        foreach (var m in matches)
        {
            if (!allowShortToken && m.Groups["t"] is { Success: true } t && t.Value.Length == 1)
                continue;
            var n = decimal.Parse(m.Groups["n"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
            var high = n;
            if (m.Groups["m"].Success)
            {
                var e = decimal.Parse(m.Groups["m"].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture);
                // "Title v03 - 2019": a year, not the end of a range.
                if (e > n && !(n < 1900 && YearOnly().IsMatch(m.Groups["m"].Value)))
                    high = e;
            }
            start = start is null ? n : Math.Min(start.Value, n);
            end = end is null ? high : Math.Max(end.Value, high);
        }
        return start is null ? null : (start.Value, end > start ? end : null);
    }

    /// <summary>
    /// The origins a category hint allows: <c>manga</c> -> Japan, <c>manhwa</c> -> Korea,
    /// <c>manhua</c> -> China/Taiwan, <c>webtoon(s)</c> -> Korea or China/Taiwan; 1.32.0 comics words by language:
    /// <c>bd</c> / <c>bande(s) dessinee(s)</c> -> French (which covers Belgium), <c>tebeos</c> / <c>historietas</c> -> Spanish,
    /// <c>fumetti</c> -> Italian, <c>stripboeken</c> -> Dutch, <c>us comics</c> -> English-original (no US / UK split). Null when
    /// the hint says nothing about origin (<c>comics</c>, <c>graphic novels</c>, <c>european comics</c>...).
    /// </summary>
    public static IReadOnlySet<MetadataOrigin>? OriginsForCategory(string? categoryHint) =>
        TitleNormalizer.ScoringForm(categoryHint) switch
        {
            "manga" or "japanese manga" => new HashSet<MetadataOrigin> { MetadataOrigin.Japan },
            "manhwa" or "korean manhwa" => new HashSet<MetadataOrigin> { MetadataOrigin.Korea },
            "manhua" or "chinese manhua" => new HashSet<MetadataOrigin> { MetadataOrigin.ChinaTaiwan },
            "webtoon" or "webtoons" => new HashSet<MetadataOrigin> { MetadataOrigin.Korea, MetadataOrigin.ChinaTaiwan },
            "bd" or "bande dessinee" or "bandes dessinees" => new HashSet<MetadataOrigin> { MetadataOrigin.French },
            "tebeos" or "historietas" => new HashSet<MetadataOrigin> { MetadataOrigin.Spanish },
            "fumetti" => new HashSet<MetadataOrigin> { MetadataOrigin.Italian },
            "stripboeken" => new HashSet<MetadataOrigin> { MetadataOrigin.Dutch },
            "us comics" => new HashSet<MetadataOrigin> { MetadataOrigin.EnglishOriginal },
            _ => null,
        };

    /// <summary>
    /// Reads a candidate origin: a provider type ("Manga", "Manhwa", "Manhua", "OEL") or a
    /// <see cref="MetadataOrigin"/> name. Null when unknown or a format word.
    /// </summary>
    public static MetadataOrigin? ParseOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin))
            return null;
        var s = origin.Trim();
        if (Enum.TryParse<MetadataOrigin>(s, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            && !s.All(char.IsDigit))
            return parsed;
        return s.ToLowerInvariant() switch
        {
            "manga" => MetadataOrigin.Japan,
            "manhwa" => MetadataOrigin.Korea,
            "manhua" => MetadataOrigin.ChinaTaiwan,
            "oel" => MetadataOrigin.EnglishOriginal,
            _ => null,
        };
    }

    private static string Bare(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return string.Empty;
        var s = name.Normalize(NormalizationForm.FormKC);
        for (var i = 0; i < 3; i++)
        {
            var next = BracketGroup().Replace(s, " ");
            if (next == s) break;
            s = next;
        }
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim(' ', '-', '_', '.');
    }
}
