namespace com.lifepixer.mangapixer.Core.Api;

using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Metadata.Facets;

// 1.45.0 lane Q: the Info panel's tags and the tag view (D1 "Information Panel and Faceted Tags" 2.5 / 2.6). Every number here is
// computed for the VIEWER: only libraries the viewer may see, minus their Private libraries while Incognito is on, minus libraries
// with "Show series information" off (owner Q9). A value with no visible entry is never named and never counted. No path anywhere.

/// <summary>What the Info panel's tags describe.</summary>
public enum InfoPanelMode
{
    /// <summary>Nothing to show (no indexed work here, or "Show series information" is off).</summary>
    None = 0,

    /// <summary>One work: the node is (or lies inside) a location of a work; its own values.</summary>
    Work = 1,

    /// <summary>A container (a library root, an artist's folder, "One Shots"): the values of the works below.</summary>
    Container = 2,
}

/// <summary>The tags of a node (<c>GET /api/v1/nodes/{nodeId}/info</c>) or of a library root (<c>GET /api/v1/libraries/{libraryId}/info</c>).</summary>
public sealed record InfoPanelDto
{
    /// <summary>The node's public id (or the library's, for a library root).</summary>
    public required string NodeId { get; init; }

    public required InfoPanelMode Mode { get; init; }

    /// <summary>One entry per bucket with at least one chip, in the owner's bucket order (creators first).</summary>
    public required IReadOnlyList<FacetBucketDto> Buckets { get; init; }

    /// <summary>Container: how many works below carry at least one tag. Null otherwise.</summary>
    public int? WorksBelow { get; init; }

    /// <summary>Work: the work's title and its locations the viewer may see. Null otherwise.</summary>
    public FacetWorkSummaryDto? Work { get; init; }
}

/// <summary>One bucket of chips (label + wrapping chips in the panel).</summary>
public sealed record FacetBucketDto
{
    public required FacetBucket Bucket { get; init; }

    /// <summary>Single work: every value. Container: the top 12 by how many works below carry them (owner Q10).</summary>
    public required IReadOnlyList<FacetChipDto> Chips { get; init; }

    /// <summary>How many further values this bucket has beyond <see cref="Chips"/> (container only; 0 otherwise).</summary>
    public int More { get; init; }
}

/// <summary>A clickable tag value. Clicking opens the search with <c>f=</c> <see cref="Filter"/>.</summary>
public sealed record FacetChipDto
{
    /// <summary>The value's public id (<c>fv_...</c>).</summary>
    public required string Id { get; init; }

    public required string Display { get; init; }

    /// <summary>
    /// How many works the viewer may see carry this value - set only when it is 2 or more (count-of-1 rule: the chip stays quiet but
    /// clickable). Null otherwise.
    /// </summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Count { get; init; }

    /// <summary>Container only: how many works below this node carry the value ("12 here"). Null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Here { get; init; }

    /// <summary>Creators: the roles here (both = "Story &amp; art"). <see cref="FacetRoles.None"/> for other buckets.</summary>
    public FacetRoles Roles { get; init; }

    /// <summary>A declared value that disagrees with the record (shown amber with the Conflict wording).</summary>
    public bool Conflict { get; init; }

    /// <summary>The search filter for this chip: the value of one <c>f</c> query parameter (<c>creator:fv_...</c>).</summary>
    public required string Filter { get; init; }
}

/// <summary>A work as the Info panel shows it in Work mode.</summary>
public sealed record FacetWorkSummaryDto
{
    public required string Title { get; init; }

    /// <summary>How many locations of the work the viewer may see ("2 locations" for a split series).</summary>
    public required int LocationCount { get; init; }

    /// <summary>The visible locations (at most 10), as display-name breadcrumbs.</summary>
    public required IReadOnlyList<FacetLocationDto> Locations { get; init; }
}

/// <summary>A place where a work lives, as display names only (never a path).</summary>
public sealed record FacetLocationDto
{
    /// <summary>The location node (folder or archive) - what the card or crumb opens.</summary>
    public required string NodeId { get; init; }

    public required string LibraryId { get; init; }

    public required string LibraryName { get; init; }

    /// <summary>From the top-level folder of the library down to the location node itself (inclusive).</summary>
    public required IReadOnlyList<BreadcrumbEntry> Trail { get; init; }
}

/// <summary>A work card of the tag view (search with a filter).</summary>
public sealed record FacetWorkDto
{
    public required string Title { get; init; }

    /// <summary>The first visible location as a normal card (cover, read state, favourite) - what the card opens.</summary>
    public required CatalogNodeDto Node { get; init; }

    /// <summary>The card's cover (= <see cref="CatalogNodeDto.CoverUrl"/> of <see cref="Node"/>).</summary>
    public string? CoverUrl { get; init; }

    /// <summary>How many locations of the work the viewer may see.</summary>
    public required int LocationCount { get; init; }

    /// <summary>The visible locations (at most 5), first = <see cref="Node"/>.</summary>
    public required IReadOnlyList<FacetLocationDto> Locations { get; init; }
}

/// <summary>Where a search filter came from.</summary>
public enum SearchFilterSource
{
    /// <summary>An <c>f=&lt;bucket&gt;:&lt;value id&gt;</c> query parameter (a chip).</summary>
    Chip = 0,

    /// <summary>Typed field syntax in <c>q</c> (<c>creator:"..."</c>, <c>genre:</c>, ...).</summary>
    Typed = 1,
}

/// <summary>
/// A filter of the tag view as the server understood it (for the removable chips in the search field). A filter that matches no value
/// the viewer may see - unknown, or present only where the viewer cannot look - is <see cref="Matched"/> false with no display and no
/// ids: the two cases answer alike.
/// </summary>
public sealed record SearchFilterDto
{
    public required FacetBucket Bucket { get; init; }

    public required SearchFilterSource Source { get; init; }

    /// <summary>Chip: the value id as sent. Typed: null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    /// <summary>Typed: the text as typed (unquoted). Chip: null.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    /// <summary>The matched value's name (visible values only); null when not matched.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Display { get; init; }

    public required bool Matched { get; init; }

    /// <summary>The value ids this filter matched (any of them, AND with the other filters).</summary>
    public required IReadOnlyList<string> ValueIds { get; init; }

    /// <summary>Chip: the value id that was sent when it belonged to a creator merged into another (the redirect); null otherwise.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RedirectedFrom { get; init; }
}
