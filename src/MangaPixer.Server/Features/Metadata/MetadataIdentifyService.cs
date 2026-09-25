namespace com.lifepixer.mangapixer.Server.Features.Metadata;

using System.Security.Cryptography;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
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
/// </summary>
public sealed class MetadataIdentifyService
{
    /// <summary>A stored record younger than this is reused by Preview / Link instead of a new GET.</summary>
    public static readonly TimeSpan RecordReuseWindow = TimeSpan.FromHours(24);

    /// <summary>Candidate image tokens and bytes live in memory this long.</summary>
    public static readonly TimeSpan CandidateTtl = TimeSpan.FromHours(1);

    /// <summary>Stage 1 has exactly one provider.</summary>
    public const string DefaultProvider = MangaUpdatesProvider.ProviderId;

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
        ILogger<MetadataIdentifyService> logger)
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
    }

    private sealed record CandidateImage(string Provider, long LibraryId, string Url);

    private sealed record CandidateBytes(byte[] Bytes, string ContentType);

    // --- Context (no network) ---

    public async Task<IdentifyContextDto?> GetContextAsync(string nodePublicId, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;

        var library = await _db.Libraries.AsNoTracking().Where(l => l.Id == node.LibraryId).Select(l => l.PublicId).FirstAsync(ct);
        var refusal = await _gateway.CheckSwitchesAsync(node.LibraryId, ct);
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
        foreach (var v in normalized.Variants)
            Add(v);
        if (local.ComicInfoSeries is { } ciSeries)
            Add(TitleNormalizer.Normalize(ciSeries).Primary is { Length: > 0 } p ? p : ciSeries);
        if (suggestions.Count == 0)
            Add(node.DisplayName);

        var ownLink = await _db.NodeSeriesLinks.AsNoTracking().FirstOrDefaultAsync(l => l.NodeId == node.Id, ct);

        return new IdentifyContextDto
        {
            NodeId = node.PublicId,
            NodeKind = (CatalogNodeKind)node.Kind,
            DisplayName = node.DisplayName,
            LibraryId = library,
            Provider = DefaultProvider,
            ProviderName = _providers.DisplayNameFor(DefaultProvider),
            FetchAvailable = refusal is null,
            UnavailableCode = refusal?.Code,
            UnavailableMessage = refusal?.Message,
            Suggestions = suggestions,
            ComicInfoHint = await FindComicInfoHintAsync(node, ct),
            BudgetUsedToday = budget.Used,
            DailyBudget = budget.Limit,
            BackoffUntil = await _backoff.ActiveUntilAsync(ct),
            CurrentLink = ownLink is null ? null : await ToLinkDtoAsync(ownLink, node.PublicId, ct),
            Local = local,
        };
    }

    // --- Search ---

    public async Task<IdentifySearchResultDto?> SearchAsync(string nodePublicId, IdentifySearchRequest request, CancellationToken ct = default)
    {
        var node = await FindNodeAsync(nodePublicId, ct);
        if (node is null)
            return null;

        var page = await _gateway.SearchAsync(DefaultProvider, node.LibraryId, request.Query, request.Page, ct);
        var name = TitleNormalizer.Normalize(node.DisplayName);
        var queries = name.Variants.Append(MetadataGateway.NormalizeQuery(request.Query)).ToList();

        var candidates = page.Hits
            .Select((hit, index) =>
            {
                var score = Rank(queries, [hit.Title, hit.HitTitle], name.YearHint, hit.Year);
                return (Index: index, Dto: new IdentifyCandidateDto
                {
                    ExternalId = hit.ExternalId,
                    Title = hit.Title,
                    HitTitle = hit.HitTitle,
                    ProviderType = hit.ProviderType,
                    Origin = MangaUpdatesMapping.OriginOf(hit.ProviderType),
                    Format = MangaUpdatesMapping.FormatOf(hit.ProviderType),
                    Year = hit.Year,
                    Score = Math.Round(score, 3),
                    Strength = TitleSimilarity.Label(score),
                    ImageToken = hit.ImageRemoteUrl is { } url ? IssueImageToken(DefaultProvider, node.LibraryId, url) : null,
                });
            })
            .OrderByDescending(c => c.Dto.Score)
            .ThenBy(c => c.Index)
            .Select(c => c.Dto)
            .ToList();

        var budget = await _budget.GetAsync(ct);
        return new IdentifySearchResultDto
        {
            Provider = DefaultProvider,
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
        if (reference is null)
            throw new MetadataGatewayException(StatusCodes.Status400BadRequest, "invalid_reference",
                "Paste a MangaUpdates series address or a shortcode like mu:12345.");
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

        return new IdentifyPreviewDto
        {
            Provider = record.Provider,
            ProviderName = _providers.DisplayNameFor(record.Provider),
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
            ImageToken = record.ImageRemoteUrl is { } url ? IssueImageToken(record.Provider, node.LibraryId, url) : null,
            Score = Math.Round(score, 3),
            Strength = TitleSimilarity.Label(score),
            FetchedAt = record.FetchedAt,
            Local = local,
            Warnings = Warnings(record, local, name.YearHint),
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

    private string IssueImageToken(string provider, long libraryId, string url)
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

        var fetched = await _gateway.GetSeriesAsync(provider, libraryId, externalId, ct);
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

    private static void Apply(MetadataRecordEntity record, ProviderSeriesRecord fetched, DateTimeOffset now)
    {
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
    private async Task<bool> TryStoreImageAsync(MetadataRecordEntity record, long libraryId, CancellationToken ct)
    {
        if (record.ImageRemoteUrl is not { } url)
            return false;
        try
        {
            var bytes = await _gateway.FetchImageAsync(record.Provider, libraryId, url, ct);
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

    private async Task<string> NewPublicIdAsync(CancellationToken ct)
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
        };
    }

    private async Task<IdentifyReferenceDto?> FindComicInfoHintAsync(CatalogNodeEntity node, CancellationToken ct)
    {
        var archiveIds = await LocalArchiveIdsAsync(node, ct);
        var webJson = await _db.EmbeddedMetadata.AsNoTracking()
            .Where(e => archiveIds.Contains(e.NodeId) && e.State == 1 && e.WebUrlsJson != null)
            .Select(e => e.WebUrlsJson)
            .Take(50)
            .ToListAsync(ct);
        foreach (var json in webJson)
        {
            foreach (var url in MetadataJson.ReadList<string>(json))
            {
                if (_gateway.ParseReference(DefaultProvider, url) is { } reference)
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

    /// <summary>Display-only ranking: best similarity, nudged by the year hint.</summary>
    internal static double Rank(IEnumerable<string> queries, IEnumerable<string?> titles, int? yearHint, int? year)
    {
        var score = TitleSimilarity.Best(queries, titles.OfType<string>());
        if (yearHint is { } hint && year is { } y)
            score += Math.Abs(hint - y) <= 1 ? 0.05 : -0.10;
        return Math.Clamp(score, 0, 1);
    }

    private static List<IdentifyWarningDto> Warnings(MetadataRecordEntity record, IdentifyLocalDto local, int? yearHint)
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
        var expected = Math.Max(record.OriginVolumes ?? 0, (int)Math.Ceiling(record.LatestChapter ?? 0));
        if (expected > 0 && local.ItemCount > expected * 3 / 2 + 2)
            warnings.Add(new IdentifyWarningDto
            {
                Code = "count_mismatch",
                Message = $"The record lists {expected} volumes/chapters; this folder has {local.ItemCount} items.",
            });
        if (record.FetchState == 1)
            warnings.Add(new IdentifyWarningDto { Code = "record_gone", Message = "MangaUpdates no longer lists this record." });
        return warnings;
    }
}
