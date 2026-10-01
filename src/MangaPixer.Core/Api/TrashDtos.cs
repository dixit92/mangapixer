namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// The admin trash card (1.31.0, <c>GET /api/v1/admin/trash</c>): the settings, what "Empty trash now" would remove per
/// library (with any hold and its reason), what "Clean bundles" would remove, and the last runs. Ids, names an admin already
/// sees, counts and sizes only - never a path or a title.
/// </summary>
public sealed record TrashOverviewDto
{
    public required TrashSettingsDto Settings { get; init; }

    /// <summary>Tombstones removed before this time are past the window (now minus the retention).</summary>
    public required DateTimeOffset WindowStart { get; init; }

    /// <summary>One row per library that has removed items (tombstones), eligible or still waiting.</summary>
    public required IReadOnlyList<TrashLibraryDto> Libraries { get; init; }

    /// <summary>What "Empty trash now" for all libraries removes: the libraries without a hold.</summary>
    public required TrashCountsDto Total { get; init; }

    /// <summary>What "Clean bundles now" removes.</summary>
    public required TrashFilesDto Bundles { get; init; }

    public TrashRunDto? LastEmpty { get; init; }

    public TrashRunDto? LastBundleClean { get; init; }
}

/// <summary>The trash settings (<c>PUT /api/v1/admin/trash/settings</c> answers with them too).</summary>
public sealed record TrashSettingsDto
{
    /// <summary>"Turn automatic cleaning on": empty the trash and clean bundles once a day. Off by default.</summary>
    public required bool AutomaticCleaning { get; init; }

    /// <summary>The move window, which is also the trash retention, in days (one of <see cref="AllowedRetentionDays"/>).</summary>
    public required int RetentionDays { get; init; }

    /// <summary>Daily, Weekly, Monthly, Quarterly, Yearly as days, shortest first.</summary>
    public required IReadOnlyList<int> AllowedRetentionDays { get; init; }

    /// <summary>The server-local hour of the automatic run (0-23).</summary>
    public required int AutomaticHour { get; init; }
}

/// <summary>Changes the trash settings; a null field keeps its value.</summary>
public sealed record UpdateTrashSettingsRequest
{
    public bool? AutomaticCleaning { get; init; }

    public int? RetentionDays { get; init; }

    /// <summary>The server-local hour (0-23) of the daily automatic run.</summary>
    public int? AutomaticHour { get; init; }
}

/// <summary>One library's trash.</summary>
public sealed record TrashLibraryDto
{
    public required string LibraryId { get; init; }

    public required string Name { get; init; }

    /// <summary>What emptying this library's trash removes now (also when it is held: what releasing the hold would remove).</summary>
    public required TrashCountsDto Eligible { get; init; }

    /// <summary>Removed items still inside the window, or kept by move recognition: they stay.</summary>
    public required int Waiting { get; init; }

    /// <summary>All nodes of the library, present and removed.</summary>
    public required int LibraryNodes { get; init; }

    /// <summary><c>scan_running</c>, <c>root_unavailable</c> or <c>burst</c>; null when the library is not held.</summary>
    public string? Hold { get; init; }

    /// <summary>Whether the admin can release the hold by emptying this library's trash (not for a running scan).</summary>
    public required bool HoldReleasable { get; init; }
}

/// <summary>What a purge removes: nodes, the per-user rows that go with them, and their files in the data root.</summary>
public sealed record TrashCountsDto
{
    public required int Nodes { get; init; }

    public required int Archives { get; init; }

    public required int Folders { get; init; }

    /// <summary>Reading progress, read marks, bookmarks, reader overrides and favorites of these nodes, all users.</summary>
    public required int UserStateRows { get; init; }

    public required int Files { get; init; }

    public required long Bytes { get; init; }
}

/// <summary>A number of files and their size.</summary>
public sealed record TrashFilesDto
{
    public required int Files { get; init; }

    public required long Bytes { get; init; }
}

/// <summary>The last run of "Empty trash" or "Clean bundles".</summary>
public sealed record TrashRunDto
{
    public required DateTimeOffset At { get; init; }

    /// <summary>True for the daily automatic run, false for an admin's "now".</summary>
    public required bool Automatic { get; init; }

    /// <summary>Nodes removed (Empty trash) or files removed (Clean bundles).</summary>
    public required int Count { get; init; }

    /// <summary>Bytes of files removed.</summary>
    public required long Bytes { get; init; }

    /// <summary>Libraries skipped because of a hold (Empty trash).</summary>
    public required int HeldLibraries { get; init; }
}

/// <summary>Empties the trash: every library without a hold, or one library (<see cref="ReleaseHold"/> releases its hold).</summary>
public sealed record EmptyTrashRequest
{
    /// <summary>One library's id; null for every library.</summary>
    public string? LibraryId { get; init; }

    /// <summary>For one library: empty it even though it is held for a burst or an unreachable root (after the admin confirmed).</summary>
    public bool ReleaseHold { get; init; }
}

/// <summary>What "Empty trash now" removed.</summary>
public sealed record EmptyTrashResultDto
{
    public required TrashCountsDto Removed { get; init; }

    /// <summary>Libraries that kept their trash, with the hold.</summary>
    public required IReadOnlyList<TrashHeldLibraryDto> Held { get; init; }
}

public sealed record TrashHeldLibraryDto
{
    public required string LibraryId { get; init; }

    public required string Hold { get; init; }
}
