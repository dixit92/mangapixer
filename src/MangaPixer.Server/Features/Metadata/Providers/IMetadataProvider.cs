namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers;

using com.lifepixer.mangapixer.Core.Metadata;

// The provider seam (1.24.0 stage 1). Lane B1 defines it with NO implementation;
// lane B2 adds MangaUpdates behind a MetadataGateway. Providers only translate
// HTTP/JSON (or local-dump rows) into ProviderSeriesRecord; every policy (kill
// switch, global + library switches, consent, budget, rate limit, backoff,
// logging, audit) lives in the gateway, and a provider is never called except
// through it.

/// <summary>A metadata source: an online API or a local database dump.</summary>
public interface IMetadataProvider
{
    /// <summary>Stable slug stored in <c>metadata_records.Provider</c>, e.g. <c>mangaupdates</c>.</summary>
    string Id { get; }

    /// <summary>Attribution name, e.g. <c>MangaUpdates</c>.</summary>
    string DisplayName { get; }

    MetadataProviderKind Kind { get; }

    MetadataCapabilities Capabilities { get; }

    /// <summary>
    /// Parses a pasted provider URL or shortcode (<c>mu:12345</c>) into a record
    /// reference WITHOUT any network call.
    /// </summary>
    bool TryParseReference(string input, out ProviderRef reference);

    Task<ProviderSearchPage> SearchSeriesAsync(ProviderSearchQuery query, CancellationToken ct);

    /// <summary>The full record, or null when the provider says it does not exist.</summary>
    Task<ProviderSeriesRecord?> GetSeriesAsync(string externalId, CancellationToken ct);
}

public enum MetadataProviderKind
{
    Online = 0,
    LocalDatabase = 1,
}

[Flags]
public enum MetadataCapabilities
{
    None = 0,
    SeriesSearch = 1,
    SeriesGet = 2,
    Covers = 4,
    Releases = 8,
    Units = 16,
}

/// <summary>A provider record reference parsed locally.</summary>
public sealed record ProviderRef(string Provider, string ExternalId);

/// <summary>
/// A search the admin confirmed. <see cref="LibraryId"/> is the library the call is made for (gateway gate).
/// <see cref="HideDoujinshiAndNovels"/> asks the provider to leave doujinshi, novels, artbooks and drama CDs out;
/// <see cref="AllowDoujinshi"/> (stage 2: automatic searches below a folder whose Content is "Doujinshi &amp; adult
/// one-shots") keeps doujinshi in that filter. <see cref="DeclaredType"/> (1.28.0, automatic searches only, on unless
/// <c>Metadata:AutoMatch:DeclaredTypeFilter=false</c>): the type an admin declared for the folder; for manga / manhwa / manhua
/// the provider also leaves the other two origins out.
/// </summary>
public sealed record ProviderSearchQuery(
    string Text, long LibraryId, int Page = 1, int PerPage = 10, bool HideDoujinshiAndNovels = false, bool AllowDoujinshi = false,
    Core.Metadata.DeclaredType? DeclaredType = null);

public sealed record ProviderSearchPage(IReadOnlyList<ProviderSearchHit> Hits, int TotalHits);

/// <summary>One search hit, before any GET. Never persisted.</summary>
public sealed record ProviderSearchHit
{
    public required string ExternalId { get; init; }
    public required string Title { get; init; }

    /// <summary>The title variant the provider matched on, when it differs.</summary>
    public string? HitTitle { get; init; }
    public string? ProviderType { get; init; }
    public int? Year { get; init; }
    public string? ImageRemoteUrl { get; init; }
}

/// <summary>
/// A full provider record, 1:1 with the <c>metadata_records</c> columns (the gateway
/// maps it onto the entity).
/// </summary>
public sealed record ProviderSeriesRecord
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
    public MetadataSourceKind SourceKind { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> AltTitles { get; init; } = [];
    public string? Description { get; init; }
    public MetadataOrigin? Origin { get; init; }
    public MetadataFormat? Format { get; init; }
    public bool? Webtoon { get; init; }
    public string? ProviderType { get; init; }
    public int? StartYear { get; init; }
    public MetadataOriginStatus? OriginStatus { get; init; }
    public int? OriginVolumes { get; init; }
    public double? LatestChapter { get; init; }

    /// <summary>
    /// The chapter total the provider states (MangaUpdates status "195 Chapters (Hiatus)", 1.27.0). Read by the
    /// auto-match count rule only; not persisted (the status text is).
    /// </summary>
    public int? TotalChapters { get; init; }

    /// <summary>
    /// The English publishers' largest stated volume / chapter totals (MangaUpdates <c>publishers[].notes</c> where
    /// the type is English, 1.27.0; same GET, nothing extra sent). Read by the auto-match count rule; not persisted.
    /// </summary>
    public int? EnglishVolumes { get; init; }
    public int? EnglishChapters { get; init; }
    public string? StatusText { get; init; }
    public bool? LicensedEn { get; init; }
    public bool? TranslationComplete { get; init; }
    public IReadOnlyList<MetadataJson.Creator> Creators { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>Top ~20 categories by votes (webtoon flag + stage-2 ranking; never displayed in stage 1).</summary>
    public IReadOnlyList<MetadataJson.Category> Categories { get; init; } = [];
    public IReadOnlyList<MetadataJson.Publisher> Publishers { get; init; } = [];
    public IReadOnlyDictionary<string, string> CrossIds { get; init; } = new Dictionary<string, string>();

    /// <summary>Serialization venues (stage 2; facet-ready).</summary>
    public IReadOnlyList<MetadataJson.Publication> Publications { get; init; } = [];

    /// <summary>Related records (stage 2: the related-pair rule of auto-match).</summary>
    public IReadOnlyList<MetadataJson.RelatedRecord> Relations { get; init; } = [];
    public string? SiteUrl { get; init; }
    public string? ImageRemoteUrl { get; init; }
    public DateTimeOffset? ProviderUpdatedAt { get; init; }
}
