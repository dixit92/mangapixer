namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>The typed field a search text ends in (<c>creator:ura</c>): where its token starts, its name and the value typed so far.</summary>
public sealed record TrailingField(int Start, bool Negated, string Name, FacetBucket? Bucket, string Prefix);

/// <summary>
/// Finds the typed field a search text ENDS in (1.49.0, lane O), by the same token rules as <see cref="FacetSearchSyntax"/> (a space after
/// the colon is allowed, a quoted value may hold spaces, an unterminated quote runs to the end). A field is "being typed" when its token
/// reaches the end of the text: <c>creator:ura</c>, <c>genre:</c>, <c>creator:"Naoki Ur</c>. A closed quote, a space after a finished
/// value, an unknown name (<c>Re:Zero</c>) or text after it means the user moved on. Pure; no I/O.
/// </summary>
public static class SearchSuggestSyntax
{
    /// <summary>The non-bucket fields that complete: <c>is:</c> and <c>library:</c>.</summary>
    public const string IsField = "is", LibraryField = "library";

    public static TrailingField? Trailing(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return null;
        TrailingField? last = null;
        var i = 0;
        while (i < text.Length)
        {
            if (char.IsWhiteSpace(text[i]))
            {
                i++;
                continue;
            }
            last = null;
            var start = i;
            var negated = text[i] == '-' && i + 1 < text.Length && !char.IsWhiteSpace(text[i + 1]);
            var nameStart = negated ? i + 1 : i;
            var colon = -1;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '"')
            {
                if (text[i] == ':' && colon < 0)
                    colon = i;
                i++;
            }

            if (colon > nameStart)
            {
                var name = text[nameStart..colon];
                string? value;
                int end;
                var closedQuote = false;
                if (colon + 1 < i)
                {
                    value = text[(colon + 1)..i];
                    end = i;
                }
                else
                {
                    var j = i;
                    while (j < text.Length && text[j] == ' ')
                        j++;
                    if (j < text.Length && text[j] == '"')
                    {
                        var close = text.IndexOf('"', j + 1);
                        closedQuote = close >= 0;
                        value = close < 0 ? text[(j + 1)..] : text[(j + 1)..close];
                        end = close < 0 ? text.Length : close + 1;
                    }
                    else
                    {
                        end = j;
                        while (end < text.Length && !char.IsWhiteSpace(text[end]))
                            end++;
                        value = text[j..end];
                    }
                }

                if (end >= text.Length && !closedQuote && Known(name, negated) is { } known)
                    last = new TrailingField(start, negated, known.Name, known.Bucket, (value ?? "").Trim());
                i = end;
                continue;
            }

            if (i < text.Length && text[i] == '"')
            {
                var close = text.IndexOf('"', i + 1);
                i = close < 0 ? text.Length : close + 1;
            }
        }
        return last;
    }

    private static (string Name, FacetBucket? Bucket)? Known(string name, bool negated)
    {
        if (!negated && string.Equals(name, IsField, StringComparison.OrdinalIgnoreCase))
            return (IsField, null);
        if (!negated && string.Equals(name, LibraryField, StringComparison.OrdinalIgnoreCase))
            return (LibraryField, null);
        return FacetBucketTokens.TryParse(name, out var bucket) && FacetBucketTokens.Typed.Contains(bucket)
            ? (FacetBucketTokens.Token(bucket), bucket)
            : null;
    }

    /// <summary>The token that replaces the one being typed: <c>creator:"Naoki Urasawa"</c> (quoted when the value holds a space or a bar).</summary>
    public static string Token(bool negated, string name, string value)
    {
        var plain = value.Replace("\"", "", StringComparison.Ordinal).Trim();
        var quote = plain.Any(c => char.IsWhiteSpace(c) || c == '|');
        return (negated ? "-" : "") + name + ":" + (quote ? "\"" + plain + "\"" : plain);
    }
}

/// <summary>
/// Quick results while typing (1.49.0, lane O; owner decision 7): the best few series, folders, archives, creators and tags for the typed
/// words, or - when the text ends in a typed field - that field's values. Every part starts from the viewer's access: the node half from
/// the visible libraries (<see cref="CatalogBrowseService.SuggestNodesAsync"/>), the creator / tag half from the facet query's viewer
/// scope (<see cref="FacetViewerScopeResolver"/>: series information hidden = no series, creators or tags), library names from the visible
/// libraries. Nothing is cached (nothing viewer-blind exists to cache) and nothing is logged but counts and timings.
/// </summary>
public sealed class SearchSuggestService
{
    /// <summary>Quick results per group.</summary>
    public const int PerGroup = 5;

    /// <summary>Values listed for a typed field.</summary>
    public const int PerField = 8;

    /// <summary>The longest text the route reads (the rest is cut).</summary>
    public const int MaxQueryLength = 200;

    /// <summary>Creators and tags are looked up from this many characters (names from a word of 3+ characters).</summary>
    public const int MinValueChars = 2;

    private static readonly string[] ReadStates = ["read", "reading", "unread"];

    private readonly CatalogBrowseService _browse;
    private readonly FacetSuggestService _facets;
    private readonly FacetViewerScopeResolver _scopes;
    private readonly LibraryAuthorizationService _auth;
    private readonly MangaPixerDbContext _db;
    private readonly ILogger<SearchSuggestService> _logger;

    public SearchSuggestService(CatalogBrowseService browse, FacetSuggestService facets, FacetViewerScopeResolver scopes,
        LibraryAuthorizationService auth, MangaPixerDbContext db, ILogger<SearchSuggestService> logger)
    {
        _browse = browse;
        _facets = facets;
        _scopes = scopes;
        _auth = auth;
        _db = db;
        _logger = logger;
    }

    public async Task<SearchSuggestDto> SuggestAsync(long userId, bool incognito, string? text, CancellationToken ct = default)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var q = text ?? "";
        if (q.Length > MaxQueryLength)
            q = q[..MaxQueryLength];
        if (string.IsNullOrWhiteSpace(q))
            return Empty(q);

        if (SearchSuggestSyntax.Trailing(q) is { } field)
        {
            var items = await FieldValuesAsync(userId, incognito, field, ct);
            Log(started, true, items.Count, 0, 0, 0, 0);
            return Empty(q) with
            {
                Field = new SuggestFieldDto { Name = field.Name, Prefix = field.Prefix, Start = field.Start, Items = items },
            };
        }

        // The words that are not typed fields or scope words (a field typed earlier narrows the search page, not the names).
        var query = SearchText.Parse(FacetSearchSyntax.Parse(q).FreeText);
        if (query.IsEmpty)
            return Empty(q);

        var nodes = await _browse.SuggestNodesAsync(userId, query, incognito, PerGroup, ct);
        IReadOnlyList<SuggestedValue> creators = [];
        IReadOnlyList<SuggestedValue> tags = [];
        if (query.AllWords.Sum(w => w.Length) >= MinValueChars)
        {
            var scope = await _scopes.ResolveAsync(userId, incognito, ct);
            creators = await _facets.ValuesAsync(scope, [FacetBucket.Creators], query, PerGroup, ct);
            tags = await _facets.ValuesAsync(scope, [FacetBucket.Tags, FacetBucket.Genres], query, PerGroup, ct);
        }

        Log(started, false, 0, nodes.Series.Count, nodes.Folders.Count + nodes.Archives.Count, creators.Count, tags.Count);
        return new SearchSuggestDto
        {
            Query = q,
            Series = nodes.Series.Select(n => NodeItem(n, "series")).ToList(),
            Folders = nodes.Folders.Select(n => NodeItem(n, "folder")).ToList(),
            Archives = nodes.Archives.Select(n => NodeItem(n, "archive")).ToList(),
            Creators = creators.Select(ValueItem).ToList(),
            Tags = tags.Select(ValueItem).ToList(),
        };
    }

    private async Task<List<SuggestValueDto>> FieldValuesAsync(long userId, bool incognito, TrailingField field, CancellationToken ct)
    {
        var prefix = field.Prefix;
        // Alternatives (genre:horror|my) and year ranges are typed by hand; there is no value list for the half of one.
        if (prefix.Contains('|', StringComparison.Ordinal) || prefix.Contains("..", StringComparison.Ordinal))
            return [];
        var query = SearchText.Parse(prefix);
        SuggestValueDto Value(string label, int? count = null) =>
            new() { Label = label, Insert = SearchSuggestSyntax.Token(field.Negated, field.Name, label), Count = count };

        switch (field.Name)
        {
            case SearchSuggestSyntax.IsField:
                return ReadStates.Where(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).Select(s => Value(s)).ToList();

            case SearchSuggestSyntax.LibraryField:
            {
                var visible = await _auth.GetVisibleLibraryIdsAsync(userId, incognito, ct);
                var names = await _db.Libraries.AsNoTracking().Where(l => visible.Contains(l.Id)).Select(l => l.DisplayName).ToListAsync(ct);
                return names
                    .Select(n => (Name: n, Tier: FacetSuggestService.ValueTier(query, n)))
                    .Where(n => n.Tier is not null)
                    .OrderBy(n => n.Tier).ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase)
                    .Take(PerField)
                    .Select(n => Value(n.Name))
                    .ToList();
            }

            case "series":
            {
                var titles = await _browse.SuggestSeriesTitlesAsync(userId, query, incognito, PerField, ct);
                return titles.Select(t => Value(t)).ToList();
            }

            default:
            {
                if (field.Bucket is not { } bucket || (!query.IsEmpty && query.AllWords.Sum(w => w.Length) < MinValueChars))
                    return [];
                var scope = await _scopes.ResolveAsync(userId, incognito, ct);
                var values = await _facets.ValuesAsync(scope, [bucket], query, PerField, ct);
                return values.Select(v => Value(v.Display, v.Count)).ToList();
            }
        }
    }

    private static SearchSuggestDto Empty(string query) => new()
    {
        Query = query,
        Series = [],
        Folders = [],
        Archives = [],
        Creators = [],
        Tags = [],
    };

    private static SuggestItemDto NodeItem(CatalogBrowseService.SuggestNode node, string kind) => new()
    {
        Kind = kind,
        Id = node.Id,
        Label = node.Name,
        Detail = node.Aka is not null ? "aka " + node.Aka : node.Place,
        LibraryId = node.LibraryId,
        NodeKind = node.Kind.ToString(),
    };

    private static SuggestItemDto ValueItem(SuggestedValue value) => new()
    {
        Kind = value.Bucket switch { FacetBucket.Creators => "creator", FacetBucket.Genres => "genre", _ => "tag" },
        Id = value.PublicId,
        Label = value.Display,
        Filter = FacetBucketTokens.Token(value.Bucket) + ":" + value.PublicId,
        Count = value.Count,
    };

    private void Log(long started, bool typed, int fieldItems, int series, int nodes, int creators, int tags) =>
        _logger.LogDebug(LogEvents.QuickResults.Answered,
            "Quick results: {Mode}, {FieldItems} field values, {Series} series, {Nodes} nodes, {Creators} creators, {Tags} tags, {ElapsedMs} ms",
            typed ? "field" : "groups", fieldItems, series, nodes, creators, tags,
            (long)System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
}
