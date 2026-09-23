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
}

/// <summary>Result of a backup settings PUT.</summary>
public sealed record BackupSettingsUpdateResultDto
{
    public required BackupSettingsDto Settings { get; init; }
    public required bool ValidateOnly { get; init; }

    /// <summary>The custom folder did not exist and is (or would be) created.</summary>
    public required bool WillCreate { get; init; }

    /// <summary>The location changed: snapshots in the previous location stay there, unmanaged.</summary>
    public required bool LocationChanged { get; init; }

    /// <summary>Advisory codes, e.g. <c>low_free_space</c>.</summary>
    public required IReadOnlyList<string> Warnings { get; init; }
}
