namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// A declared artist's provider reference (1.45.0): <c>mangaupdates:&lt;author id&gt;</c> in <c>declared_facts.ExternalRef</c> - the
/// MangaUpdates author an admin picked from an author search. Only MangaUpdates, only a positive decimal id.
/// </summary>
public static class AuthorReference
{
    public const string MangaUpdatesPrefix = MangaUpdatesProvider.ProviderId + ":";

    /// <summary>The reference of a MangaUpdates author id.</summary>
    public static string MangaUpdates(string authorId) => MangaUpdatesPrefix + authorId;

    /// <summary>The MangaUpdates author id a reference names, or null for anything else.</summary>
    public static string? MangaUpdatesIdOf(string? reference) =>
        reference is not null && reference.StartsWith(MangaUpdatesPrefix, StringComparison.Ordinal)
        && reference[MangaUpdatesPrefix.Length..] is var id && AuthorAliasLookupService.IsAuthorId(id)
            ? id
            : null;

    /// <summary>
    /// The reference a request names (provider + external id), null when it names none, or an error (<c>author_ref_invalid</c>): only
    /// both fields or neither, the provider <c>mangaupdates</c>, a positive decimal id. No request is made.
    /// </summary>
    public static (string? Reference, string? Error) FromRequest(string? provider, string? externalId)
    {
        var hasProvider = !string.IsNullOrWhiteSpace(provider);
        var hasId = !string.IsNullOrWhiteSpace(externalId);
        if (!hasProvider && !hasId)
            return (null, null);
        if (!hasProvider || !hasId || !string.Equals(provider!.Trim(), MangaUpdatesProvider.ProviderId, StringComparison.Ordinal))
            return (null, "author_ref_invalid");
        var id = externalId!.Trim();
        return AuthorAliasLookupService.IsAuthorId(id) ? (MangaUpdates(id), null) : (null, "author_ref_invalid");
    }
}

/// <summary>Outcome of an author search: the result, or a machine error code (400 / 404) - gateway refusals are thrown.</summary>
public sealed record AuthorSearchOutcome(AuthorSearchResultDto? Result, string? Error = null, bool NotFound = false);

/// <summary>
/// Author search by name (1.45.0; owner-approved 2026-10-19, main consent 7): an ADMIN searches MangaUpdates for an artist - from "Match
/// folders by name" as artist folders (one request per ticked folder, driven by the dialog: at least 1 s apart, at most 50 per run) or
/// from the Artist folder dialog's "Find on MangaUpdates" (one request). Never automatic, never in the background.
/// It sends ONLY the confirmed text (normalized, 1-200 characters) with a fixed page and page size
/// (<see cref="MangaUpdatesProvider.SearchAuthorsAsync"/>), through <see cref="MetadataGateway.DetailCallAsync{T}"/> with the Interactive
/// origin: every switch gate for the folder's library (the config kill, Fetch with the CURRENT consent, the allowlist, the library's switch),
/// the persisted backoff, the ONE daily budget and the MangaUpdates bucket. Answers are kept for an hour in memory (keyed by a SHA-256 of
/// the provider, the normalized text and the page, like series searches) and are NOT stored: they are only offered; nothing is marked until
/// the admin picks one and presses Mark (<see cref="Artists.ArtistFolderService"/> stores the reference; that sends nothing).
/// Logs: the gateway's line plus <see cref="LogEvents.Metadata.AuthorSearchServed"/> with the library id, hit count and elapsed ms - never
/// the text or a name.
/// </summary>
public sealed class AuthorSearchService
{
    /// <summary>The gateway operation name of an author search (its log line and failure bookkeeping).</summary>
    public const string Operation = "author_search";

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly MetadataProviderRegistry _providers;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AuthorSearchService> _logger;

    public AuthorSearchService(MangaPixerDbContext db, MetadataGateway gateway, MetadataProviderRegistry providers, IMemoryCache cache,
        ILogger<AuthorSearchService> logger)
    {
        _db = db;
        _gateway = gateway;
        _providers = providers;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>
    /// Searches MangaUpdates' authors for <paramref name="text"/> on behalf of the folder <paramref name="nodePublicId"/> (its library's
    /// switches apply). Errors: not found (no such live node), <c>not_a_folder</c>; a gateway refusal or provider failure is thrown as
    /// <see cref="MetadataGatewayException"/> (400 <c>invalid_query</c>, 409 switched off, 429 budget / busy, 503 backoff, 502 / 504 failures).
    /// </summary>
    public async Task<AuthorSearchOutcome> SearchAsync(string nodePublicId, string? text, CancellationToken ct = default)
    {
        var node = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.PublicId == nodePublicId && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => new { n.LibraryId, n.Kind })
            .FirstOrDefaultAsync(ct);
        if (node is null)
            return new(null, NotFound: true);
        if (node.Kind != (int)CatalogNodeKind.Folder)
            return new(null, "not_a_folder");

        var query = MetadataGateway.NormalizeQuery(text);
        if (query.Length == 0 || query.Length > MetadataGateway.MaxQueryLength)
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "invalid_query",
                $"The search text must be 1-{MetadataGateway.MaxQueryLength} characters.");

        // The switches first - also for an answer still in the cache: a refused instance serves nothing.
        if (await _gateway.CheckSwitchesAsync(node.LibraryId, MetadataCallOrigin.Interactive, MangaUpdatesProvider.ProviderId, ct) is { } refusal)
        {
            _logger.LogInformation(LogEvents.Metadata.GatewayRefused, "Metadata call refused for library {LibraryId}: {Code}", node.LibraryId, refusal.Code);
            throw refusal;
        }

        var watch = Stopwatch.StartNew();
        var key = CacheKey(query);
        var cached = _cache.TryGetValue<ProviderAuthorSearchPage>(key, out var page) && page is not null;
        if (!cached)
        {
            if (_providers.Find(MangaUpdatesProvider.ProviderId) is not MangaUpdatesProvider provider)
                throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "unknown_provider", "No such metadata provider.");
            page = await _gateway.DetailCallAsync(MangaUpdatesProvider.ProviderId, Operation, node.LibraryId,
                c => provider.SearchAuthorsAsync(query, c), new MetadataCallContext { Origin = MetadataCallOrigin.Interactive }, ct);
            _cache.SetSized(key, page, MetadataGateway.SearchCacheTtl, SharedMemoryCache.Kib(256 + (page.Hits.Count * 512L)), CacheItemPriority.High);
        }

        _logger.LogInformation(LogEvents.Metadata.AuthorSearchServed,
            "Author search for library {LibraryId}: {Hits} hit(s) of {Total}{Cached} in {ElapsedMs} ms",
            node.LibraryId, page!.Hits.Count, page.Total, cached ? " (cached)" : string.Empty, watch.ElapsedMilliseconds);
        return new(new AuthorSearchResultDto
        {
            Candidates = page.Hits.Select(h => new AuthorSearchCandidateDto
            {
                Provider = MangaUpdatesProvider.ProviderId,
                ExternalId = h.ExternalId,
                Name = h.Name,
                HitName = h.HitName,
                TotalSeries = h.TotalSeries,
                Genres = h.Genres,
                SiteUrl = h.SiteUrl,
            }).ToList(),
            Total = page.Total,
        });
    }

    /// <summary>The cache key: a SHA-256 of the operation, the provider, the case-folded text and the fixed page (never the text itself).</summary>
    private static string CacheKey(string query)
    {
        var material = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"{Operation}\n{MangaUpdatesProvider.ProviderId}\n{query.ToLowerInvariant()}\n{MuAuthorSearchRequest.FixedPage}"));
        return "metadata-author-search:" + Convert.ToHexString(SHA256.HashData(material));
    }
}
