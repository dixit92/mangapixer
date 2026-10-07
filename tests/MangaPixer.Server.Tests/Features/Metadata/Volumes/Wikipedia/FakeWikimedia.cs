namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes.Wikipedia;

using System.Net;
using System.Text;
using System.Text.Json;
using System.Web;
using com.lifepixer.mangapixer.Server.Features.Metadata;

/// <summary>
/// A synthetic Wikidata + English Wikipedia (1.32.0 tests): answers exactly the Action API requests the Wikipedia companion makes - the
/// Wikidata <c>haswbstatement</c> search and <c>wbgetentities</c> sitelinks, the Wikipedia <c>prop=info</c> revision check and
/// <c>prop=revisions</c> wikitext read (with normalisation, redirects and missing pages). The pages hold SYNTHETIC wikitext in the
/// structure of the real list pages; no Wikipedia text is stored in the repository. Plug <see cref="Respond"/> into a harness.
/// </summary>
public sealed class FakeWikimedia
{
    public sealed record Page(long Revision, string Wikitext);

    /// <summary>MangaUpdates series id (base36, as Wikidata P11149 holds it) -> Wikidata item ids.</summary>
    public Dictionary<string, string[]> Items { get; } = new(StringComparer.Ordinal);

    /// <summary>Wikidata item id -> English Wikipedia article title.</summary>
    public Dictionary<string, string> Sitelinks { get; } = new(StringComparer.Ordinal);

    /// <summary>Page title -> revision and wikitext.</summary>
    public Dictionary<string, Page> Pages { get; } = new(StringComparer.Ordinal);

    /// <summary>Redirect source title -> target title.</summary>
    public Dictionary<string, string> Redirects { get; } = new(StringComparer.Ordinal);

    /// <summary>When set, every Wikipedia / Wikidata request is answered with a <c>maxlag</c> error and this <c>Retry-After</c>.</summary>
    public int? MaxLagSeconds { get; set; }

    /// <summary>
    /// When set, Wikidata's lag in seconds (it includes its query-service lag): a Wikidata request whose <c>maxlag</c> is below it is
    /// answered like the live API does - HTTP 200 with a <c>maxlag</c> error and <c>Retry-After: 5</c>; en.wikipedia.org is not lagged.
    /// </summary>
    public int? WikidataLagSeconds { get; set; }

    /// <summary>When set, every request is answered with this HTTP status (a 429, a 503 ...).</summary>
    public HttpStatusCode? Status { get; set; }

    public static bool IsWikimedia(Uri uri) => uri.Host is MetadataHttp.WikipediaHost or MetadataHttp.WikidataHost;

    /// <summary>The response for a Wikimedia request, or null for any other host.</summary>
    public HttpResponseMessage? Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (!IsWikimedia(uri))
            return null;
        if (Status is { } status)
            return new HttpResponseMessage(status) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        if (MaxLagSeconds is { } lag)
            return MaxLagAnswer(lag);

        var query = HttpUtility.ParseQueryString(uri.Query);
        if (uri.Host == MetadataHttp.WikidataHost && WikidataLagSeconds is { } wikidataLag
            && int.TryParse(query["maxlag"], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var asked)
            && wikidataLag > asked)
            return MaxLagAnswer(5);
        var action = query["action"];
        if (uri.Host == MetadataHttp.WikidataHost)
            return action == "wbgetentities" ? Entities(query) : Search(query);
        return PagesAnswer(query);
    }

    private static HttpResponseMessage MaxLagAnswer(int retryAfterSeconds)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"error\":{\"code\":\"maxlag\",\"info\":\"Waiting for a database server\",\"lag\":9}}", Encoding.UTF8, "application/json"),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(retryAfterSeconds));
        return response;
    }

    private static HttpResponseMessage Json(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private HttpResponseMessage Search(System.Collections.Specialized.NameValueCollection query)
    {
        const string prefix = "haswbstatement:P11149=";
        var search = query["srsearch"] ?? string.Empty;
        var id = search.StartsWith(prefix, StringComparison.Ordinal) ? search[prefix.Length..] : string.Empty;
        var hits = Items.GetValueOrDefault(id) ?? [];
        return Json(new { batchcomplete = true, query = new { searchinfo = new { totalhits = hits.Length }, search = hits.Select(q => new { ns = 0, title = q }) } });
    }

    private HttpResponseMessage Entities(System.Collections.Specialized.NameValueCollection query)
    {
        var entities = new Dictionary<string, object>();
        foreach (var id in (query["ids"] ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            entities[id] = Sitelinks.TryGetValue(id, out var title)
                ? new { type = "item", id, sitelinks = new Dictionary<string, object> { ["enwiki"] = new { site = "enwiki", title } } }
                : new { type = "item", id, sitelinks = new Dictionary<string, object>() };
        }
        return Json(new { entities, success = 1 });
    }

    private HttpResponseMessage PagesAnswer(System.Collections.Specialized.NameValueCollection query)
    {
        var withContent = (query["prop"] ?? string.Empty).Contains("revisions", StringComparison.Ordinal);
        var normalized = new List<object>();
        var redirects = new List<object>();
        var pages = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var requested in (query["titles"] ?? string.Empty).Split('|', StringSplitOptions.RemoveEmptyEntries))
        {
            var title = requested.Replace('_', ' ');
            title = char.ToUpperInvariant(title[0]) + title[1..];
            if (title != requested)
                normalized.Add(new { from = requested, to = title });
            if (Redirects.TryGetValue(title, out var target))
            {
                redirects.Add(new { from = title, to = target });
                title = target;
            }
            if (pages.ContainsKey(title))
                continue;
            if (!Pages.TryGetValue(title, out var page))
            {
                pages[title] = new { title, missing = true };
                continue;
            }
            pages[title] = withContent
                ? new
                {
                    pageid = 1,
                    ns = 0,
                    title,
                    revisions = new[] { new { revid = page.Revision, slots = new { main = new { contentmodel = "wikitext", content = page.Wikitext } } } },
                }
                : (object)new { pageid = 1, ns = 0, title, lastrevid = page.Revision };
        }
        var body = new Dictionary<string, object> { ["batchcomplete"] = true, ["query"] = new Dictionary<string, object> { ["pages"] = pages.Values.ToArray() } };
        if (normalized.Count > 0)
            ((Dictionary<string, object>)body["query"])["normalized"] = normalized;
        if (redirects.Count > 0)
            ((Dictionary<string, object>)body["query"])["redirects"] = redirects;
        return Json(body);
    }

    // --- Synthetic wikitext ------------------------------------------------------------------------------------------------

    /// <summary>One volume of a synthetic list page: chapters <c>From</c>..<c>To</c> (counting on from the previous one when null).</summary>
    public sealed record Volume(int Number, int From, int To, string? EnglishDate = null, string? EnglishIsbn = null);

    /// <summary>A "List of ... chapters" table in the structure of the real pages: header, one row per volume, footer.</summary>
    public static string ListPage(string heading, params Volume[] volumes)
    {
        var sb = new StringBuilder();
        sb.Append("== ").Append(heading).Append(" ==\n{{Graphic novel list/header|Language=Japanese}}\n");
        foreach (var v in volumes)
        {
            sb.Append("{{Graphic novel list\n| VolumeNumber = ").Append(v.Number).Append('\n');
            sb.Append("| OriginalRelDate = January 5, 2020<ref>{{cite web|url=http://example.invalid/x}}</ref>\n| OriginalISBN = 978-4-09-850180-9\n");
            if (v.EnglishDate is not null)
                sb.Append("| LicensedRelDate = ").Append(v.EnglishDate).Append("<ref name=\"x\"/>\n");
            if (v.EnglishIsbn is not null)
                sb.Append("| LicensedISBN = ").Append(v.EnglishIsbn).Append('\n');
            if (v.To >= v.From)
            {
                sb.Append("| ChapterList =\n{{Numbered list|start=").Append(v.From);
                for (var c = v.From; c <= v.To; c++)
                    sb.Append("\n|{{nihongo|\"Synthetic chapter ").Append(c).Append("\"|x|y}}");
                sb.Append("\n}}\n");
            }
            sb.Append("| Summary = Made-up summary text.\n}}\n");
        }
        sb.Append("{{Graphic novel list/footer}}\n");
        return sb.ToString();
    }

    /// <summary>Volumes <c>1..count</c> with <paramref name="perVolume"/> chapters each.</summary>
    public static Volume[] Run(int count, int perVolume = 4) =>
        Enumerable.Range(1, count).Select(v => new Volume(v, ((v - 1) * perVolume) + 1, v * perVolume, $"January {v}, 2021", IsbnOf(v))).ToArray();

    /// <summary>A valid ISBN-13 for volume <paramref name="v"/> (978-1-9747-xxxx-c).</summary>
    public static string IsbnOf(int v)
    {
        var body = "978197470" + v.ToString("D3", System.Globalization.CultureInfo.InvariantCulture);
        var sum = 0;
        for (var i = 0; i < 12; i++)
            sum += (body[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return body + ((10 - (sum % 10)) % 10);
    }
}
