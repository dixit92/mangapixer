namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

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

    /// <summary>
    /// 1.29.0 RC: the series status line is shown - the folder has its own link to a series record. The fields below are set only
    /// then.
    /// </summary>
    public bool HasSeriesStatus { get; init; }

    /// <summary>The record's publication status (Ongoing, Complete, Hiatus, Cancelled), or null when it states none.</summary>
    public MetadataOriginStatus? SeriesStatus { get; init; }

    /// <summary>Whole volumes missing (the placeholders): gaps below the highest volume here, and volumes released in the preferred language after it.</summary>
    public int MissingVolumes { get; init; }

    /// <summary>Chapters missing: gaps below the highest chapter here, and chapters released in the preferred language after it.</summary>
    public int MissingChapters { get; init; }

    /// <summary>What is released in the preferred language is known (a volume total or a released chapter list): "up to date" can be said.</summary>
    public bool ReleaseKnown { get; init; }

    /// <summary>The preferred language the missing counts follow ("en", "fr", ...).</summary>
    public string? Language { get; init; }
}
