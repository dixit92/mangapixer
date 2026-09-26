namespace com.lifepixer.mangapixer.Server.Operations;

/// <summary>
/// Effective backup settings for the admin UI (<c>GET/PUT /api/v1/operations/backups/settings</c>).
/// Every field carries its source (<c>default</c> | <c>settings</c> | <c>configuration</c>);
/// configuration-managed fields are read-only in the UI. The default location
/// is never emitted as a path: only <see cref="CustomLocation"/> (the value an
/// admin or operator typed) is returned, and only by these admin endpoints.
/// </summary>
public sealed record BackupSettingsDto
{
    public required bool Enabled { get; init; }
    public required string EnabledSource { get; init; }
    public required double IntervalHours { get; init; }
    public required string IntervalHoursSource { get; init; }
    public required int RetentionCount { get; init; }
    public required string RetentionCountSource { get; init; }

    /// <summary><c>default</c> (inside the data folder) or <c>custom</c>.</summary>
    public required string LocationKind { get; init; }
    public required string LocationSource { get; init; }
    public required string? CustomLocation { get; init; }

    /// <summary>False when the location is pinned by configuration or <c>AllowLocationChange=false</c>.</summary>
    public required bool LocationChangeAllowed { get; init; }

    /// <summary><c>ok</c> | <c>unavailable</c> | <c>invalid</c> | <c>unknown</c>.</summary>
    public required string LocationStatus { get; init; }

    /// <summary><c>linux</c> | <c>windows</c> | null; drives the UI's path idiom.</summary>
    public required string? Platform { get; init; }

    /// <summary>
    /// Rotating snapshots in the current location (0 when it cannot be read):
    /// what "Move existing snapshots" would move on a location change (1.23.0).
    /// </summary>
    public int RotatingSnapshotCount { get; init; }

    /// <summary>Total size in bytes of <see cref="RotatingSnapshotCount"/> snapshots.</summary>
    public long RotatingSnapshotBytes { get; init; }
}

/// <summary>New backup location: <c>mode</c> = <c>default</c> | <c>custom</c>.</summary>
public sealed record BackupLocationUpdate
{
    public string? Mode { get; init; }
    public string? CustomLocation { get; init; }
}

/// <summary>
/// Partial update of the backup settings. A null field is left unchanged.
/// <see cref="CurrentPassword"/> is REQUIRED whenever <see cref="Location"/> is
/// present (including <see cref="ValidateOnly"/>); a wrong password counts
/// against the login rate limiter.
/// </summary>
public sealed record UpdateBackupSettingsRequest
{
    public bool? Enabled { get; init; }
    public double? IntervalHours { get; init; }
    public int? RetentionCount { get; init; }
    public BackupLocationUpdate? Location { get; init; }
    public string? CurrentPassword { get; init; }

    /// <summary>Run every check (including the write probe) and change nothing.</summary>
    public bool ValidateOnly { get; init; }

    /// <summary>Take over a folder whose marker belongs to another instance (e.g. after a reinstall).</summary>
    public bool AdoptExistingMarker { get; init; }

    /// <summary>
    /// On a location change, move the rotating snapshots of the previous
    /// location to the new one in the background (1.23.0). False (the API
    /// default) leaves them where they are, unmanaged.
    /// </summary>
    public bool MoveExistingSnapshots { get; init; }

    public override string ToString() =>
        $"UpdateBackupSettingsRequest {{ Enabled = {Enabled}, IntervalHours = {IntervalHours}, RetentionCount = {RetentionCount}, Location = {Location}, "
        + $"CurrentPassword = {(CurrentPassword is null ? "null" : "[redacted]")}, ValidateOnly = {ValidateOnly}, AdoptExistingMarker = {AdoptExistingMarker}, MoveExistingSnapshots = {MoveExistingSnapshots} }}";
}

/// <summary>Result of a backup settings PUT.</summary>
public sealed record BackupSettingsUpdateResultDto
{
    public required BackupSettingsDto Settings { get; init; }
    public required bool ValidateOnly { get; init; }

    /// <summary>The custom folder did not exist and is (or would be) created.</summary>
    public required bool WillCreate { get; init; }

    /// <summary>
    /// The location changed. Unless <see cref="SnapshotMoveStarted"/>, the
    /// snapshots in the previous location stay there, unmanaged.
    /// </summary>
    public required bool LocationChanged { get; init; }

    /// <summary>A background move of the existing snapshots started (poll <c>GET backups/move</c>).</summary>
    public bool SnapshotMoveStarted { get; init; }

    /// <summary>Advisory codes, e.g. <c>low_free_space</c>.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}

/// <summary>
/// Progress and result of the background move of existing rotating snapshots
/// (<c>GET /api/v1/operations/backups/move</c>, 1.23.0). In memory only:
/// <c>idle</c> after a restart. Generated file names only, never a folder.
/// </summary>
public sealed record BackupSnapshotMoveStatusDto
{
    /// <summary><c>idle</c> | <c>running</c> | <c>completed</c> | <c>cancelled</c>.</summary>
    public required string State { get; init; }

    /// <summary><c>default</c> | <c>custom</c> | null (never the folder itself).</summary>
    public required string? FromKind { get; init; }
    public required string? ToKind { get; init; }
    public required int TotalFiles { get; init; }
    public required int FilesDone { get; init; }
    public required long TotalBytes { get; init; }
    public required long BytesDone { get; init; }

    /// <summary>Snapshots now in the new location (moved, or an identical copy was already there).</summary>
    public required int MovedCount { get; init; }

    /// <summary>Snapshots deleted by the retention applied in the new location after the move.</summary>
    public required int PrunedCount { get; init; }
    public required DateTimeOffset? StartedUtc { get; init; }
    public required DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>Snapshots that were not moved (or not cleanly), and where they are now.</summary>
    public required IReadOnlyList<BackupSnapshotMoveIssueDto> Issues { get; init; }
}

/// <summary>One snapshot the move could not finish cleanly.</summary>
public sealed record BackupSnapshotMoveIssueDto
{
    public required string FileName { get; init; }

    /// <summary><c>name_conflict</c> | <c>verify_failed</c> | <c>copy_failed</c> | <c>source_delete_failed</c> | <c>cancelled</c>.</summary>
    public required string Code { get; init; }

    /// <summary><c>previous</c> (still in the previous location) | <c>both</c> (in both locations).</summary>
    public required string Location { get; init; }
}
