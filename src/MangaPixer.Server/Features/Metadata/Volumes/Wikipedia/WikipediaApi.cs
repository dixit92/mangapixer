namespace com.lifepixer.mangapixer.Server.Features.Metadata.Volumes.Wikipedia;

using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>A page the revision check looked at: where a requested title ended up (normalised, redirected) and its latest revision.</summary>
public sealed record WikipediaPageRevision(string Requested, string Title, bool Missing, long? RevisionId);

/// <summary>A page's wikitext with its revision id; <see cref="Wikitext"/> is null for a missing page.</summary>
public sealed record WikipediaPageText(string Requested, string Title, bool Missing, long? RevisionId, string? Wikitext);

/// <summary>
/// The Wikimedia side of the Wikipedia companion (1.32.0): the English Wikipedia Action API and Wikidata, as much as the volume lists need.
/// Like every provider class it only translates HTTP / JSON; the gateway (<see cref="MetadataGateway.CompanionCallAsync{T}"/>) owns every
/// policy (switches, allowlist, backoff, budget, the one Wikimedia limiter) and the service decides what to ask.
/// </summary>
public interface IWikipediaApi
{
    /// <summary>Wikidata items whose MangaUpdates series id (P11149) is <paramref name="mangaUpdatesId"/> (base36), best first (at most 3).</summary>
    Task<IReadOnlyList<string>> FindItemsAsync(string mangaUpdatesId, CancellationToken ct);

    /// <summary>The English Wikipedia article title linked to the first of <paramref name="items"/> that has one, or null.</summary>
    Task<string?> EnglishArticleAsync(IReadOnlyList<string> items, CancellationToken ct);

    /// <summary>The latest revision id of up to 50 pages (redirects followed); missing pages are reported as such.</summary>
    Task<IReadOnlyList<WikipediaPageRevision>> RevisionsAsync(IReadOnlyList<string> titles, CancellationToken ct);

    /// <summary>The wikitext of up to 50 pages (redirects followed).</summary>
    Task<IReadOnlyList<WikipediaPageText>> PagesAsync(IReadOnlyList<string> titles, CancellationToken ct);
}

/// <summary>
/// The Wikimedia APIs (<c>https://en.wikipedia.org/w/api.php</c>, <c>https://www.wikidata.org/w/api.php</c>). Fixed requests, only these
/// parameters: Wikidata <c>action=query&amp;list=search&amp;srsearch=haswbstatement:P11149=&lt;id&gt;</c> and <c>action=wbgetentities&amp;props=sitelinks&amp;sitefilter=enwiki</c>;
/// Wikipedia <c>action=query&amp;prop=info</c> or <c>prop=revisions&amp;rvprop=content|ids&amp;rvslots=main</c> with <c>titles=A|B|...</c>,
/// <c>redirects=1</c>. Every request carries <c>maxlag=5</c> and <c>format=json</c>; the headers come from the named clients (the fixed
/// User-Agent, no cookies, no token, no <c>Via</c>). Requests are serial: one at a time, whatever the callers do (the Robot policy for
/// unauthenticated clients). A <c>maxlag</c> answer is a "slow down" (503 with its <c>Retry-After</c>), like a 429.
/// </summary>
public sealed partial class WikipediaApi : IWikipediaApi
{
    public const int MaxTitles = 50;
    public const int MaxWikitextBytes = 4 * 1024 * 1024;
    private const string WikipediaBase = "https://" + MetadataHttp.WikipediaHost + "/w/api.php";
    private const string WikidataBase = "https://" + MetadataHttp.WikidataHost + "/w/api.php";
    private const int DefaultMaxLagWaitSeconds = 5;

    private readonly IHttpClientFactory _httpFactory;
    private readonly SemaphoreSlim _serial = new(1, 1);

    public WikipediaApi(IHttpClientFactory httpFactory)
    {
        _httpFactory = httpFactory;
    }

    [GeneratedRegex(@"^Q[1-9][0-9]{0,11}$", RegexOptions.CultureInvariant)]
    private static partial Regex ItemPattern();

    [GeneratedRegex(@"^[0-9a-z]{1,13}$", RegexOptions.CultureInvariant)]
    private static partial Regex MangaUpdatesIdPattern();

    /// <summary>A page title as the API accepts it from us: 1-255 characters, no markup, no fragment, no pipe, no control characters.</summary>
    public static bool IsValidTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && title.Length <= 255
        && title.All(c => !char.IsControl(c) && c is not ('|' or '<' or '>' or '[' or ']' or '{' or '}' or '#' or '\\'));

    /// <summary>
    /// A page title from what an admin pastes: a title (<c>List of X chapters</c>) or an English Wikipedia address
    /// (<c>https://en.wikipedia.org/wiki/List_of_X_chapters</c>), parsed LOCALLY - nothing is sent to learn it.
    /// </summary>
    public static bool TryParseTitle(string? input, out string title)
    {
        title = string.Empty;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text) || text.Length > 512)
            return false;
        if (text.Contains("://", StringComparison.Ordinal) || text.StartsWith("en.wikipedia.org", StringComparison.OrdinalIgnoreCase))
        {
            var candidate = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http")
                || !uri.Host.Equals(MetadataHttp.WikipediaHost, StringComparison.OrdinalIgnoreCase)
                || !uri.AbsolutePath.StartsWith("/wiki/", StringComparison.Ordinal))
                return false;
            text = Uri.UnescapeDataString(uri.AbsolutePath["/wiki/".Length..]);
        }
        text = string.Join(' ', text.Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (!IsValidTitle(text))
            return false;
        title = text;
        return true;
    }

    /// <summary>The address of an English Wikipedia page (the credit link).</summary>
    public static string PageUrl(string title) =>
        "https://" + MetadataHttp.WikipediaHost + "/wiki/" + Uri.EscapeDataString(title.Replace(' ', '_')).Replace("%2F", "/").Replace("%3A", ":");

    public async Task<IReadOnlyList<string>> FindItemsAsync(string mangaUpdatesId, CancellationToken ct)
    {
        if (!MangaUpdatesIdPattern().IsMatch(mangaUpdatesId))
            return [];
        var url = $"{WikidataBase}?action=query&list=search&srsearch=haswbstatement%3AP11149%3D{mangaUpdatesId}&srnamespace=0&srlimit=3&maxlag=5&format=json&formatversion=2";
        using var doc = await GetJsonAsync(MetadataHttp.WikidataClient, url, ct);
        var items = new List<string>();
        if (doc.RootElement.TryGetProperty("query", out var query) && query.TryGetProperty("search", out var hits) && hits.ValueKind == JsonValueKind.Array)
        {
            foreach (var hit in hits.EnumerateArray())
            {
                if (hit.TryGetProperty("title", out var t) && t.GetString() is { } id && ItemPattern().IsMatch(id))
                    items.Add(id);
            }
        }
        return items;
    }

    public async Task<string?> EnglishArticleAsync(IReadOnlyList<string> items, CancellationToken ct)
    {
        var ids = items.Where(i => ItemPattern().IsMatch(i)).Take(3).ToList();
        if (ids.Count == 0)
            return null;
        var url = $"{WikidataBase}?action=wbgetentities&ids={string.Join("%7C", ids)}&props=sitelinks&sitefilter=enwiki&maxlag=5&format=json";
        using var doc = await GetJsonAsync(MetadataHttp.WikidataClient, url, ct);
        if (!doc.RootElement.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var id in ids)
        {
            if (entities.TryGetProperty(id, out var entity) && entity.TryGetProperty("sitelinks", out var links) && links.ValueKind == JsonValueKind.Object
                && links.TryGetProperty("enwiki", out var link) && link.TryGetProperty("title", out var title)
                && title.GetString() is { } text && IsValidTitle(text))
                return text;
        }
        return null;
    }

    public async Task<IReadOnlyList<WikipediaPageRevision>> RevisionsAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        var url = PageQuery(titles, "prop=info");
        if (url is null)
            return [];
        using var doc = await GetJsonAsync(MetadataHttp.WikipediaClient, url, ct);
        return ReadPages(doc.RootElement, titles).Select(p => new WikipediaPageRevision(p.Requested, p.Title, p.Missing, p.RevisionId)).ToList();
    }

    public async Task<IReadOnlyList<WikipediaPageText>> PagesAsync(IReadOnlyList<string> titles, CancellationToken ct)
    {
        var url = PageQuery(titles, "prop=revisions&rvprop=content%7Cids&rvslots=main");
        if (url is null)
            return [];
        using var doc = await GetJsonAsync(MetadataHttp.WikipediaClient, url, ct);
        return ReadPages(doc.RootElement, titles);
    }

    private static string? PageQuery(IReadOnlyList<string> titles, string what)
    {
        var valid = titles.Where(IsValidTitle).Distinct(StringComparer.Ordinal).Take(MaxTitles).ToList();
        if (valid.Count == 0)
            return null;
        return $"{WikipediaBase}?action=query&{what}&titles={string.Join("%7C", valid.Select(Uri.EscapeDataString))}&redirects=1&maxlag=5&format=json&formatversion=2";
    }

    /// <summary>Maps every requested title to the page it ended up on (normalisation, then redirects) and reads that page.</summary>
    internal static IReadOnlyList<WikipediaPageText> ReadPages(JsonElement root, IReadOnlyList<string> requested)
    {
        var results = new List<WikipediaPageText>();
        if (!root.TryGetProperty("query", out var query))
            return results;
        var moves = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { "normalized", "redirects" })
        {
            if (!query.TryGetProperty(name, out var list) || list.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var move in list.EnumerateArray())
            {
                if (move.TryGetProperty("from", out var from) && move.TryGetProperty("to", out var to) && from.GetString() is { } f && to.GetString() is { } t)
                    moves[f] = t;
            }
        }
        var pages = new Dictionary<string, WikipediaPageText>(StringComparer.Ordinal);
        if (query.TryGetProperty("pages", out var array) && array.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in array.EnumerateArray())
            {
                if (!page.TryGetProperty("title", out var titleElement) || titleElement.GetString() is not { } title)
                    continue;
                var missing = (page.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.True)
                    || (page.TryGetProperty("invalid", out var inv) && inv.ValueKind == JsonValueKind.True);
                long? revision = page.TryGetProperty("lastrevid", out var last) && last.TryGetInt64(out var l) ? l : null;
                string? text = null;
                if (page.TryGetProperty("revisions", out var revisions) && revisions.ValueKind == JsonValueKind.Array && revisions.GetArrayLength() > 0)
                {
                    var first = revisions[0];
                    revision = first.TryGetProperty("revid", out var r) && r.TryGetInt64(out var rv) ? rv : revision;
                    if (first.TryGetProperty("slots", out var slots) && slots.TryGetProperty("main", out var main)
                        && main.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
                        text = content.GetString();
                }
                pages[title] = new WikipediaPageText(title, title, missing, missing ? null : revision, text);
            }
        }
        foreach (var title in requested)
        {
            var final = title;
            for (var hop = 0; hop < 4 && moves.TryGetValue(final, out var next); hop++)
                final = next;
            if (pages.TryGetValue(final, out var page))
                results.Add(page with { Requested = title });
        }
        return results;
    }

    /// <summary>One serial GET of a Wikimedia API; a <c>maxlag</c> answer becomes a 503 "slow down".</summary>
    private async Task<JsonDocument> GetJsonAsync(string client, string url, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            var http = _httpFactory.CreateClient(client);
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.NotFound)
                throw new MetadataResponseInvalidException("not_found");
            MetadataHttp.EnsureSuccess(response);
            var bytes = await MetadataHttp.ReadBoundedAsync(response, MaxWikitextBytes, ct);
            JsonDocument doc;
            try
            {
                doc = JsonDocument.Parse(bytes);
            }
            catch (JsonException)
            {
                throw new MetadataResponseInvalidException("malformed_json");
            }
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("error", out var error))
            {
                var code = error.TryGetProperty("code", out var c) ? c.GetString() : null;
                doc.Dispose();
                if (string.Equals(code, "maxlag", StringComparison.Ordinal) || string.Equals(code, "readonly", StringComparison.Ordinal))
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(DefaultMaxLagWaitSeconds);
                    throw new MetadataHttpStatusException(HttpStatusCode.ServiceUnavailable, wait, response.Headers.RetryAfter?.Date);
                }
                throw new MetadataResponseInvalidException("api_error");
            }
            return doc;
        }
        finally
        {
            _serial.Release();
        }
    }
}
