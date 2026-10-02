namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Security.Cryptography;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

/// <summary>
/// The admin identify flow (1.24.0, lane B2): context (no network), search,
/// look-up by pasted URL/shortcode (parsed locally, only the id is sent), preview,
/// link-with-fetch, refresh and candidate images. Every provider call goes through
/// <see cref="MetadataGateway"/> for the node's library; nothing here runs unless
/// an admin asked for it.
/// 1.32.0 (lane B): a site switch - "Search on: MangaUpdates | Grand Comics Database" - defaulting to GCD for a node whose local
/// signs route to comics; a pasted <c>comics.org/series/&lt;id&gt;/</c> address is a GET by id; a GCD search answers with whole
/// series, so its preview reuses the searched series and reads only the publisher and the first issue (cover thumbnail shown
/// while choosing, never stored as a cover).
/// </summary>
public sealed class MetadataIdentifyService
{
    /// <summary>A stored record younger than this is reused by Preview / Link instead of a new GET.</summary>
    public static readonly TimeSpan RecordReuseWindow = TimeSpan.FromHours(24);

    /// <summary>Candidate image tokens and bytes live in memory this long.</summary>
    public static readonly TimeSpan CandidateTtl = TimeSpan.FromHours(1);

    /// <summary>The default site (MangaUpdates); 1.32.0 adds the comics site <see cref="ComicsProvider"/>.</summary>
    public const string DefaultProvider = MangaUpdatesProvider.ProviderId;

    /// <summary>The Grand Comics Database (1.32.0): Identify's second site, the default for comics-signalled nodes.</summary>
    public const string ComicsProvider = GcdMapping.ProviderId;

    private const int MaxLocalItems = 500;
    private const int MaxMeasuredPages = 400;
    private const double TallRatio = 2.0;

    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly MetadataBudget _budget;
    private readonly MetadataBackoff _backoff;
    private readonly MetadataLinkService _links;
    private readonly MetadataImageStore _images;
    private readonly MetadataProviderRegistry _providers;
    private readonly SeriesInfoResolver _resolver;
    private readonly AuditService _audit;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataIdentifyService> _logger;
    private readonly ICoverResolver _covers;
    private readonly Declared.IDeclaredFactsReader? _declared;
    private readonly MetadataAutoMatchState? _autoState;
    private readonly GcdDetails? _gcd;

    public MetadataIdentifyService(
        MangaPixerDbContext db,
        MetadataGateway gateway,
        MetadataBudget budget,
        MetadataBackoff backoff,
        MetadataLinkService links,
        MetadataImageStore images,
        MetadataProviderRegistry providers,
        SeriesInfoResolver resolver,
        AuditService audit,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<MetadataIdentifyService> logger,
        ICoverResolver? covers = null,
        Declared.IDeclaredFactsReader? declared = null,
        MetadataAutoMatchState? autoState = null,
        GcdDetails? gcd = null)
    {
        _db = db;
        _gateway = gateway;
        _budget = budget;
        _backoff = backoff;
        _links = links;
        _images = images;
        _providers = providers;
        _resolver = resolver;
        _audit = audit;
        _cache = cache;
        _time = time;
        _logger = logger;
        _covers = covers ?? new FileCoverResolver(db);
        _declared = declared;
        _autoState = autoState;
        _gcd = gcd;
    }

    /// <summary>Whether the comics site is registered (it is in the app; a test host may leave it out).</summary>
    private bool ComicsAvailable => _gcd is not null && _providers.Find(ComicsProvider) is not null;

    private sealed record CandidateImage(string Provider, long LibraryId, string Url);

    private sealed record CandidateBytes(byte[] Bytes, string ContentType);

    // --- Context (no network) ---

    public async Task<IdentifyContextDto?> GetContextAsync(string nodePublicId, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;

        var library = await _db.Libraries.AsNoTracking().Where(l => l.Id == node.LibraryId).Select(l => l.PublicId).FirstAsync(ct);
        var sites = await SitesAsync(node.LibraryId, ct);
        var comics = ComicsAvailable && await IsComicsSignalledAsync(node, ct);
        var provider = comics && sites.Any(x => x.Id == ComicsProvider && x.Available) ? ComicsProvider : DefaultProvider;
        var refusal = await _gateway.CheckSwitchesAsync(node.LibraryId, MetadataCallOrigin.Interactive, provider, ct);
        var local = await BuildLocalAsync(node, ct);
        var budget = await _budget.GetAsync(ct);

        var suggestions = new List<string>();
        void Add(string? s)
        {
            var q = MetadataGateway.NormalizeQuery(s);
            if (q.Length is > 0 and <= MetadataGateway.MaxQueryLength
                && !suggestions.Contains(q, StringComparer.OrdinalIgnoreCase) && suggestions.Count < 5)
                suggestions.Add(q);
        }
        var normalized = TitleNormalizer.Normalize(node.DisplayName);
        // "BLAME!" finds the record "Blame!" where "BLAME" does not: the name as written comes first.
        Add(normalized.PrimaryWithExclamation);
        foreach (var v in normalized.Variants)
            Add(v);
        if (local.ComicInfoSeries is { } ciSeries)
            Add(TitleNormalizer.Normalize(ciSeries).Primary is { Length: > 0 } p ? p : ciSeries);
        if (suggestions.Count == 0)
            Add(node.DisplayName);

        var ownLink = await _db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);
        var (content, _) = await MetadataFolderContentService.ResolveAsync(_db, node.Id, ct);

        return new IdentifyContextDto
        {
            NodeId = node.PublicId,
            NodeKind = (CatalogNodeKind)node.Kind,
            DisplayName = node.DisplayName,
            LibraryId = library,
            Provider = provider,
            ProviderName = _providers.DisplayNameFor(provider),
            Sites = sites,
            ComicsSignalled = comics,
            FetchAvailable = refusal is null,
            UnavailableCode = refusal?.Code,
            UnavailableMessage = refusal?.Message,
            Suggestions = suggestions,
            ComicInfoHint = await FindComicInfoHintAsync(node, ct),
            BudgetUsedToday = budget.Used,
            DailyBudget = budget.Limit,
            BackoffUntil = await _backoff.ActiveUntilAsync(provider, ct),
            CurrentLink = ownLink is null ? null : await ToLinkDtoAsync(ownLink, node.PublicId, ct),
            Local = local,
            DoujinshiContent = content == MetadataFolderContent.DoujinshiAndAdultOneShots,
        };
    }

    // --- Search ---

    public async Task<IdentifySearchResultDto?> SearchAsync(string nodePublicId, IdentifySearchRequest request, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;

        var provider = SiteOf(request.Provider);
        var name = TitleNormalizer.Normalize(node.DisplayName);
        // Only the (YYYY) of the node's own name may narrow a GCD search (what the consent text names).
        var startYear = provider == ComicsProvider && request.StartYear is { } year && year == name.YearHint ? year : (int?)null;
        var page = await _gateway.SearchAsync(
            provider, node.LibraryId, request.Query, request.Page, request.HideDoujinshiAndNovels, ct, startYear);
        var queries = name.Variants.Append(MetadataGateway.NormalizeQuery(request.Query)).ToList();

        var candidates = page.Hits
            .Select((hit, index) =>
            {
                var score = Rank(queries, [hit.Title, hit.HitTitle], name.YearHint, hit.Year);
                if (hit.Record is { } record)
                    RememberSearched(record);
                var extra = GcdExtra.Read(hit.Record?.ExtraJson);
                return (Index: index, Dto: new IdentifyCandidateDto
                {
                    ExternalId = hit.ExternalId,
                    Title = hit.Title,
                    HitTitle = hit.HitTitle,
                    ProviderType = hit.ProviderType,
                    Origin = hit.Record is { } r ? r.Origin : MangaUpdatesMapping.OriginOf(hit.ProviderType),
                    Format = hit.Record is { } f ? f.Format : MangaUpdatesMapping.FormatOf(hit.ProviderType),
                    Year = hit.Year,
                    Score = Math.Round(score, 3),
                    Strength = TitleSimilarity.Label(score),
                    ImageToken = hit.ImageRemoteUrl is { } url ? IssueImageToken(provider, node.LibraryId, url) : null,
                    Country = extra?.Country,
                    Language = extra?.Language,
                    UnitCount = extra?.Issues,
                    UnitKind = extra is null ? null : extra.ShapeValue == ComicsShape.Issues ? "issues" : "books",
                });
            })
            .OrderByDescending(c => c.Dto.Score)
            .ThenBy(c => c.Index)
            .Select(c => c.Dto)
            .ToList();

        var budget = await _budget.GetAsync(ct);
        return new IdentifySearchResultDto
        {
            Provider = provider,
            Page = Math.Clamp(request.Page, 1, 100),
            TotalHits = page.TotalHits,
            Candidates = candidates,
            BudgetUsedToday = budget.Used,
            DailyBudget = budget.Limit,
        };
    }

    // --- Look up / preview ---

    /// <summary>Parses a pasted reference LOCALLY, then previews that record (only the id is sent).</summary>
    public async Task<IdentifyPreviewDto?> LookupAsync(string nodePublicId, IdentifyLookupRequest request, CancellationToken ct = default)
    {
        var (kind, _) = MangaUpdatesReference.Parse(request.Reference);
        if (kind == MangaUpdatesReferenceKind.Legacy)
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "legacy_url",
                "This is an old-style MangaUpdates link (series.html?id=). Open the series on MangaUpdates and paste its current address (…/series/<code>/<name>).");
        var reference = _gateway.ParseReference(DefaultProvider, request.Reference ?? string.Empty);
        if (reference is null && ComicsAvailable)
        {
            // 1.32.0: a Grand Comics Database series address (or gcd:<number>) - only the number is sent, on the GET.
            if (GcdReference.Parse(request.Reference).Kind == GcdReferenceKind.Issue)
                throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "gcd_issue_url",
                    "This is a Grand Comics Database issue address. Open its series on comics.org and paste the series address (…/series/<number>/).");
            reference = _gateway.ParseReference(ComicsProvider, request.Reference ?? string.Empty);
        }
        if (reference is null)
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "invalid_reference",
                ComicsAvailable
                    ? "Paste a MangaUpdates series address or a shortcode like mu:12345, or a Grand Comics Database series address (comics.org/series/<number>/)."
                    : "Paste a MangaUpdates series address or a shortcode like mu:12345.");
        return await PreviewAsync(nodePublicId, new IdentifyPreviewRequest { Provider = reference.Provider, ExternalId = reference.ExternalId }, ct);
    }

    /// <summary>
    /// The record next to the local node. Reuses a stored record fetched within
    /// <see cref="RecordReuseWindow"/>; otherwise one GET, and the record is stored.
    /// </summary>
    public async Task<IdentifyPreviewDto?> PreviewAsync(string nodePublicId, IdentifyPreviewRequest request, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;

        var record = await EnsureRecordAsync(request.Provider, request.ExternalId, node.LibraryId, requireFresh: true, ct);
        var local = await BuildLocalAsync(node, ct);
        var name = TitleNormalizer.Normalize(node.DisplayName);
        var alt = MetadataJson.ReadList<string>(record.AltTitlesJson);
        var queries = name.Variants.ToList();
        if (local.ComicInfoSeries is { } series)
            queries.Add(series);
        var score = Rank(queries, alt.Prepend(record.Title), name.YearHint, record.StartYear);
        var providerName = _providers.DisplayNameFor(record.Provider);
        var extra = record.Provider == ComicsProvider ? GcdExtra.Read(record.ExtraJson) : null;
        // A GCD record has no stored poster: its first issue's thumbnail is shown while choosing (identification only).
        var imageUrl = record.ImageRemoteUrl
            ?? (record.Provider == ComicsProvider && _cache.TryGetValue<string>(ComicsCoverKey(record.ExternalId), out var cover) ? cover : null);

        return new IdentifyPreviewDto
        {
            Provider = record.Provider,
            ProviderName = providerName,
            ExternalId = record.ExternalId,
            Title = record.Title,
            AltTitles = alt,
            Description = record.Description,
            ProviderType = record.ProviderType,
            Origin = (MetadataOrigin?)record.Origin,
            Format = (MetadataFormat?)record.Format,
            Webtoon = record.Webtoon,
            StartYear = record.StartYear,
            OriginStatus = (MetadataOriginStatus?)record.OriginStatus,
            OriginVolumes = record.OriginVolumes,
            LatestChapter = record.LatestChapter,
            Creators = MetadataJson.ReadList<MetadataJson.Creator>(record.CreatorsJson)
                .Select(c => new SeriesCreatorDto { Name = c.Name, Role = c.Role }).ToList(),
            Genres = MetadataJson.ReadList<string>(record.GenresJson),
            SiteUrl = record.SiteUrl,
            ImageToken = imageUrl is { } url ? IssueImageToken(record.Provider, node.LibraryId, url) : null,
            Score = Math.Round(score, 3),
            Strength = TitleSimilarity.Label(score),
            FetchedAt = record.FetchedAt,
            Local = local,
            Warnings = Warnings(record, local, name.YearHint, await LocalUnitsAsync(node, ct), node.Kind == (int)CatalogNodeKind.Folder,
                await DeclaredTypeAsync(node, ct), providerName),
            Country = extra?.Country,
            Language = extra?.Language,
            Publishers = MetadataJson.ReadList<MetadataJson.Publisher>(record.PublishersJson).Select(p => p.Name).ToList(),
            Credit = record.Provider == ComicsProvider ? GcdMapping.Credit : null,
        };
    }

    // --- Link with fetch ---

    /// <summary>
    /// Links a node to a provider record, fetching and storing the record first
    /// when it is not stored yet (gated), then storing its poster (gated; a failed
    /// image never fails the link). A record that is already stored links WITHOUT
    /// any network call, as in lane B1.
    /// </summary>
    public async Task<(MetadataLinkResultCode Code, NodeSeriesLinkChangeDto? Change)> LinkAsync(
        string nodePublicId, LinkSeriesRequest request, string? actor, CancellationToken ct = default)
    {
        if (!MetadataIdentifiers.IsValidProvider(request.Provider) || !MetadataIdentifiers.IsValidExternalId(request.ExternalId))
            return (MetadataLinkResultCode.InvalidRequest, null);
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return (MetadataLinkResultCode.NodeNotFound, null);

        var stored = await _db.MetadataRecords.AnyAsync(r => r.Provider == request.Provider && r.ExternalId == request.ExternalId, ct);
        if (!stored)
        {
            if (_providers.Find(request.Provider) is null)
                return (MetadataLinkResultCode.RecordNotFound, null);
            await EnsureRecordAsync(request.Provider, request.ExternalId, node.LibraryId, requireFresh: false, ct);
        }

        var result = await _links.LinkAsync(nodePublicId, request, actor, ct);
        if (result.Code == MetadataLinkResultCode.Ok)
        {
            var record = await _db.MetadataRecords.FirstAsync(r => r.Provider == request.Provider && r.ExternalId == request.ExternalId, ct);
            if (record.ImageState != 1)
                await TryStoreImageAsync(record, node.LibraryId, ct);
        }
        return result;
    }

    // --- Refresh ---

    /// <summary>
    /// One GET for the record that applies to the node (its own link or the
    /// nearest inherited one). A 404 marks the record gone and keeps its data.
    /// Null when the node does not exist; <see cref="MetadataGatewayException"/>
    /// 404 <c>no_web_link</c> when no web record applies.
    /// </summary>
    public async Task<MetadataRefreshResultDto?> RefreshAsync(string nodePublicId, string? actor, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;

        var applied = await _resolver.ResolveWebRecordAsync(node, ct)
            ?? throw new MetadataGatewayException(StatusCodes.Status404NotFound, "no_web_link", "This item is not linked to a web series.");
        var record = await _db.MetadataRecords.FirstAsync(r => r.Id == applied.Id, ct);

        ProviderSeriesRecord? fetched;
        try
        {
            fetched = await _gateway.GetSeriesAsync(record.Provider, node.LibraryId, record.ExternalId, ct);
        }
        catch (MetadataGatewayException ex) when (ex.HttpStatus is StatusCodes.Status502BadGateway or StatusCodes.Status504GatewayTimeout)
        {
            // The provider failed (not a local refusal): remember that the last refresh did not work.
            record.FetchState = 2;
            await _db.SaveChangesAsync(ct);
            throw;
        }

        var now = _time.GetUtcNow();
        var imageUpdated = false;
        string state;
        if (fetched is null)
        {
            record.FetchState = 1;
            record.FetchedAt = now;
            state = "Gone";
            await _db.SaveChangesAsync(ct);
        }
        else
        {
            var oldImageUrl = record.ImageRemoteUrl;
            Apply(record, fetched, now);
            await _db.SaveChangesAsync(ct);
            state = "Ok";
            if (record.ImageRemoteUrl is not null && (record.ImageState != 1 || !string.Equals(oldImageUrl, record.ImageRemoteUrl, StringComparison.Ordinal)))
                imageUpdated = await TryStoreImageAsync(record, node.LibraryId, ct);
        }

        await _audit.RecordAsync(AuditActions.MetadataRefresh, state == "Ok" ? AuditResults.Success : "gone", actor, ct: ct,
            targetLibraryId: node.LibraryId, targetItemId: node.Id);
        return new MetadataRefreshResultDto { State = state, FetchedAt = now, ImageUpdated = imageUpdated };
    }

    // --- Candidate images ---

    /// <summary>
    /// The image behind a candidate token (search hit or preview), fetched once
    /// through the gateway and then served from memory for <see cref="CandidateTtl"/>.
    /// Null for an unknown or expired token.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType)?> GetCandidateImageAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 64 || !_cache.TryGetValue<CandidateImage>(TokenKey(token), out var candidate) || candidate is null)
            return null;
        if (_cache.TryGetValue<CandidateBytes>(BytesKey(token), out var cached) && cached is not null)
            return (cached.Bytes, cached.ContentType);

        var bytes = await _gateway.FetchImageAsync(candidate.Provider, candidate.LibraryId, candidate.Url, ct);
        var type = MetadataImageStore.ContentTypeFor(MetadataImageStore.DetectExtension(bytes)!);
        _cache.Set(BytesKey(token), new CandidateBytes(bytes, type), CandidateTtl);
        return (bytes, type);
    }

    internal string IssueImageToken(string provider, long libraryId, string url)
    {
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        _cache.Set(TokenKey(token), new CandidateImage(provider, libraryId, url), CandidateTtl);
        return token;
    }

    private static string TokenKey(string token) => "metadata-candidate:" + token;
    private static string BytesKey(string token) => "metadata-candidate-bytes:" + token;

    // --- Helpers ---

    private async Task<CatalogNodeEntity?> FindNodeAsync(string nodePublicId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.PublicId == nodePublicId, ct);
        return node is null || node.Availability == (int)CatalogNodeAvailability.Tombstoned ? null : node;
    }

    /// <summary>The stored record, fetching (gated) when missing or - with <paramref name="requireFresh"/> - older than the reuse window.</summary>
    private async Task<MetadataRecordEntity> EnsureRecordAsync(string provider, string externalId, long libraryId, bool requireFresh, CancellationToken ct)
    {
        if (!MetadataIdentifiers.IsValidProvider(provider) || !MetadataIdentifiers.IsValidExternalId(externalId))
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "invalid_request", "The record reference is not valid.");

        var record = await _db.MetadataRecords.FirstOrDefaultAsync(r => r.Provider == provider && r.ExternalId == externalId, ct);
        var now = _time.GetUtcNow();
        if (record is not null && (!requireFresh || now - record.FetchedAt < RecordReuseWindow))
            return record;

        // 1.32.0: a GCD series the admin just found in a search is that search's answer (no second request for it).
        var fetched = provider == ComicsProvider && _cache.TryGetValue<ProviderSeriesRecord>(SearchedKey(externalId), out var searched) && searched is not null
            ? searched
            : await _gateway.GetSeriesAsync(provider, libraryId, externalId, ct);
        if (fetched is not null && provider == ComicsProvider && _gcd is not null)
            fetched = await WithComicsDetailsAsync(fetched, libraryId, ct);
        if (fetched is null)
        {
            if (record is not null)
            {
                record.FetchState = 1;
                await _db.SaveChangesAsync(ct);
            }
            throw new MetadataGatewayException(StatusCodes.Status404NotFound, "record_gone",
                "The provider has no series with that id.");
        }

        if (record is null)
        {
            record = new MetadataRecordEntity
            {
                PublicId = await NewPublicIdAsync(ct),
                Provider = fetched.Provider,
                ExternalId = fetched.ExternalId,
            };
            _db.MetadataRecords.Add(record);
        }
        Apply(record, fetched, now);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation(LogEvents.Metadata.RecordStored, "Metadata record {RecordId} stored ({Provider} {ExternalId})",
            record.Id, record.Provider, record.ExternalId);
        return record;
    }

    /// <summary>
    /// The chosen GCD candidate's details (1.32.0, best effort): its publisher's name and its first issue - credits, and the cover
    /// thumbnail remembered for the preview only (never stored as the record's image).
    /// </summary>
    private async Task<ProviderSeriesRecord> WithComicsDetailsAsync(ProviderSeriesRecord fetched, long libraryId, CancellationToken ct)
    {
        fetched = await _gcd!.WithPublisherAsync(fetched, libraryId, null, ct);
        var issue = await _gcd.FirstIssueAsync(fetched, libraryId, null, ct);
        if (issue?.CoverUrl is { } cover)
            _cache.Set(ComicsCoverKey(fetched.ExternalId), cover, CandidateTtl);
        return GcdDetails.WithIssue(fetched, issue);
    }

    private void RememberSearched(ProviderSeriesRecord record) => _cache.Set(SearchedKey(record.ExternalId), record, CandidateTtl);

    private static string SearchedKey(string externalId) => "metadata-gcd-searched:" + externalId;
    private static string ComicsCoverKey(string externalId) => "metadata-gcd-cover:" + externalId;

    /// <summary>The site a request names: the comics site when asked for and registered, else MangaUpdates.</summary>
    private string SiteOf(string? provider) =>
        provider == ComicsProvider && ComicsAvailable ? ComicsProvider
        : provider is null or DefaultProvider ? DefaultProvider
        : throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "unknown_provider", "No such metadata provider.");

    /// <summary>Identify's sites (1.32.0), each with what refuses it right now (gates 1-3 and the allowlist).</summary>
    private async Task<List<IdentifySiteDto>> SitesAsync(long libraryId, CancellationToken ct)
    {
        var sites = new List<IdentifySiteDto>();
        foreach (var id in ComicsAvailable ? new[] { DefaultProvider, ComicsProvider } : [DefaultProvider])
        {
            var refusal = await _gateway.CheckSwitchesAsync(libraryId, MetadataCallOrigin.Interactive, id, ct);
            sites.Add(new IdentifySiteDto
            {
                Id = id,
                Name = _providers.DisplayNameFor(id),
                Available = refusal is null,
                UnavailableCode = refusal?.Code,
                Note = id == ComicsProvider ? "Comics and graphic novels. Answers about 25 requests an hour." : null,
            });
        }
        return sites;
    }

    /// <summary>
    /// Whether the node's local signs route it to comics (1.32.0) - the same reader automatic matching uses: the declared type,
    /// a comics category folder, and (with lane A's detectors) ComicInfo ids / publishers and file numbering. Local only.
    /// </summary>
    private async Task<bool> IsComicsSignalledAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var folderId = node.Kind == (int)CatalogNodeKind.Folder ? node.Id : node.ParentId;
        if (folderId is not { } folder)
            return false;
        var revision = await _db.Libraries.AsNoTracking().Where(l => l.Id == node.LibraryId).Select(l => l.CatalogRevision).FirstOrDefaultAsync(ct);
        var tree = _autoState?.CachedSnapshot(node.LibraryId, revision);
        if (tree is null)
        {
            tree = await LibraryTreeSnapshot.LoadAsync(_db, node.LibraryId, ct);
            _autoState?.Cache(tree);
        }
        if (tree.Find(folder) is null)
            return false;
        var shape = tree.ShapeOf(folder);
        var query = new MatchQuery([], new MatchContext(WorkClass.Series, 0, 0, 0, null, shape.CategoryHint, false, [],
            DeclaredType: await DeclaredTypeAsync(node, ct)));
        query = await ComicsSignalReader.ApplyAsync(_db, query, shape, await LocalArchiveIdsAsync(node, ct), ct);
        return query.Context.Comics?.RoutesToComics == true;
    }

    /// <summary>Maps a fetched provider record onto the stored entity (shared by identify, auto-match and refresh).</summary>
    internal static void Apply(MetadataRecordEntity record, ProviderSeriesRecord fetched, DateTimeOffset now)
    {
        // 1.32.0: a Grand Comics Database series payload names its publisher by id only and carries no credits; what a chosen
        // candidate's details added (publisher name, first-issue credits) is kept while the publisher is the same.
        fetched = Providers.Gcd.GcdMapping.KeepDetails(record, fetched);
        if (fetched.ExtraJson is { } extra)
            record.ExtraJson = extra;
        record.SourceKind = (int)fetched.SourceKind;
        record.RecordKind = 0;
        record.Title = fetched.Title;
        record.AltTitlesJson = MetadataJson.WriteList(fetched.AltTitles);
        record.Description = fetched.Description;
        record.Origin = (int?)fetched.Origin;
        record.Format = (int?)fetched.Format;
        record.Webtoon = fetched.Webtoon;
        record.ProviderType = fetched.ProviderType;
        record.StartYear = fetched.StartYear;
        record.OriginStatus = (int?)fetched.OriginStatus;
        record.OriginVolumes = fetched.OriginVolumes;
        record.LatestChapter = fetched.LatestChapter;
        record.StatusText = fetched.StatusText;
        record.LicensedEn = fetched.LicensedEn;
        record.TranslationComplete = fetched.TranslationComplete;
        record.CreatorsJson = MetadataJson.WriteList(fetched.Creators);
        record.GenresJson = MetadataJson.WriteList(fetched.Genres);
        record.CategoriesJson = MetadataJson.WriteList(fetched.Categories);
        record.PublishersJson = MetadataJson.WriteList(fetched.Publishers);
        record.CrossIdsJson = fetched.CrossIds.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(fetched.CrossIds);
        record.PublicationsJson = MetadataJson.WriteList(fetched.Publications);
        record.RelationsJson = MetadataJson.WriteList(fetched.Relations);
        record.SiteUrl = fetched.SiteUrl;
        if (!string.Equals(record.ImageRemoteUrl, fetched.ImageRemoteUrl, StringComparison.Ordinal))
        {
            record.ImageRemoteUrl = fetched.ImageRemoteUrl;
            if (record.ImageState != 1)
                record.ImageState = 0;
        }
        record.ProviderUpdatedAt = fetched.ProviderUpdatedAt;
        record.FetchedAt = now;
        record.FetchState = 0;
    }

    /// <summary>Fetches and stores the record's poster; returns true when a new image was stored. Never throws for provider trouble.</summary>
    internal async Task<bool> TryStoreImageAsync(MetadataRecordEntity record, long libraryId, CancellationToken ct, MetadataCallContext? call = null)
    {
        if (record.ImageRemoteUrl is not { } url)
            return false;
        try
        {
            var bytes = await _gateway.FetchImageAsync(record.Provider, libraryId, url, ct, call);
            var version = record.ImageVersion + 1;
            await _images.PublishAsync(record.Id, version, bytes, ct);
            record.ImageVersion = version;
            record.ImageState = 1;
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (MetadataGatewayException ex)
        {
            _logger.LogInformation(LogEvents.Metadata.ImageRejected, "Series image for record {RecordId} not stored: {Code}", record.Id, ex.Code);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or MetadataResponseInvalidException)
        {
            _logger.LogWarning(LogEvents.Metadata.ImageStoreFailed, "Series image for record {RecordId} not stored: {Error}", record.Id, ex.GetType().Name);
        }
        if (record.ImageState != 1)
        {
            record.ImageState = 2;
            await _db.SaveChangesAsync(ct);
        }
        return false;
    }

    internal async Task<string> NewPublicIdAsync(CancellationToken ct)
    {
        while (true)
        {
            var id = "mr" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();
            if (!await _db.MetadataRecords.AnyAsync(r => r.PublicId == id, ct))
                return id;
        }
    }

    private async Task<List<long>> LocalArchiveIdsAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        if (node.Kind == (int)CatalogNodeKind.Archive)
            return [node.Id];
        var live = (int)CatalogNodeAvailability.Tombstoned;
        var children = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId == node.Id && n.Availability != live)
            .Select(n => new { n.Id, n.Kind })
            .ToListAsync(ct);
        var archives = children.Where(c => c.Kind == (int)CatalogNodeKind.Archive).Select(c => c.Id).ToList();
        var folders = children.Where(c => c.Kind == (int)CatalogNodeKind.Folder).Select(c => c.Id).ToList();
        if (folders.Count > 0 && archives.Count < MaxLocalItems)
        {
            archives.AddRange(await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.ParentId != null && folders.Contains(n.ParentId.Value) && n.Kind == (int)CatalogNodeKind.Archive && n.Availability != live)
                .OrderBy(n => n.Id)
                .Select(n => n.Id)
                .Take(MaxLocalItems)
                .ToListAsync(ct));
        }
        return archives.Take(MaxLocalItems).ToList();
    }

    private async Task<IdentifyLocalDto> BuildLocalAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var archiveIds = await LocalArchiveIdsAsync(node, ct);

        var seriesNames = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => archiveIds.Contains(e.NodeId) && e.State == 1 && e.Series != null)
            .Select(e => e.Series!)
            .ToListAsync(ct);
        var comicInfoSeries = seriesNames
            .GroupBy(s => s.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key)
            .FirstOrDefault();

        var dims = await _db.PageEntries.AsNoTracking()
            .Where(p => archiveIds.Contains(p.ItemId) && p.Width != null && p.Height != null && p.Width > 0)
            .OrderBy(p => p.ItemId).ThenBy(p => p.Ordinal)
            .Select(p => new { p.Width, p.Height })
            .Take(MaxMeasuredPages)
            .ToListAsync(ct);
        bool? tall = dims.Count < 20 ? null : dims.Count(d => d.Height >= d.Width * TallRatio) * 2 >= dims.Count;

        return new IdentifyLocalDto
        {
            DisplayName = node.DisplayName,
            ItemCount = archiveIds.Count,
            ComicInfoSeries = comicInfoSeries,
            TallStrips = tall,
            YearHint = TitleNormalizer.Normalize(node.DisplayName).YearHint,
            CoverUrl = (await _covers.ResolveUrlsAsync([new CoverTarget(node.Id, node.PublicId, node.Kind == (int)CatalogNodeKind.Folder)], ct))
                .GetValueOrDefault(node.Id),
        };
    }

    private async Task<IdentifyReferenceDto?> FindComicInfoHintAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var archiveIds = await LocalArchiveIdsAsync(node, ct);
        var webJson = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => archiveIds.Contains(e.NodeId) && e.State == 1 && e.WebUrlsJson != null)
            .OrderBy(e => e.NodeId)
            .Select(e => e.WebUrlsJson)
            .Take(50)
            .ToListAsync(ct);
        foreach (var json in webJson)
        {
            foreach (var url in MetadataJson.ReadList<string>(json))
            {
                var reference = _gateway.ParseReference(DefaultProvider, url)
                    ?? (ComicsAvailable ? _gateway.ParseReference(ComicsProvider, url) : null);
                if (reference is not null)
                    return new IdentifyReferenceDto { Provider = reference.Provider, ExternalId = reference.ExternalId };
            }
        }
        return null;
    }

    private async Task<NodeSeriesLinkDto> ToLinkDtoAsync(NodeSeriesLinkEntity link, string nodePublicId, CancellationToken ct)
    {
        var record = link.RecordId is { } id
            ? await _db.MetadataRecords.AsNoTracking().Where(r => r.Id == id).Select(r => new { r.Provider, r.ExternalId, r.PublicId }).FirstOrDefaultAsync(ct)
            : null;
        return new NodeSeriesLinkDto
        {
            NodeId = nodePublicId,
            State = (SeriesLinkState)link.State,
            Provider = record?.Provider,
            ExternalId = record?.ExternalId,
            RecordId = record?.PublicId,
            MatchMethod = (MetadataMatchMethod?)link.MatchMethod,
            UpdatedAt = link.UpdatedAt,
        };
    }

    /// <summary>
    /// Display-only ranking: best similarity (the first title is the record's main title; a trailing author disambiguator on it
    /// or on another title counts as in the matcher, 1.29.0), nudged by the year hint.
    /// </summary>
    internal static double Rank(IEnumerable<string> queries, IEnumerable<string?> titles, int? yearHint, int? year, IEnumerable<string>? authors = null)
    {
        var list = titles.ToList();
        var score = AutoMatchText.BestTitleScore(queries, list.FirstOrDefault(), list.Skip(1), authors);
        if (yearHint is { } hint && year is { } y)
            score += Math.Abs(hint - y) <= 1 ? 0.05 : -0.10;
        return Math.Clamp(score, 0, 1);
    }

    /// <summary>
    /// The count rule's local side of a node (1.29.0, <see cref="CountEvidence.LocalOf"/>): an archive's own name, or a
    /// folder's archives plus the archive names below its unit subfolders (Volumes, Chapters, Season 2 ...; bounded).
    /// Other subfolders are separate works and are not read.
    /// </summary>
    private async Task<LocalUnitCounts> LocalUnitsAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        if (node.Kind == (int)CatalogNodeKind.Archive)
            return CountEvidence.LocalOf([node.DisplayName], null);
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var children = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId == node.Id && n.Availability != tombstoned)
            .OrderBy(n => n.SortKey)
            .Select(n => new { n.Id, n.Kind, n.DisplayName })
            .Take(MaxLocalItems)
            .ToListAsync(ct);
        var loose = children.Where(c => c.Kind != folder).Select(c => c.DisplayName).ToList();
        var units = children.Where(c => c.Kind == folder && AutoMatchText.IsUnitFolderName(c.DisplayName)).ToList();
        var names = units.ToDictionary(u => u.Id, _ => new List<string>());
        var rootOf = units.ToDictionary(u => u.Id, u => u.Id);
        var frontier = units.Select(u => u.Id).ToList();
        var total = 0;
        for (var depth = 0; depth < 3 && frontier.Count > 0 && total < MaxLocalItems; depth++)
        {
            var level = frontier;
            var rows = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.ParentId != null && level.Contains(n.ParentId.Value) && n.Availability != tombstoned)
                .OrderBy(n => n.SortKey)
                .Select(n => new { n.Id, ParentId = n.ParentId!.Value, n.Kind, n.DisplayName })
                .Take(MaxLocalItems)
                .ToListAsync(ct);
            frontier = [];
            foreach (var r in rows)
            {
                if (r.Kind == folder)
                {
                    rootOf[r.Id] = rootOf[r.ParentId];
                    frontier.Add(r.Id);
                }
                else if (total++ < MaxLocalItems)
                {
                    names[rootOf[r.ParentId]].Add(r.DisplayName);
                }
            }
        }
        return CountEvidence.LocalOf(loose, units.Select(u => new ChildFolderShape(u.DisplayName, names[u.Id].Count, names[u.Id])));
    }

    /// <summary>The published totals of a stored record, as the matcher reads them from the provider's record.</summary>
    internal static PublishedUnitCounts PublishedOf(MetadataRecordEntity record)
    {
        var english = MetadataJson.ReadList<MetadataJson.Publisher>(record.PublishersJson)
            .Where(p => string.Equals(p.Kind, "english", StringComparison.Ordinal))
            .ToList();
        return new PublishedUnitCounts(
            record.OriginVolumes,
            english.Max(p => p.Volumes),
            MangaUpdatesStatusParser.Parse(record.StatusText).Chapters,
            english.Max(p => p.Chapters),
            record.LatestChapter is { } latest ? (int)Math.Floor(latest) : null);
    }

    /// <summary>
    /// The count warning (1.29.0): the matcher's count rule (<see cref="CountEvidence"/>) - volumes against the record's
    /// volume total, chapters against its chapter total, each sentence naming its unit; nothing when the record states no
    /// total for the unit the folder has, or the folder mixes volumes and chapters.
    /// </summary>
    internal static IEnumerable<string> CountWarnings(LocalUnitCounts units, PublishedUnitCounts published, bool isFolder)
    {
        var count = CountEvidence.Compare(units, published);
        var where = isFolder ? "this folder" : "this archive";
        if (count.Volumes == CountSignal.Conflict)
            yield return FormattableString.Invariant(
                $"The record lists {count.PublishedVolumes} volumes; {where} has {CountEvidence.Describe(units, volumes: true)}.");
        if (count.Chapters == CountSignal.Conflict)
        {
            var total = Math.Max(published.StatusChapters ?? 0, published.EnglishChapters ?? 0);
            yield return total >= count.PublishedChapters
                ? FormattableString.Invariant($"The record lists {total} chapters; {where} has {CountEvidence.Describe(units, volumes: false)}.")
                : FormattableString.Invariant(
                    $"The record's latest chapter is {count.PublishedChapters}; {where} has {CountEvidence.Describe(units, volumes: false)}.");
        }
    }

    /// <summary>The type declared for a folder (or an archive's folder), or null (1.30.0).</summary>
    private async Task<DeclaredType?> DeclaredTypeAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        if (_declared is null)
            return null;
        var effective = await _declared.EffectiveForLibraryAsync(node.LibraryId, ct);
        var folderId = node.Kind == (int)CatalogNodeKind.Folder ? node.Id : node.ParentId;
        return folderId is { } id && effective.TryGetValue(id, out var facts) ? facts.TypeValue : null;
    }

    /// <summary>
    /// The declared-type warning (1.30.0): the record contradicts the type declared for the folder - the same rule as the Info
    /// panel's conflict badge and the matcher's evidence (<see cref="DeclaredFactsComparer.TypeSignal"/>). A hint, never a block.
    /// </summary>
    internal static IdentifyWarningDto? DeclaredTypeWarning(DeclaredType? declared, MetadataRecordEntity record, string providerName = "MangaUpdates")
    {
        if (declared is not { } type
            || DeclaredFactsComparer.TypeSignal(type, (MetadataOrigin?)record.Origin, (MetadataFormat?)record.Format, record.Webtoon)
                != DeclaredTypeSignal.Mismatch)
            return null;
        var what = record.ProviderType is { Length: > 0 } t ? t : "another type";
        return new IdentifyWarningDto
        {
            Code = "declared_type",
            Message = $"This folder is declared {DeclaredFactKeys.TypeLabel(type)}; {providerName} lists this record as {what}. "
                + "A declared type is a hint: it does not block the link.",
        };
    }

    private static List<IdentifyWarningDto> Warnings(MetadataRecordEntity record, IdentifyLocalDto local, int? yearHint, LocalUnitCounts units, bool isFolder,
        DeclaredType? declared = null, string providerName = "MangaUpdates")
    {
        var warnings = new List<IdentifyWarningDto>();
        var nameLower = local.DisplayName.ToLowerInvariant();
        switch ((MetadataFormat?)record.Format)
        {
            case MetadataFormat.Novel when !nameLower.Contains("novel", StringComparison.Ordinal):
                warnings.Add(new IdentifyWarningDto { Code = "format_novel", Message = "MangaUpdates lists this record as a novel, not a comic." });
                break;
            case MetadataFormat.Artbook when !nameLower.Contains("artbook", StringComparison.Ordinal) && !nameLower.Contains("art book", StringComparison.Ordinal):
                warnings.Add(new IdentifyWarningDto { Code = "format_artbook", Message = "MangaUpdates lists this record as an artbook." });
                break;
        }
        if (yearHint is { } hint && record.StartYear is { } year && Math.Abs(hint - year) > 1)
            warnings.Add(new IdentifyWarningDto { Code = "year_mismatch", Message = $"The name says {hint}; the record starts in {year}." });
        foreach (var message in CountWarnings(units, PublishedOf(record), isFolder))
            warnings.Add(new IdentifyWarningDto { Code = "count_mismatch", Message = message });
        if (DeclaredTypeWarning(declared, record, providerName) is { } typeWarning)
            warnings.Add(typeWarning);
        if (record.FetchState == 1)
            warnings.Add(new IdentifyWarningDto { Code = "record_gone", Message = $"{providerName} no longer lists this record." });
        return warnings;
    }
}
