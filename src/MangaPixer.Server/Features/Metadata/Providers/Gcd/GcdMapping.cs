namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>What a GCD record keeps in <c>metadata_records.ExtraJson</c> (1.32.0): the raw country, language, format and publisher id.</summary>
public sealed record GcdExtra(
    [property: JsonPropertyName("country")] string? Country,
    [property: JsonPropertyName("language")] string? Language,
    [property: JsonPropertyName("format")] string? Format,
    [property: JsonPropertyName("binding")] string? Binding,
    [property: JsonPropertyName("shape")] string? Shape,
    [property: JsonPropertyName("publisherId")] long? PublisherId,
    [property: JsonPropertyName("firstIssueId")] long? FirstIssueId,
    [property: JsonPropertyName("issues")] int? Issues,
    [property: JsonPropertyName("yearEnded")] int? YearEnded)
{
    public string Write() => JsonSerializer.Serialize(this);

    public static GcdExtra? Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonSerializer.Deserialize<GcdExtra>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public ComicsShape ShapeValue => Enum.TryParse<ComicsShape>(Shape, out var s) ? s : ComicsShape.Unknown;
}

/// <summary>Details of the chosen candidate's first issue (1.32.0): read for Identify's preview and the cover comparison only.</summary>
public sealed record GcdIssueDetail(string? CoverUrl, string? IndiciaPublisher, int? PageCount, string? Isbn, IReadOnlyList<MetadataJson.Creator> Creators);

/// <summary>
/// Grand Comics Database -> the normalized record (1.32.0, research 4.3). A GCD series is one edition: the country and language of
/// THAT edition (a Danish Blacksad is a Nordic record), its binding / format text in its own language, its issue descriptors with
/// every printing and variant listed separately (counted once). Covers are never part of the record (<see cref="ProviderSeriesRecord.ImageRemoteUrl"/>
/// stays null): GCD's cover scans are licensed for identification only, so a GCD-linked series keeps the file's own cover.
/// </summary>
public static partial class GcdMapping
{
    public const string ProviderId = MetadataProviderAllowlist.Gcd;
    public const string ProviderName = "Grand Comics Database";

    /// <summary>The credit shown next to GCD data (CC BY-SA 4.0 asks for attribution and a link).</summary>
    public const string Credit = "Data: Grand Comics Database, CC BY-SA 4.0";

    /// <summary>The image host of GCD's cover thumbnails: the single source is its gateway transport.</summary>
    internal static IReadOnlySet<string> ImageHosts => MetadataHttp.Transports[MetadataProviderAllowlist.Gcd].ImageHosts;

    /// <summary>
    /// The origin of an edition from its country (and, for multilingual countries, its language): English-speaking countries ->
    /// EnglishOriginal, France / Belgium / Switzerland by language, Spain and Latin America -> Spanish, Germany / Austria -> German,
    /// Italy -> Italian, the Netherlands -> Dutch, the Nordic countries -> Nordic, Japan / Korea / China / Taiwan -> their manga
    /// origins (GCD lists translated manga too), anything else -> Other.
    /// </summary>
    public static MetadataOrigin? OriginOf(string? country, string? language)
    {
        var c = country?.Trim().ToLowerInvariant();
        var l = language?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(c))
            return null;
        return c switch
        {
            "us" or "gb" or "uk" or "ca" or "au" or "ie" or "nz" => MetadataOrigin.EnglishOriginal,
            "fr" or "lu" or "mc" => MetadataOrigin.French,
            "be" => l == "nl" ? MetadataOrigin.Dutch : MetadataOrigin.French,
            "ch" => l switch { "de" => MetadataOrigin.German, "it" => MetadataOrigin.Italian, _ => MetadataOrigin.French },
            "es" or "ar" or "mx" or "cl" or "co" or "pe" or "uy" => MetadataOrigin.Spanish,
            "de" or "at" => MetadataOrigin.German,
            "it" => MetadataOrigin.Italian,
            "nl" => MetadataOrigin.Dutch,
            "se" or "no" or "dk" or "fi" or "is" => MetadataOrigin.Nordic,
            "jp" => MetadataOrigin.Japan,
            "kr" => MetadataOrigin.Korea,
            "cn" or "tw" or "hk" => MetadataOrigin.ChinaTaiwan,
            _ => MetadataOrigin.Other,
        };
    }

    // Single issues: stapled books (US floppies, German "Heft", Brazilian "grampeada", French "agrafé", Italian "spillato").
    [GeneratedRegex(@"saddle[- ]?stitch|stapled|geheftet|grampead|agraf|spillat|grapad|niitattu|h[aä]ftad", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IssueBinding();

    // Collected books: trade paperbacks, hardcovers, graphic novels, albums and their words in the languages GCD records use.
    [GeneratedRegex(
        @"trade\s*paperback|\btpb\b|hard\s*cover|hardback|soft\s*cover|paperback|square\s*bound|graphic\s*novel|collected|album|omnibus|int[eé]grale?|integraal|gesamtausgabe|verzamelbundel|broch[eé]|reli[eé]|cartonn?[eé]|dos\s+carr[eé]|tapa\s+(dura|blanda)|harde\s+kaft|gelijmd|oprawa|fadenheftung|kem[eé]nyt[aá]bl|kovakant|nidottu|lombada\s+quadrada|cartonat|brossura|innbundet|indbundet|inbunden",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CollectedText();

    // "1 - Quelque part entre les ombres": numbered albums with a title (BD, translated albums).
    [GeneratedRegex(@"^\d{1,4}\s+-\s+\S", RegexOptions.CultureInvariant)]
    private static partial Regex TitledNumber();

    /// <summary>Issues or collected books, from the binding first, then the format text, then numbered titled albums.</summary>
    public static ComicsShape ShapeOf(string? binding, string? publishingFormat, IReadOnlyList<string>? descriptors)
    {
        if (!string.IsNullOrWhiteSpace(binding))
        {
            if (IssueBinding().IsMatch(binding))
                return ComicsShape.Issues;
            if (CollectedText().IsMatch(binding))
                return ComicsShape.Collected;
        }
        if (!string.IsNullOrWhiteSpace(publishingFormat) && CollectedText().IsMatch(publishingFormat))
            return ComicsShape.Collected;
        var list = descriptors ?? [];
        if (list.Count > 0 && list.Count(d => TitledNumber().IsMatch(d ?? string.Empty)) * 2 > list.Count)
            return ComicsShape.Collected;
        return ComicsShape.Unknown;
    }

    /// <summary>A GCD id from one of its API URLs (<c>https://www.comics.org/api/publisher/672/?format=json</c> -> 672).</summary>
    public static long? IdFromApiUrl(string? url, string kind)
    {
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.Host, MetadataHttp.GcdApiHost, StringComparison.OrdinalIgnoreCase))
            return null;
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i + 1 < segments.Length; i++)
        {
            if (segments[i].Equals(kind, StringComparison.OrdinalIgnoreCase)
                && long.TryParse(segments[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
                return id;
        }
        return null;
    }

    /// <summary>The record of a series. <paramref name="publisherName"/> is the publisher's name when it is already known locally.</summary>
    internal static ProviderSeriesRecord ToRecord(GcdSeries s, long requestedId, string? publisherName)
    {
        var id = IdFromApiUrl(s.ApiUrl, "series") ?? requestedId;
        var title = MetadataText.Line(s.Name, 512) ?? throw new MetadataResponseInvalidException("missing_title");
        var language = ComicsEvidenceRules.LanguageCode(s.Language);
        var descriptors = (s.IssueDescriptors ?? []).Take(2000).ToList();
        var numbers = ComicsEvidenceRules.DistinctIssueNumbers(descriptors, out var unnumbered);
        var shape = ShapeOf(s.Binding, s.PublishingFormat, descriptors);
        var books = numbers.Count + unnumbered;
        var yearBegan = Year(s.YearBegan);
        var yearEnded = Year(s.YearEnded);
        var format = MetadataText.Line(s.PublishingFormat, 64);
        var publisherId = IdFromApiUrl(s.Publisher, "publisher");

        // Counts: an issue run's issues are its chapters (the Missing report and the count rule compare issue numbers with
        // chapter files); collected books and albums are its volumes. An unknown shape states no total.
        int? volumes = shape == ComicsShape.Collected && books > 0 ? books : null;
        int? chapters = shape == ComicsShape.Issues && numbers.Count > 0 ? numbers.Count : null;
        double? latest = shape == ComicsShape.Issues && numbers.Count > 0 ? (double)numbers[^1] : null;

        return new ProviderSeriesRecord
        {
            Provider = ProviderId,
            ExternalId = id.ToString(CultureInfo.InvariantCulture),
            SourceKind = MetadataSourceKind.OnlineApi,
            Title = title,
            Description = MetadataText.Description(s.Notes, 4 * 1024),
            Origin = OriginOf(s.Country, s.Language),
            Format = MetadataFormat.Comic,
            Webtoon = false,
            ProviderType = format is null ? null : MetadataText.Line(format, 32),
            StartYear = yearBegan,
            OriginStatus = yearEnded is not null ? MetadataOriginStatus.Complete : yearBegan is not null ? MetadataOriginStatus.Ongoing : null,
            OriginVolumes = volumes,
            LatestChapter = latest,
            TotalChapters = chapters,
            Publishers = publisherName is { } name && MetadataText.Line(name, 256) is { } p ? [new MetadataJson.Publisher(p, "original")] : [],
            SiteUrl = GcdReference.SiteUrl(id),
            ImageRemoteUrl = null,
            Language = language,
            Shape = shape,
            ExtraJson = new GcdExtra(
                MetadataText.Line(s.Country, 8), language, format, MetadataText.Line(s.Binding, 128), shape.ToString(), publisherId,
                IdFromApiUrl(s.ActiveIssues?.FirstOrDefault(), "issue"), books, yearEnded).Write(),
        };
    }

    /// <summary>
    /// The series of a search page, one per edition: a series listed twice with the same name, start year, publisher, language
    /// and shape (a reprint run, a second binding of the same book) is one candidate - the one with the most issues is kept. An
    /// issue run and its trade paperbacks (same name, year and publisher - Saga) are two editions and both stay.
    /// </summary>
    internal static IReadOnlyList<GcdSeries> Distinct(IEnumerable<GcdSeries> series) =>
        series
            .Where(s => !string.IsNullOrWhiteSpace(s.Name) && IdFromApiUrl(s.ApiUrl, "series") is not null)
            .GroupBy(s => (TitleNormalizer.ScoringForm(s.Name), s.YearBegan, IdFromApiUrl(s.Publisher, "publisher"), s.Language?.Trim().ToLowerInvariant(),
                ShapeOf(s.Binding, s.PublishingFormat, s.IssueDescriptors)))
            .Select(g => g.OrderByDescending(s => s.IssueDescriptors?.Count ?? 0).ThenBy(s => IdFromApiUrl(s.ApiUrl, "series")).First())
            .ToList();

    /// <summary>
    /// A re-fetched GCD record keeps the stored publisher name and credits when the new payload has none (a series payload names
    /// its publisher by id only, credits live on issues) and the publisher is still the same. Other providers pass through.
    /// </summary>
    internal static ProviderSeriesRecord KeepDetails(Persistence.Entities.MetadataRecordEntity stored, ProviderSeriesRecord fetched)
    {
        if (fetched.Provider != ProviderId || stored.Provider != ProviderId)
            return fetched;
        var samePublisher = GcdExtra.Read(stored.ExtraJson)?.PublisherId == GcdExtra.Read(fetched.ExtraJson)?.PublisherId;
        var result = fetched;
        if (fetched.Publishers.Count == 0 && samePublisher)
            result = result with { Publishers = MetadataJson.ReadList<MetadataJson.Publisher>(stored.PublishersJson) };
        if (fetched.Creators.Count == 0)
            result = result with { Creators = MetadataJson.ReadList<MetadataJson.Creator>(stored.CreatorsJson) };
        return result;
    }

    /// <summary>The chosen candidate's first issue: cover thumbnail (identification only), credits as text, page count, ISBN.</summary>
    internal static GcdIssueDetail ToIssueDetail(GcdIssue issue)
    {
        var creators = new List<MetadataJson.Creator>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var story in (issue.Stories ?? []).Where(s => !string.Equals(s.Type?.Trim(), "cover", StringComparison.OrdinalIgnoreCase)).Take(20))
        {
            foreach (var (text, role) in new[] { (story.Script, "author"), (story.Pencils, "artist") })
            {
                foreach (var name in CreditNames(text))
                {
                    if (creators.Count >= 10)
                        break;
                    if (seen.Add(role + "\n" + name))
                        creators.Add(new MetadataJson.Creator(name, role, null));
                }
            }
        }
        var pages = decimal.TryParse(issue.PageCount, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var p) && p is > 0 and < 10000
            ? (int?)decimal.ToInt32(decimal.Round(p))
            : null;
        return new GcdIssueDetail(ImageUrl(issue.Cover), MetadataText.Line(issue.IndiciaPublisher, 256), pages, MetadataText.Line(issue.Isbn, 32), creators);
    }

    /// <summary>Names from a GCD credit text (<c>Jeff Smith (credited); ?</c>): parenthesised notes and unknown markers dropped.</summary>
    internal static IEnumerable<string> CreditNames(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            yield break;
        foreach (var part in text.Split(';'))
        {
            var name = Parenthesised().Replace(part, " ").Replace("[", " ").Replace("]", " ").Replace("?", " ");
            if (MetadataText.Line(name, 128) is { } clean && clean.Any(char.IsLetter)
                && !clean.Equals("None", StringComparison.OrdinalIgnoreCase) && !clean.Equals("typeset", StringComparison.OrdinalIgnoreCase))
                yield return clean;
        }
    }

    [GeneratedRegex(@"\([^()]*\)", RegexOptions.CultureInvariant)]
    private static partial Regex Parenthesised();

    /// <summary>A thumbnail URL is kept only when it is HTTPS on GCD's image host (re-validated again before any fetch).</summary>
    internal static string? ImageUrl(string? url) =>
        url is { Length: > 0 and <= 512 } && Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && HostAllowlistHandler.IsAllowed(uri, ImageHosts)
            ? uri.AbsoluteUri
            : null;

    private static int? Year(int? year) => year is >= 1800 and <= 2200 ? year : null;
}
