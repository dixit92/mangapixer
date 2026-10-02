namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The local signs that a work is a Western comic / graphic novel / BD album (1.32.0 step 0; owner 2026-09-30: comic-like
/// works are searched on the Grand Comics Database first, else MangaUpdates). Read from local data only - nothing is sent to
/// compute them. Lane A (comics signals) adds the detectors; lane B (GCD provider) routes on <see cref="ComicsSignal.RoutesToComics"/>.
/// </summary>
[Flags]
public enum ComicsSignalKind
{
    None = 0,

    /// <summary>An admin declared the folder Comic or Graphic novel (strong).</summary>
    DeclaredType = 1 << 0,

    /// <summary>An ancestor folder is a comics category word - Comics, BD, Fumetti... (strong).</summary>
    CategoryFolder = 1 << 1,

    /// <summary>ComicInfo <c>Web</c> / <c>Notes</c> names a Comic Vine, Metron or GCD record (strong).</summary>
    ComicsIdInComicInfo = 1 << 2,

    /// <summary>ComicInfo <c>Publisher</c> is a known Western comics publisher (strong).</summary>
    WesternPublisher = 1 << 3,

    /// <summary>Archive names numbered like comic issues: <c>#12</c>, <c>Issue 12</c>, <c>12 (of 6)</c>, <c>Annual 2</c> (weak).</summary>
    IssueNumbering = 1 << 4,

    /// <summary>Archive names numbered like BD / European albums: <c>Tome 3</c>, <c>T03</c>, <c>Band 3</c>, <c>Deel 3</c> (weak).</summary>
    AlbumNumbering = 1 << 5,

    /// <summary>A <c>(YYYY)</c> start year right after the series name, the comics taggers' convention (weak).</summary>
    StartYearAfterName = 1 << 6,

    /// <summary>Collected-edition format words: TPB, HC, OGN, Omnibus, Integrale... (weak).</summary>
    CollectedFormatWord = 1 << 7,
}

/// <summary>
/// The page-count shape of a work's archives (1.32.0, lane A): scoring evidence for the comics provider (an issue run vs a
/// collected book), NEVER routing on its own - scanlated manga chapters are short too.
/// </summary>
public enum ComicsPageShape
{
    /// <summary>No page counts, or a median between the two bounds.</summary>
    Unknown = 0,

    /// <summary>Median at most <see cref="ComicsSignals.IssuePagesMax"/> pages: single issues (or manga chapters).</summary>
    Issues = 1,

    /// <summary>Median at least <see cref="ComicsSignals.CollectedPagesMin"/> pages: collected books (TPB / GN / album / tankobon).</summary>
    Collected = 2,
}

/// <summary>The comics database a local id names.</summary>
public enum ComicsIdSite
{
    ComicVine = 0,
    Metron = 1,
    Gcd = 2,
}

/// <summary>What a local comics id names: a series (a Comic Vine "volume" = a run) or one issue.</summary>
public enum ComicsIdKind
{
    Unknown = 0,
    Series = 1,
    Issue = 2,
}

/// <summary>
/// A comics database id found in local data (ComicInfo <c>Web</c> / <c>Notes</c>, or a <c>[cv-123]</c> folder tag). Read
/// only; turning a GCD id into a lookup is the GCD provider's (tier 0), a Comic Vine / Metron id is display-only.
/// <c>Id</c> is the site's id as written (digits, or a Metron slug).
/// </summary>
public sealed record ComicsId(ComicsIdSite Site, ComicsIdKind Kind, string Id);

/// <summary>The comics signs found for one work.</summary>
public sealed record ComicsSignal(ComicsSignalKind Kinds)
{
    public static ComicsSignal None { get; } = new(ComicsSignalKind.None);

    public const ComicsSignalKind Strong = ComicsSignalKind.DeclaredType | ComicsSignalKind.CategoryFolder
        | ComicsSignalKind.ComicsIdInComicInfo | ComicsSignalKind.WesternPublisher;

    public const ComicsSignalKind Weak = ComicsSignalKind.IssueNumbering | ComicsSignalKind.AlbumNumbering
        | ComicsSignalKind.StartYearAfterName | ComicsSignalKind.CollectedFormatWord;

    /// <summary>
    /// Whether the work is searched on the comics site first. One strong sign is enough; weak signs route only two
    /// together - alone they are scoring evidence. (1.32.0 kickoff measurement on the owner's live library: the only
    /// folders with <c>#N</c>-numbered files were manga, so issue numbering alone must not send a manga name to GCD.)
    /// </summary>
    public bool RoutesToComics => (Kinds & Strong) != 0 || BitOperations.PopCount((uint)(Kinds & Weak)) >= 2;

    /// <summary>The page-count shape of the work's archives (scoring evidence only; lane A).</summary>
    public ComicsPageShape PageShape { get; init; }

    /// <summary>The run's start year from a <c>Title (YYYY)</c> name (<see cref="ComicsSignalKind.StartYearAfterName"/>), else null.</summary>
    public int? StartYear { get; init; }

    /// <summary>Comics database ids found locally (<see cref="ComicsSignalKind.ComicsIdInComicInfo"/>), at most a few, de-duplicated.</summary>
    public IReadOnlyList<ComicsId> Ids { get; init; } = [];
}

/// <summary>
/// What <see cref="ComicsSignals.Of"/> reads - display names and stored local metadata only, never paths.
/// <c>ComicInfoPublisher</c> / <c>ComicInfoImprint</c>: the majority value over the work's archives; <c>ComicInfoWebUrls</c>:
/// every value; <c>ComicInfoNotes</c>: the notes of every archive, joined by new lines (an id in any archive counts); <c>ComicInfoSaysManga</c>: most archives with ComicInfo set
/// <c>Manga</c> to Yes / YesAndRightToLeft; <c>FolderName</c>: the work folder's display name (lane A, 1.32.0).
/// </summary>
public sealed record ComicsSignalInput(
    DeclaredType? DeclaredType,
    string? CategoryHint,
    IReadOnlyList<string> ArchiveNames,
    string? ComicInfoPublisher = null,
    IReadOnlyList<string>? ComicInfoWebUrls = null,
    string? ComicInfoNotes = null,
    int? MedianPageCount = null,
    string? FolderName = null,
    string? ComicInfoImprint = null,
    bool ComicInfoSaysManga = false);

/// <summary>
/// Detects <see cref="ComicsSignal"/>s. Pure and deterministic. The rules keep a manga library where it is (1.32.0 kickoff
/// measurement: the owner's library is nearly all manga):
/// <list type="bullet">
/// <item>a declared manga / manhwa / manhua / webtoon / novel type silences every sign (the declaration wins);</item>
/// <item>a manga-side category folder (manga, manhwa, manhua, webtoon(s), doujin(shi)) and ComicInfo <c>Manga = Yes</c>
/// silence the weak signs and the publisher - only a comics id stays;</item>
/// <item>name grammar counts when at least half of the archives (or the folder name) carry it, so one odd file never decides;</item>
/// <item>tokens that manga share (<c>Vol</c>, <c>Book 1</c>, <c>#12</c> alone, <c>Omnibus</c> alone) are weak at most.</item>
/// </list>
/// </summary>
public static partial class ComicsSignals
{
    /// <summary>A median at or below this many pages is an issues shape (ComicTagger: a single issue is ~20-40 pages).</summary>
    public const int IssuePagesMax = 48;

    /// <summary>A median at or above this many pages is a collected shape (ComicTagger's TPB rule: more than 100 pages).</summary>
    public const int CollectedPagesMin = 100;

    /// <summary>At most this many ids are kept per work.</summary>
    public const int MaxIds = 5;

    // --- issue grammar (weak): #12, #12.1, #1/2, #½, Issue 12, No. 12 / N°12 after a title, 12 (of 6), Annual 2, FCBD ---
    [GeneratedRegex(@"(?:(?<![\p{L}\p{N}])#\s*\d{1,4}(?:\.\d{1,2})?|(?<![\p{L}\p{N}])issue\s*#?\s*\d{1,4}|(?<=[\p{L}\p{N}][\s\-_.,]*)(?:no\.|n[°º])\s*\d{1,4}|(?<![\p{L}\p{N}])annual(?:\s*#?\s*\d{1,4})?(?![\p{L}])|(?<![\p{L}\p{N}])(?:fcbd|free\s+comic\s+book\s+day)(?![\p{L}]))",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IssueToken();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.])\d{1,4}\s*\(\s*of\s*\d{1,4}\s*\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OfCount();

    // --- album grammar (weak): Tome / Tomo / Band / Deel / Album / Livre N, T12, Book One (a SPELLED number: Book 1 is manga's) ---
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:(?:tome|tomo|band|deel|album|livre)\.?\s*\d{1,4}|book\s+(?:one|two|three|four|five|six|seven|eight|nine|ten|eleven|twelve))(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AlbumWordToken();

    // T12 / T01: an upper-case T glued to the number (French BD scans); case-sensitive so "t1" in a word never counts.
    [GeneratedRegex(@"(?<![\p{L}\p{N}])T\d{1,3}(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex AlbumTToken();

    // --- collected-format words (weak; Omnibus / Deluxe / Absolute are manga edition words too) ---
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:tpb|ogn|trade\s+paperback|omnibus|absolute|deluxe|compendium|library\s+edition|int[eé]grale|gesamtausgabe|integraal)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FormatWord();

    // HC / GN only upper-case and standalone (two letters are too common inside other words and tags).
    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:HC|GN)(?![\p{L}\p{N}])", RegexOptions.CultureInvariant)]
    private static partial Regex ShortFormatWord();

    // A (YYYY) group RIGHT after the title text: "Saga (2012)", "Saga (2012) (Digital)", "Saga (2012) 001".
    [GeneratedRegex(@"^\s*(?<title>[^\[\](){}#]*?\p{L}[^\[\](){}#]*?)\s*\((?<year>19\d{2}|20\d{2})\)", RegexOptions.CultureInvariant)]
    private static partial Regex TitleThenYear();

    // --- comics ids (strong) ---
    [GeneratedRegex(@"^https?://(?:www\.)?comicvine(?:\.gamespot)?\.com/(?:[^/?#]+/)*(?<type>4050|4000)-(?<id>\d{1,9})(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ComicVineUrl();

    [GeneratedRegex(@"^https?://(?:www\.)?metron\.cloud/(?<type>series|issue)/(?<id>[a-z0-9][a-z0-9\-]{0,120})/?(?:[?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MetronUrl();

    [GeneratedRegex(@"^https?://(?:www\.)?comics\.org/(?<type>series|issue)/(?<id>\d{1,9})(?:[/?#]|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GcdUrl();

    // ComicRack CV Scraper "[CVDB123]" (CVDBSKIP is "do not match", not an id), Komf "[cv-123]" / "[gcd-123]", Metron-Tagger
    // "[issue_id:123]", ComicTagger "... Comic Vine ... [Issue ID 123]", comicbox "urn:comicvine:issue:123".
    [GeneratedRegex(@"CVDB(?<cv>\d{1,9})|\[\s*cv-(?<cv>\d{1,9})\s*\]|\[\s*gcd-(?<gcd>\d{1,9})\s*\]|\[\s*issue_id\s*:\s*(?<metron>\d{1,9})\s*\]|urn:(?<urnsite>comicvine|metron|gcd):(?<urntype>issue|series|volume):(?<urnid>[a-z0-9\-]{1,120})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteId();

    [GeneratedRegex(@"comic\s*vine.*?\[\s*issue\s*id\s*(?<id>\d{1,9})\s*\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex ComicTaggerNote();

    [GeneratedRegex(@"\[[^\[\]]*\]|\([^()]*\)|\{[^{}]*\}", RegexOptions.CultureInvariant)]
    private static partial Regex BracketGroup();

    /// <summary>
    /// Comics category words (positive-only, whole folder name, by scoring form). They are also
    /// <see cref="AutoMatchText.CategoryFolderWords"/> (the one category list), so they never become a creator.
    /// </summary>
    public static IReadOnlySet<string> CategoryWords { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "comic", "comics", "comic books", "graphic novel", "graphic novels", "bd", "bande dessinee", "bandes dessinees",
        "fumetti", "tebeos", "historietas", "stripboeken", "us comics", "european comics", "eurocomics",
    };

    /// <summary>
    /// Western comics publishers (ComicInfo <c>Publisher</c>, whole value by scoring form) - a strong sign. Left out on
    /// purpose: Dark Horse and Drawn &amp; Quarterly (large manga lines: Berserk, Mizuki...), bare Glenat and Panini
    /// (Glenat Manga, Panini Manga / Planet Manga) - a manga under them must not be sent to the comics site first. An
    /// imprint naming manga (<c>Planet Manga</c>, <c>Sakka</c>) cancels the sign.
    /// </summary>
    public static IReadOnlySet<string> WesternPublishers { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        // US: the big two and the large independents (no manga lines).
        "marvel", "marvel comics", "dc", "dc comics", "image", "image comics", "idw", "idw publishing",
        "boom", "boom studios", "boom box", "dynamite", "dynamite entertainment", "oni", "oni press",
        "fantagraphics", "fantagraphics books", "top shelf", "top shelf productions",
        // Franco-Belgian BD houses (their manga lines publish under other names: Kana, Ki-oon, Tonkam, Sakka).
        "dargaud", "dupuis", "casterman", "le lombard", "lombard", "glenat bd", "delcourt", "soleil",
        // Italian, the Panini comics arm (not Panini Manga), Flemish.
        "bonelli", "sergio bonelli", "sergio bonelli editore", "panini comics", "standaard", "standaard uitgeverij",
    };

    private static readonly HashSet<string> s_mangaCategories = new(StringComparer.Ordinal)
    {
        "manga", "manhwa", "manhua", "webtoon", "webtoons", "doujin", "doujinshi", "dojin", "dojinshi",
    };

    /// <summary>The comics signs of a work. See the class remarks for the rules that keep manga where it is.</summary>
    public static ComicsSignal Of(ComicsSignalInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var declared = input.DeclaredType;
        var declaredComics = declared is Metadata.DeclaredType.Comic or Metadata.DeclaredType.GraphicNovel;
        // An admin's declaration of another type wins over every local sign.
        if (declared is not null && !declaredComics)
            return ComicsSignal.None;
        var ids = IdsOf(input.ComicInfoWebUrls, input.ComicInfoNotes is { } notes ? [notes] : [], input.FolderName);

        var kinds = ComicsSignalKind.None;
        if (declaredComics)
            kinds |= ComicsSignalKind.DeclaredType;
        if (ids.Count > 0)
            kinds |= ComicsSignalKind.ComicsIdInComicInfo;

        // The category hint is not read while a type is declared (the declaration wins, as in the scorer).
        var category = declared is null ? TitleNormalizer.ScoringForm(input.CategoryHint) : string.Empty;
        if (CategoryWords.Contains(category))
            kinds |= ComicsSignalKind.CategoryFolder;
        var mangaSide = s_mangaCategories.Contains(category) || input.ComicInfoSaysManga;

        int? startYear = null;
        if (!mangaSide)
        {
            if (IsWesternPublisher(input.ComicInfoPublisher, input.ComicInfoImprint))
                kinds |= ComicsSignalKind.WesternPublisher;
            var names = input.ArchiveNames ?? [];
            if (Carried(names, input.FolderName, IsIssueNamed, folderCounts: false))
                kinds |= ComicsSignalKind.IssueNumbering;
            if (Carried(names, input.FolderName, IsAlbumNamed, folderCounts: false))
                kinds |= ComicsSignalKind.AlbumNumbering;
            if (Carried(names, input.FolderName, HasCollectedFormatWord, folderCounts: true))
                kinds |= ComicsSignalKind.CollectedFormatWord;
            startYear = StartYearOf(input.FolderName, names);
            if (startYear is not null)
                kinds |= ComicsSignalKind.StartYearAfterName;
        }
        return Build(kinds, input, ids, startYear);
    }

    private static ComicsSignal Build(ComicsSignalKind kinds, ComicsSignalInput input, IReadOnlyList<ComicsId> ids, int? startYear) =>
        kinds == ComicsSignalKind.None
            ? ComicsSignal.None
            : new ComicsSignal(kinds) { PageShape = PageShapeOf(input.MedianPageCount), StartYear = startYear, Ids = ids };

    /// <summary>Whether a category hint (an ancestor folder's whole name) names comics.</summary>
    public static bool IsComicsCategory(string? categoryHint) => CategoryWords.Contains(TitleNormalizer.ScoringForm(categoryHint));

    /// <summary>The page-count shape of a median page count (<see cref="ComicsPageShape"/>).</summary>
    public static ComicsPageShape PageShapeOf(int? medianPageCount) => medianPageCount switch
    {
        null or <= 0 => ComicsPageShape.Unknown,
        <= IssuePagesMax => ComicsPageShape.Issues,
        >= CollectedPagesMin => ComicsPageShape.Collected,
        _ => ComicsPageShape.Unknown,
    };

    /// <summary>The median of the known page counts (the lower middle of an even count), or null when there are none.</summary>
    public static int? MedianOf(IEnumerable<int?> pageCounts)
    {
        var known = pageCounts.Where(p => p is > 0).Select(p => p!.Value).Order().ToList();
        return known.Count == 0 ? null : known[(known.Count - 1) / 2];
    }

    /// <summary>
    /// A ComicInfo publisher that names a Western comics house (<see cref="WesternPublishers"/>), unless the imprint names
    /// manga (<c>Panini Comics</c> / <c>Planet Manga</c>, <c>Casterman</c> / <c>Sakka</c>).
    /// </summary>
    public static bool IsWesternPublisher(string? publisher, string? imprint = null)
    {
        if (!WesternPublishers.Contains(TitleNormalizer.ScoringForm(publisher)))
            return false;
        var imprintForm = TitleNormalizer.ScoringForm(imprint);
        return !(imprintForm.Length > 0 && (AutoMatchText.ContainsTokens(imprintForm, "manga") || imprintForm == "sakka"));
    }

    /// <summary>An archive name numbered like a comic issue (<c>Saga #012</c>, <c>Saga Issue 12</c>, <c>Saga 3 (of 6)</c>, <c>Annual 2</c>).</summary>
    public static bool IsIssueNamed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var s = name.Normalize(NormalizationForm.FormKC);
        return OfCount().IsMatch(s) || IssueToken().IsMatch(Outside(s));
    }

    /// <summary>An archive name numbered like a BD / European album (<c>Tome 3</c>, <c>T03</c>, <c>Band 3</c>, <c>Book One</c>).</summary>
    public static bool IsAlbumNamed(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var s = Outside(name.Normalize(NormalizationForm.FormKC));
        return AlbumWordToken().IsMatch(s) || AlbumTToken().IsMatch(s);
    }

    /// <summary>A name carrying a collected-edition format word (TPB, HC, OGN, GN, Omnibus, Integrale... - in brackets too).</summary>
    public static bool HasCollectedFormatWord(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;
        var s = name.Normalize(NormalizationForm.FormKC);
        return FormatWord().IsMatch(s) || ShortFormatWord().IsMatch(s);
    }

    /// <summary>
    /// The start year of a <c>Title (YYYY)</c> name: the folder name, else the year most archive names carry right after
    /// their title (<c>Saga (2012) 001</c>). A year after an issue number (<c>Saga 001 (2012)</c>, a cover date) does not count.
    /// </summary>
    public static int? StartYearOf(string? folderName, IReadOnlyList<string> archiveNames)
    {
        if (TitleYear(folderName) is { } folderYear)
            return folderYear;
        var years = archiveNames.Select(TitleYear).ToList();
        if (years.Count == 0)
            return null;
        var top = years.Where(y => y is not null).GroupBy(y => y!.Value).OrderByDescending(g => g.Count()).ThenBy(g => g.Key).FirstOrDefault();
        return top is not null && top.Count() * 2 >= years.Count ? top.Key : null;
    }

    private static int? TitleYear(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;
        var m = TitleThenYear().Match(name.Normalize(NormalizationForm.FormKC));
        // The title must not end with a unit number ("Saga 001 (2012)": the year is the issue's cover date).
        if (!m.Success || Regex.IsMatch(m.Groups["title"].Value, @"\d\s*$", RegexOptions.CultureInvariant))
            return null;
        return int.Parse(m.Groups["year"].Value, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// The comics ids in ComicInfo <c>Web</c> URLs, <c>Notes</c> and a folder tag, de-duplicated in order (at most
    /// <see cref="MaxIds"/>). <c>CVDBSKIP</c> is not an id.
    /// </summary>
    public static IReadOnlyList<ComicsId> IdsOf(IEnumerable<string>? webUrls, IEnumerable<string>? notes, string? folderName = null)
    {
        var ids = new List<ComicsId>();
        void Add(ComicsId id)
        {
            if (ids.Count < MaxIds && !ids.Contains(id))
                ids.Add(id);
        }
        foreach (var url in webUrls ?? [])
        {
            if (ParseUrl(url) is { } id)
                Add(id);
        }
        foreach (var note in (notes ?? []).Append(folderName))
        {
            if (string.IsNullOrWhiteSpace(note))
                continue;
            foreach (var id in ParseNote(note))
                Add(id);
        }
        return ids;
    }

    /// <summary>A Comic Vine (<c>.../4050-123/</c> series, <c>4000-</c> issue), Metron or GCD record URL, or null.</summary>
    public static ComicsId? ParseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        var s = url.Trim();
        if (ComicVineUrl().Match(s) is { Success: true } cv)
            return new ComicsId(ComicsIdSite.ComicVine, cv.Groups["type"].Value == "4050" ? ComicsIdKind.Series : ComicsIdKind.Issue, cv.Groups["id"].Value);
        if (MetronUrl().Match(s) is { Success: true } metron)
            return new ComicsId(ComicsIdSite.Metron, KindOf(metron.Groups["type"].Value), metron.Groups["id"].Value.ToLowerInvariant());
        if (GcdUrl().Match(s) is { Success: true } gcd)
            return new ComicsId(ComicsIdSite.Gcd, KindOf(gcd.Groups["type"].Value), gcd.Groups["id"].Value);
        return null;
    }

    private static IEnumerable<ComicsId> ParseNote(string note)
    {
        var s = note.Normalize(NormalizationForm.FormKC);
        foreach (Match m in NoteId().Matches(s))
        {
            if (m.Groups["cv"].Success)
                yield return new ComicsId(ComicsIdSite.ComicVine, ComicsIdKind.Unknown, m.Groups["cv"].Value);
            else if (m.Groups["gcd"].Success)
                yield return new ComicsId(ComicsIdSite.Gcd, ComicsIdKind.Unknown, m.Groups["gcd"].Value);
            else if (m.Groups["metron"].Success)
                yield return new ComicsId(ComicsIdSite.Metron, ComicsIdKind.Issue, m.Groups["metron"].Value);
            else if (m.Groups["urnsite"].Success)
            {
                var site = m.Groups["urnsite"].Value.ToLowerInvariant() switch
                {
                    "comicvine" => ComicsIdSite.ComicVine,
                    "metron" => ComicsIdSite.Metron,
                    _ => ComicsIdSite.Gcd,
                };
                yield return new ComicsId(site, KindOf(m.Groups["urntype"].Value), m.Groups["urnid"].Value.ToLowerInvariant());
            }
        }
        if (ComicTaggerNote().Match(s) is { Success: true } tagged)
            yield return new ComicsId(ComicsIdSite.ComicVine, ComicsIdKind.Issue, tagged.Groups["id"].Value);
    }

    private static ComicsIdKind KindOf(string type) => type.ToLowerInvariant() switch
    {
        "series" or "volume" => ComicsIdKind.Series,
        "issue" => ComicsIdKind.Issue,
        _ => ComicsIdKind.Unknown,
    };

    /// <summary>
    /// A grammar the work carries: at least half of the archive names (at least one), or - for <paramref name="folderCounts"/> -
    /// the folder name. A work without archives (a folder of folders) reads the folder name only when it may.
    /// </summary>
    private static bool Carried(IReadOnlyList<string> names, string? folderName, Func<string?, bool> test, bool folderCounts)
    {
        if (folderCounts && test(folderName))
            return true;
        if (names.Count == 0)
            return false;
        var hits = names.Count(test);
        return hits > 0 && hits * 2 >= names.Count;
    }

    // The name without its bracket groups (tags never carry the issue / album grammar, except "(of 6)").
    private static string Outside(string s)
    {
        for (var i = 0; i < 3; i++)
        {
            var next = BracketGroup().Replace(s, " ");
            if (next == s) break;
            s = next;
        }
        return s;
    }
}
