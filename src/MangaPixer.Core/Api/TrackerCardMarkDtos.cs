namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// 1.45.1 (B4): the gold "read on &lt;tracker&gt;" rings of the cards a browse page shows, re-read after Back from the reader without replaying
/// the listing (<c>GET /api/v1/trackers/card-marks</c>). The same rule as the listing's <see cref="CatalogNodeDto.TrackerRead"/>; only cards of
/// a library the viewer can open are answered; at most <c>200</c> ids per request.
/// </summary>
public sealed record TrackerCardMarksDto
{
    /// <summary>One item per answered card id (unknown ids and nodes the viewer cannot open are left out).</summary>
    public required IReadOnlyList<TrackerCardMarkDto> Items { get; init; }
}

/// <summary>One card's ring: the trackers it is read on (the listing's <see cref="CatalogNodeDto.TrackerRead"/>), or null for no ring.</summary>
public sealed record TrackerCardMarkDto
{
    /// <summary>The card's id as the listing gave it (a node, or a volume stack of the Volumes view).</summary>
    public required string Id { get; init; }

    public IReadOnlyList<string>? TrackerRead { get; init; }
}
