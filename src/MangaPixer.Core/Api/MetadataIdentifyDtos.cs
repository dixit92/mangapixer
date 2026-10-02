namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;

// Admin identify flow DTOs (1.24.0 stage 1, lane B2). Admin-only endpoints; no
// DTO carries a filesystem path or a provider URL other than the record's public
// site page (attribution). Candidate images are referenced by short-lived,
// server-side tokens, so no provider image URL ever reaches the browser.

/// <summary>Everything the identify dialog needs before any network call.</summary>
public sealed record IdentifyContextDto
{
    public required string NodeId { get; init; }
    public required CatalogNodeKind NodeKind { get; init; }
    public required string DisplayName { get; init; }
    public required string LibraryId { get; init; }

    /// <summary>
    /// The site the dialog searches first: <c>mangaupdates</c>, or (1.32.0) <c>gcd</c> - the Grand Comics Database - for a node whose
    /// local signs route to comics (a Comic / Graphic novel declaration, a comics category folder ...) while GCD is allowed.
    /// </summary>
    public required string Provider { get; init; }
    public required string ProviderName { get; init; }

    /// <summary>The sites Identify can search (1.32.0: "Search on: MangaUpdates | Grand Comics Database"), in display order.</summary>
    public IReadOnlyList<IdentifySiteDto> Sites { get; init; } = [];

    /// <summary>True when the node's local signs route it to the comics site (why <see cref="Provider"/> is <c>gcd</c>).</summary>
    public bool ComicsSignalled { get; init; }

    /// <summary>True when the switches (config, global + consent, library) allow web lookups.</summary>
    public required bool FetchAvailable { get; init; }

    /// <summary>Why lookups are unavailable (<c>metadata_network_disabled</c>, <c>metadata_disabled</c>, <c>library_metadata_disabled</c>).</summary>
    public string? UnavailableCode { get; init; }
    public string? UnavailableMessage { get; init; }

    /// <summary>Search suggestions: cleaned name, a bracketed English title, the ComicInfo series (never sent until the admin submits one).</summary>
    public IReadOnlyList<string> Suggestions { get; init; } = [];

    /// <summary>A provider record referenced by ComicInfo <c>Web</c> in this node's items, when there is one.</summary>
    public IdentifyReferenceDto? ComicInfoHint { get; init; }

    public required int BudgetUsedToday { get; init; }
    public required int DailyBudget { get; init; }
    public DateTimeOffset? BackoffUntil { get; init; }

    /// <summary>The node's OWN link row, if any (for "Change match").</summary>
    public NodeSeriesLinkDto? CurrentLink { get; init; }

    public required IdentifyLocalDto Local { get; init; }

    /// <summary>
    /// True when the node is in (or is) a folder whose Content is "Doujinshi &amp; adult one-shots"
    /// (the nearest folder with a Content row wins); the dialog then starts with "Hide doujinshi
    /// &amp; novels" unticked, the same rule automatic matching follows.
    /// </summary>
    public bool DoujinshiContent { get; init; }
}

/// <summary>A site Identify can search (1.32.0).</summary>
public sealed record IdentifySiteDto
{
    /// <summary><c>mangaupdates</c> or <c>gcd</c>.</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>False when the site is off the allowlist (or another switch refuses it); the dialog shows why.</summary>
    public required bool Available { get; init; }
    public string? UnavailableCode { get; init; }

    /// <summary>A short note on the site's pace, e.g. the Grand Comics Database's "about 25 requests an hour".</summary>
    public string? Note { get; init; }
}

/// <summary>A parsed provider reference.</summary>
public sealed record IdentifyReferenceDto
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
}

/// <summary>What MangaPixer knows locally about the node (the preview's "Your folder" column).</summary>
public sealed record IdentifyLocalDto
{
    public required string DisplayName { get; init; }

    /// <summary>Archives in the node (itself, children and grandchildren).</summary>
    public required int ItemCount { get; init; }

    /// <summary>The most common ComicInfo <c>Series</c> of those archives.</summary>
    public string? ComicInfoSeries { get; init; }

    /// <summary>Tall strips (webtoon-shaped pages) - true / false, null when too few pages are measured.</summary>
    public bool? TallStrips { get; init; }

    /// <summary>A year found in the name, e.g. <c>(1989)</c>.</summary>
    public int? YearHint { get; init; }

    /// <summary>The local cover (1.26.x): the archive's own, or the folder's first archive, as browse shows it.</summary>
    public string? CoverUrl { get; init; }
}

/// <summary>
/// A confirmed search. <see cref="ToString"/> never prints the query: framework
/// trace logging of action arguments must not be able to leak it (privacy rule:
/// logs never carry search text).
/// </summary>
public sealed record IdentifySearchRequest
{
    public required string Query { get; init; }
    public int Page { get; init; } = 1;

    /// <summary>
    /// Leave doujinshi, novels, artbooks and drama CDs out of the results (the
    /// identify dialog's "Hide doujinshi &amp; novels", on by default there). Sent to
    /// the provider as a fixed type filter; carries no user data.
    /// </summary>
    public bool HideDoujinshiAndNovels { get; init; }

    /// <summary>The site to search (1.32.0): <c>mangaupdates</c> (default) or <c>gcd</c>.</summary>
    public string? Provider { get; init; }

    /// <summary>
    /// The Grand Comics Database only (1.32.0): narrow to series that began in this year. Accepted only when it is the
    /// <c>(YYYY)</c> of the node's own name (the consent text: "a start year when the name has one"); otherwise ignored.
    /// </summary>
    public int? StartYear { get; init; }

    public override string ToString() =>
        $"IdentifySearchRequest {{ Query = [redacted], Page = {Page}, HideDoujinshiAndNovels = {HideDoujinshiAndNovels}, Provider = {Provider}, StartYear = {StartYear} }}";
}

/// <summary>A pasted reference; like the query, never printed by <see cref="ToString"/> (URLs carry title slugs).</summary>
public sealed record IdentifyLookupRequest
{
    /// <summary>A pasted provider URL or shortcode (<c>mu:12345</c>, <c>mu:njeqwry</c>).</summary>
    public required string Reference { get; init; }

    public override string ToString() => "IdentifyLookupRequest { Reference = [redacted] }";
}

public sealed record IdentifyPreviewRequest
{
    public required string Provider { get; init; }
    public required string ExternalId { get; init; }
}

public sealed record IdentifySearchResultDto
{
    public required string Provider { get; init; }
    public required int Page { get; init; }
    public required int TotalHits { get; init; }
    public required IReadOnlyList<IdentifyCandidateDto> Candidates { get; init; }
    public required int BudgetUsedToday { get; init; }
    public required int DailyBudget { get; init; }
}

/// <summary>One search candidate, ranked locally (display only - stage 1 never auto-applies).</summary>
public sealed record IdentifyCandidateDto
{
    public required string ExternalId { get; init; }
    public required string Title { get; init; }

    /// <summary>The title variant the provider matched on, when it differs.</summary>
    public string? HitTitle { get; init; }
    public string? ProviderType { get; init; }
    public MetadataOrigin? Origin { get; init; }
    public MetadataFormat? Format { get; init; }
    public int? Year { get; init; }

    /// <summary>0-1 similarity to the folder name / query.</summary>
    public required double Score { get; init; }
    public required MatchStrength Strength { get; init; }

    /// <summary>Short-lived token for <c>GET .../candidates/{token}/image</c>; null when the hit has no image.</summary>
    public string? ImageToken { get; init; }

    /// <summary>The edition's country and language (1.32.0, Grand Comics Database: one series per edition and translation).</summary>
    public string? Country { get; init; }
    public string? Language { get; init; }

    /// <summary>How many issues or books the edition lists (printings and variants counted once), and which (<c>issues</c> / <c>books</c>).</summary>
    public int? UnitCount { get; init; }
    public string? UnitKind { get; init; }
}

public sealed record IdentifyWarningDto
{
    /// <summary><c>format_novel</c>, <c>format_artbook</c>, <c>year_mismatch</c>, <c>count_mismatch</c>, <c>record_gone</c>.</summary>
    public required string Code { get; init; }
    public required string Message { get; init; }
}

/// <summary>A provider record side by side with the local node (identify step 3).</summary>
public sealed record IdentifyPreviewDto
{
    public required string Provider { get; init; }
    public required string ProviderName { get; init; }
    public required string ExternalId { get; init; }
    public required string Title { get; init; }
    public IReadOnlyList<string> AltTitles { get; init; } = [];
    public string? Description { get; init; }
    public string? ProviderType { get; init; }
    public MetadataOrigin? Origin { get; init; }
    public MetadataFormat? Format { get; init; }
    public bool? Webtoon { get; init; }
    public int? StartYear { get; init; }
    public MetadataOriginStatus? OriginStatus { get; init; }
    public int? OriginVolumes { get; init; }
    public double? LatestChapter { get; init; }
    public IReadOnlyList<SeriesCreatorDto> Creators { get; init; } = [];
    public IReadOnlyList<string> Genres { get; init; } = [];

    /// <summary>The provider's public page (attribution; a user navigation, never a call by MangaPixer).</summary>
    public string? SiteUrl { get; init; }
    public string? ImageToken { get; init; }
    public required double Score { get; init; }
    public required MatchStrength Strength { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public required IdentifyLocalDto Local { get; init; }
    public IReadOnlyList<IdentifyWarningDto> Warnings { get; init; } = [];

    /// <summary>The edition's country / language and publishers (1.32.0, Grand Comics Database records).</summary>
    public string? Country { get; init; }
    public string? Language { get; init; }
    public IReadOnlyList<string> Publishers { get; init; } = [];

    /// <summary>The licence credit the data needs, e.g. "Data: Grand Comics Database, CC BY-SA 4.0" (1.32.0); null for MangaUpdates.</summary>
    public string? Credit { get; init; }
}

/// <summary>Result of Refresh: the record state after one GET.</summary>
public sealed record MetadataRefreshResultDto
{
    /// <summary><c>Ok</c>, or <c>Gone</c> when the provider no longer has the record (the stored data is kept).</summary>
    public required string State { get; init; }
    public required DateTimeOffset FetchedAt { get; init; }
    public required bool ImageUpdated { get; init; }
}
