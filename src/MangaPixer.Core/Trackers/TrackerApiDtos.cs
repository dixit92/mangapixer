namespace com.lifepixer.mangapixer.Core.Trackers;

// 1.43.0 tracker sync API (lane E). Cookie + CSRF only - an API token reaches none of these routes. No DTO carries a token, a
// password, a client secret, an OAuth code after use, a path or another user's data; requests that carry one print it [redacted].

/// <summary><c>GET /api/v1/trackers</c>: the trackers this server allows and the caller's own connections.</summary>
public sealed record TrackersOverviewDto
{
    public required IReadOnlyList<TrackerSummaryDto> Trackers { get; init; }
}

/// <summary>One tracker as a user sees it.</summary>
public sealed record TrackerSummaryDto
{
    /// <summary><c>mal</c> or <c>mangaupdates</c>.</summary>
    public required string Slug { get; init; }

    public required string Name { get; init; }

    /// <summary>An admin allowed it under the current admin consent, and the config switch does not disable tracker sync.</summary>
    public required bool Allowed { get; init; }

    /// <summary><c>Trackers:NetworkDisabled=true</c>: nothing is sent, whatever the settings.</summary>
    public required bool NetworkDisabledByConfig { get; init; }

    /// <summary>How the user connects: a sign-in on the tracker's site, or a username and password used once.</summary>
    public required TrackerConnectKind ConnectKind { get; init; }

    /// <summary>The user consent text version the client must show and send back to connect.</summary>
    public required int CurrentConsentVersion { get; init; }

    /// <summary>Whether the tracker can keep a new entry private ("Private entries" has an effect).</summary>
    public required bool SupportsPrivateEntries { get; init; }

    /// <summary>The caller's account on it, or null when not connected.</summary>
    public TrackerAccountDto? Account { get; init; }
}

/// <summary>The caller's connected account on one tracker - "connected / expires", never a token.</summary>
public sealed record TrackerAccountDto
{
    public required string Tracker { get; init; }
    public required TrackerAccountState State { get; init; }

    /// <summary>"Connected as ...", or null after an admin disconnected the account.</summary>
    public string? DisplayName { get; init; }

    public required DateTimeOffset ConnectedAt { get; init; }

    /// <summary>When the stored sign-in expires (MangaPixer renews a MyAnimeList sign-in before then), or null when unknown.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    public DateTimeOffset? LastSuccessAt { get; init; }
    public DateTimeOffset? LastFailureAt { get; init; }

    /// <summary>A sanitized outcome code, e.g. <c>needs_reconnect</c>, <c>rate_limited</c>, <c>rejected</c>, <c>unavailable</c>.</summary>
    public string? LastFailureCode { get; init; }

    /// <summary>The user accepted an older consent version: syncing stops until the current text is accepted (by connecting again).</summary>
    public required bool ConsentRenewalNeeded { get; init; }

    public required bool AddStartedSeries { get; init; }
    public required bool PrivateEntries { get; init; }

    /// <summary>Requests made today (UTC) for this account, and the daily cap.</summary>
    public required int RequestsToday { get; init; }

    public required int DailyRequestCap { get; init; }
}

/// <summary><c>POST /api/v1/trackers/{slug}/connect/start</c>: begin a sign-in on the tracker's site.</summary>
public sealed record TrackerConnectStartRequest
{
    /// <summary>The user consent version the user just accepted; must be the current one.</summary>
    public int? AcceptedConsentVersion { get; init; }
}

/// <summary>The tracker's sign-in page to open in the browser (carries the one-use <c>state</c> and the PKCE challenge).</summary>
public sealed record TrackerConnectStartResponse
{
    public required string AuthorizeUrl { get; init; }
}

/// <summary>
/// <c>POST /api/v1/trackers/{slug}/connect</c>: the callback page posts what the tracker handed back. The <c>state</c> must be one this
/// server issued to the same user AND session, within ten minutes, once.
/// </summary>
public sealed record TrackerConnectFinishRequest
{
    public string? Code { get; init; }
    public string? State { get; init; }

    public override string ToString() => "TrackerConnectFinishRequest { [redacted] }";
}

/// <summary>
/// <c>POST /api/v1/trackers/{slug}/connect/password</c> (MangaUpdates): the username and password are passed to the tracker ONCE to get
/// a sign-in token; neither is stored or logged.
/// </summary>
public sealed record TrackerConnectPasswordRequest
{
    public string? Username { get; init; }
    public string? Password { get; init; }
    public int? AcceptedConsentVersion { get; init; }

    public override string ToString() => $"TrackerConnectPasswordRequest {{ Username = [redacted], Password = [redacted], AcceptedConsentVersion = {AcceptedConsentVersion} }}";
}

/// <summary><c>PUT /api/v1/trackers/{slug}/settings</c>: the caller's settings for one connected tracker (null = unchanged).</summary>
public sealed record UpdateTrackerUserSettingsRequest
{
    public bool? AddStartedSeries { get; init; }
    public bool? PrivateEntries { get; init; }
}

/// <summary>One linked series in the caller's tracker list: MangaPixer's numbers next to the tracker's, and the outcome.</summary>
public sealed record TrackerEntryDto
{
    /// <summary>The linked series record's public id (the key of the per-series routes).</summary>
    public required string SeriesId { get; init; }

    public required string Title { get; init; }

    /// <summary>A folder linked to the series that the caller can open, or null.</summary>
    public string? NodeId { get; init; }

    public required string Tracker { get; init; }

    /// <summary>The tracker's id of the series, or null when none is known.</summary>
    public string? RemoteId { get; init; }

    public TrackerIdSource? RemoteIdSource { get; init; }

    public required TrackerEntryState State { get; init; }
    public string? ErrorCode { get; init; }
    public required bool DontSync { get; init; }

    /// <summary>What MangaPixer would send automatically (marks read in the reader or by hand).</summary>
    public int? LocalChapters { get; init; }
    public int? LocalVolumes { get; init; }

    /// <summary>The jump that waits for "Send" (it includes folder mark-reads and imports).</summary>
    public int? HeldChapters { get; init; }
    public int? HeldVolumes { get; init; }

    public int? LastSentChapters { get; init; }
    public int? LastSentVolumes { get; init; }
    public DateTimeOffset? LastSentAt { get; init; }

    /// <summary>The tracker's numbers when last read; shown, never stored as reading state.</summary>
    public int? RemoteChapters { get; init; }
    public int? RemoteVolumes { get; init; }
    public TrackerEntryStatus? RemoteStatus { get; init; }

    /// <summary>The last read found the series on none of the caller's lists.</summary>
    public required bool RemoteMissing { get; init; }

    public DateTimeOffset? RemoteSeenAt { get; init; }
    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary><c>POST /api/v1/trackers/{slug}/entries/{seriesId}/set</c>: "Set MyAnimeList to N" - may LOWER the tracker (the UI asks first).</summary>
public sealed record TrackerSetProgressRequest
{
    public int? Chapters { get; init; }
    public int? Volumes { get; init; }
}

/// <summary><c>PUT /api/v1/trackers/{slug}/entries/{seriesId}/dont-sync</c>.</summary>
public sealed record TrackerDontSyncRequest
{
    public bool DontSync { get; init; }
}

/// <summary>
/// <c>POST /api/v1/trackers/{slug}/preview</c>: the first-connect preview. Reads the tracker's entry of up to a batch of the caller's
/// series not read yet (nothing is written) and answers the whole list; call again while <see cref="Remaining"/> is above zero.
/// </summary>
public sealed record TrackerPreviewDto
{
    public required IReadOnlyList<TrackerEntryDto> Entries { get; init; }

    /// <summary>Series not read from the tracker yet.</summary>
    public required int Remaining { get; init; }

    /// <summary>The caller's daily request cap for this tracker is used up; the rest waits until tomorrow (UTC).</summary>
    public required bool CapReached { get; init; }
}

/// <summary><c>POST /api/v1/trackers/{slug}/entries/send-all</c>: "Send all" - every series where MangaPixer is ahead (held jumps included).</summary>
public sealed record TrackerSendAllResultDto
{
    public required int Sent { get; init; }
    public required int Skipped { get; init; }
    public required int Failed { get; init; }
    public required bool CapReached { get; init; }
}

/// <summary>A user's sync switch on a folder or series (owner Q3): on inside a Private library (or for doujinshi), or off.</summary>
public sealed record TrackerNodeOverrideDto
{
    public required string NodeId { get; init; }
    public required string Name { get; init; }
    public required string LibraryId { get; init; }

    /// <summary>True = sync on for everything below it; false = sync off.</summary>
    public required bool Enabled { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }
}

/// <summary><c>PUT /api/v1/trackers/sync-overrides</c>: set or clear the caller's switch on the selected folders / series.</summary>
public sealed record UpdateTrackerNodeOverridesRequest
{
    public IReadOnlyList<string>? NodeIds { get; init; }

    /// <summary>True = sync on, false = sync off, null = remove the switch (inherit again).</summary>
    public bool? Enabled { get; init; }
}

/// <summary>One tracker as an admin configures it (<c>/api/v1/admin/trackers</c>). The client secret is write-only.</summary>
public sealed record AdminTrackerSettingsDto
{
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public required bool Enabled { get; init; }
    public int? AcceptedConsentVersion { get; init; }
    public required int CurrentConsentVersion { get; init; }
    public DateTimeOffset? ConsentAt { get; init; }

    /// <summary>On under an older admin consent: nothing syncs until an admin accepts the current text.</summary>
    public required bool ConsentRenewalNeeded { get; init; }

    public required bool NetworkDisabledByConfig { get; init; }
    public required TrackerConnectKind ConnectKind { get; init; }

    /// <summary>The tracker needs this server's own app registration (MyAnimeList: Client ID and redirect URL).</summary>
    public required bool NeedsAppRegistration { get; init; }

    public string? ClientId { get; init; }

    /// <summary>A client secret is stored (its value is never returned).</summary>
    public required bool ClientSecretSet { get; init; }

    public string? RedirectUri { get; init; }

    /// <summary>Users with a connected account (any state).</summary>
    public required int ConnectedUsers { get; init; }
}

/// <summary><c>PUT /api/v1/admin/trackers/{slug}</c> (null = unchanged).</summary>
public sealed record UpdateTrackerSettingsRequest
{
    public bool? Enabled { get; init; }

    /// <summary>Required (the current admin consent version) to turn the tracker on.</summary>
    public int? AcceptedConsentVersion { get; init; }

    /// <summary>The Client ID of the app this server registered; empty clears it.</summary>
    public string? ClientId { get; init; }

    /// <summary>The client secret (write-only; empty clears it).</summary>
    public string? ClientSecret { get; init; }

    /// <summary>The exact redirect URL registered with the tracker (absolute http(s)); empty clears it.</summary>
    public string? RedirectUri { get; init; }

    public override string ToString() =>
        $"UpdateTrackerSettingsRequest {{ Enabled = {Enabled}, AcceptedConsentVersion = {AcceptedConsentVersion}, ClientId = {(ClientId is null ? "null" : "set")}, ClientSecret = {(ClientSecret is null ? "null" : "[redacted]")}, RedirectUri = {(RedirectUri is null ? "null" : "set")} }}";
}

/// <summary>A user's connection as an admin sees it: that it exists and its state - never the remote account or a token.</summary>
public sealed record AdminTrackerAccountDto
{
    public required string UserId { get; init; }
    public required string Username { get; init; }
    public required string Tracker { get; init; }
    public required TrackerAccountState State { get; init; }
    public required DateTimeOffset ConnectedAt { get; init; }
    public DateTimeOffset? LastSuccessAt { get; init; }
    public string? LastFailureCode { get; init; }
}

/// <summary>The tracker ids of the series a node belongs to (<c>/api/v1/admin/trackers/nodes/{nodeId}/ids</c>).</summary>
public sealed record TrackerSeriesIdsDto
{
    /// <summary>The linked series record's public id, or null when the node has no Confirmed / Auto link.</summary>
    public string? SeriesId { get; init; }

    public required IReadOnlyList<TrackerSeriesIdDto> Ids { get; init; }
}

/// <summary>One tracker's id for a series: the id in use, where it comes from, and the admin's override when set.</summary>
public sealed record TrackerSeriesIdDto
{
    public required string Tracker { get; init; }
    public string? RemoteId { get; init; }
    public TrackerIdSource? Source { get; init; }

    /// <summary>The admin's id (it wins over the stored one), or null.</summary>
    public string? OverrideId { get; init; }
}

/// <summary><c>PUT /api/v1/admin/trackers/nodes/{nodeId}/ids/{slug}</c>: the admin's tracker id for the series; null or empty removes it.</summary>
public sealed record UpdateTrackerSeriesIdRequest
{
    public string? RemoteId { get; init; }
}
