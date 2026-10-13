namespace com.lifepixer.mangapixer.Server.Persistence.Entities;

// 1.43.0 tracker sync (lane E, migration AddTrackerSync). Configured in Persistence/TrackerModel.cs. Every table here except
// tracker_settings and tracker_id_overrides is ONE user's data: it is deleted with the user (AdminController.DeleteUser), never
// exported, never logged. Tokens are stored only as Data Protection payloads (purpose MangaPixer.Trackers.Token.v1).

/// <summary>
/// The admin gate of one tracker (one row per tracker slug): allowed for this server after the admin consent text, and the
/// instance's own app registration (MyAnimeList: Client ID, optional secret, the exact redirect URL). MangaPixer ships none.
/// </summary>
public sealed class TrackerSettingsEntity
{
    public long Id { get; set; }

    /// <summary>A <c>TrackerSlugs</c> value (max 32, unique).</summary>
    public string Tracker { get; set; } = string.Empty;

    /// <summary>Users may connect and sync (only with <see cref="ConsentVersion"/> equal to the current admin consent).</summary>
    public bool Enabled { get; set; }

    /// <summary>The admin consent version accepted when the tracker was allowed, or null.</summary>
    public int? ConsentVersion { get; set; }

    public DateTimeOffset? ConsentAt { get; set; }

    /// <summary>The instance's client id at the tracker (max 256). Not a secret, but never logged.</summary>
    public string? ClientId { get; set; }

    /// <summary>The client secret, Data Protection payload (write-only in the API), or null.</summary>
    public string? ClientSecretProtected { get; set; }

    /// <summary>The exact redirect URL registered with the tracker (max 1024); sent as stored, never derived per request.</summary>
    public string? RedirectUri { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One user's connected account on one tracker. Unique (user, tracker).</summary>
public sealed class TrackerAccountEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>A <c>TrackerSlugs</c> value (max 32).</summary>
    public string Tracker { get; set; } = string.Empty;

    /// <summary>The tracker's own user id (max 128), shown to that user only.</summary>
    public string? RemoteUserId { get; set; }

    /// <summary>"Connected as ..." (max 256), shown to that user only.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Data Protection payload of the access token; null once an admin disconnected the account.</summary>
    public string? AccessTokenProtected { get; set; }

    /// <summary>Data Protection payload of the refresh token (MyAnimeList), or null.</summary>
    public string? RefreshTokenProtected { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary><c>TrackerAccountState</c> int value.</summary>
    public int State { get; set; }

    /// <summary>The user consent version accepted when connecting.</summary>
    public int ConsentVersion { get; set; }

    public DateTimeOffset ConsentAt { get; set; }

    /// <summary>User setting "Add series I start" (default on): a series not on any list is added when the user reads it.</summary>
    public bool AddStartedSeries { get; set; } = true;

    /// <summary>User setting "Private entries" (default on): new entries are added as private where the tracker supports it.</summary>
    public bool PrivateEntries { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? LastFailureAt { get; set; }

    /// <summary>A sanitized outcome code (max 32), never a message.</summary>
    public string? LastFailureCode { get; set; }

    /// <summary>The UTC day <see cref="RequestsUsed"/> counts for (<c>yyyy-MM-dd</c>, max 10).</summary>
    public string? RequestsDay { get; set; }

    /// <summary>Tracker requests made for this account on <see cref="RequestsDay"/> (the per-user daily cap).</summary>
    public int RequestsUsed { get; set; }

    public UserEntity? User { get; set; }
}

/// <summary>
/// One user's sync state of one linked series record on one tracker: the tracker's id for it and where that came from, what was last
/// sent, what the tracker last showed, a held jump (bulk / import marks) and the outcome. Unique (user, tracker, record).
/// </summary>
public sealed class TrackerEntryEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>A <c>TrackerSlugs</c> value (max 32).</summary>
    public string Tracker { get; set; } = string.Empty;

    /// <summary>The linked series record (<c>metadata_records.Id</c>).</summary>
    public long RecordId { get; set; }

    /// <summary>The tracker's id of the series (max 64), or null while none is known.</summary>
    public string? RemoteId { get; set; }

    /// <summary><c>TrackerIdSource</c> int value, or null without an id.</summary>
    public int? RemoteIdSource { get; set; }

    /// <summary><c>TrackerEntryState</c> int value.</summary>
    public int State { get; set; }

    /// <summary>A sanitized error code (max 32) for <c>Error</c>, else null.</summary>
    public string? ErrorCode { get; set; }

    /// <summary>The user's "Don't sync this series" for this tracker.</summary>
    public bool DontSync { get; set; }

    public int? LocalChapters { get; set; }
    public int? LocalVolumes { get; set; }

    /// <summary>A jump that waits for the user's "Send" (it came from a folder mark-read or an import).</summary>
    public int? HeldChapters { get; set; }
    public int? HeldVolumes { get; set; }

    public int? LastSentChapters { get; set; }
    public int? LastSentVolumes { get; set; }

    /// <summary><c>TrackerEntryStatus</c> int value last written, or null.</summary>
    public int? LastSentStatus { get; set; }
    public DateTimeOffset? LastSentAt { get; set; }

    /// <summary>The tracker's numbers when last read (shown next to MangaPixer's; never stored as reading state).</summary>
    public int? RemoteChapters { get; set; }
    public int? RemoteVolumes { get; set; }

    /// <summary><c>TrackerEntryStatus</c> int value last read, or null (also null when the series was on no list).</summary>
    public int? RemoteStatus { get; set; }

    /// <summary>True when the last read found the series on none of the user's lists.</summary>
    public bool RemoteMissing { get; set; }
    public DateTimeOffset? RemoteSeenAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A tracker write to retry (a <c>RateLimited</c> answer, e.g. MangaUpdates' five-second write delay or a 429): retried a few
/// seconds later in process, else on that user's next read event or Sync now. Unique (user, tracker, record).
/// </summary>
public sealed class TrackerRetryEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>A <c>TrackerSlugs</c> value (max 32).</summary>
    public string Tracker { get; set; } = string.Empty;

    public long RecordId { get; set; }

    /// <summary>True when the retried sync is the user's "Send" of a held jump (the full numbers), false for the automatic one.</summary>
    public bool SendHeld { get; set; }

    public DateTimeOffset NotBefore { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A user's sync switch on a folder or series node (owner Q3): <see cref="Enabled"/> true turns sync on for everything below it
/// (e.g. a folder inside a Private library, or doujinshi), false turns it off. The nearest setting wins. Unique (user, node).
/// </summary>
public sealed class TrackerNodeOverrideEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }
    public long NodeId { get; set; }
    public bool Enabled { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>An admin's tracker id for a linked series record (owner Q6: admin only). Unique (record, tracker).</summary>
public sealed class TrackerIdOverrideEntity
{
    public long Id { get; set; }
    public long RecordId { get; set; }

    /// <summary>A <c>TrackerSlugs</c> value (max 32).</summary>
    public string Tracker { get; set; } = string.Empty;

    /// <summary>The tracker's series id (max 64, digits only).</summary>
    public string RemoteId { get; set; } = string.Empty;

    public DateTimeOffset SetAt { get; set; }
}

/// <summary>
/// An OAuth sign-in in progress: the SHA-256 of the <c>state</c> handed to the browser, bound to the user AND the session that
/// started it, the PKCE verifier (Data Protection payload), single use, ten-minute expiry.
/// </summary>
public sealed class TrackerOAuthStateEntity
{
    public long Id { get; set; }
    public long UserId { get; set; }

    /// <summary>A <c>TrackerSlugs</c> value (max 32).</summary>
    public string Tracker { get; set; } = string.Empty;

    /// <summary>SHA-256 of the state, lowercase hex (64, unique).</summary>
    public string StateHash { get; set; } = string.Empty;

    /// <summary>SHA-256 of the session ticket that started the sign-in, lowercase hex (64).</summary>
    public string SessionHash { get; set; } = string.Empty;

    /// <summary>The PKCE code verifier, Data Protection payload.</summary>
    public string VerifierProtected { get; set; } = string.Empty;

    /// <summary>The user consent version accepted when starting.</summary>
    public int ConsentVersion { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}
