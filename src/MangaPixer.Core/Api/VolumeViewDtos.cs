namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// Whether a folder has a Volumes view and whether it is on for the viewer (<c>GET /nodes/{nodeId}/volume-view</c>, 1.29.0).
/// The series header shows the Volumes | Folders switch only when <see cref="Available"/>; the switch itself is the per-user
/// <c>SeriesViewMode</c> preference. Reads stored data only - never a request to a provider.
/// </summary>
public sealed record VolumeViewDto
{
    public required string NodeId { get; init; }

    /// <summary>Stacks (or merged unit subfolders) exist here, so the Volumes view differs from the folder list.</summary>
    public required bool Available { get; init; }

    /// <summary>The viewer sees the Volumes view now (the user's switch, else the folder / library / global default).</summary>
    public required bool Active { get; init; }

    /// <summary>The list merges this series' generic <c>Volumes</c> / <c>Chapters</c> subfolders into one volume-ordered list.</summary>
    public required bool Consolidated { get; init; }

    /// <summary>Number of virtual volume stacks in the view.</summary>
    public required int StackCount { get; init; }
}
