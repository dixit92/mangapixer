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
    /// (<see cref="OriginsForCategory"/>); the hint only ever ADDS evidence (owner option a', 2026-09-27).
    /// </summary>
    public static IReadOnlySet<string> CategoryFolderWords { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "manga", "manhwa", "manhua", "webtoon", "webtoons", "comic", "comics", "doujin", "doujinshi",
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
    public static bool IsCategoryFolderName(string? name) => name is not null && CategoryFolderWords.Contains(name.Trim());

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
    /// English title in brackets costs nothing. Plain separators (<c>Author - Title</c>) are not read yet.
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
        return rest.Any(char.IsLetter) ? hints : [];
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

    /// <summary>
    /// The origins a category hint allows: <c>manga</c> -> Japan, <c>manhwa</c> -> Korea,
    /// <c>manhua</c> -> China/Taiwan, <c>webtoon(s)</c> -> Korea or China/Taiwan. Null when the
    /// hint says nothing about origin.
    /// </summary>
    public static IReadOnlySet<MetadataOrigin>? OriginsForCategory(string? categoryHint) =>
        TitleNormalizer.ScoringForm(categoryHint) switch
        {
            "manga" or "japanese manga" => new HashSet<MetadataOrigin> { MetadataOrigin.Japan },
            "manhwa" or "korean manhwa" => new HashSet<MetadataOrigin> { MetadataOrigin.Korea },
            "manhua" or "chinese manhua" => new HashSet<MetadataOrigin> { MetadataOrigin.ChinaTaiwan },
            "webtoon" or "webtoons" => new HashSet<MetadataOrigin> { MetadataOrigin.Korea, MetadataOrigin.ChinaTaiwan },
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
