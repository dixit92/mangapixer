namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>Sets a folder's own cover preference (<c>PUT /admin/folders/{nodeId}/cover-preference</c>); DELETE clears it (inherit).</summary>
public sealed record SetFolderCoverPreferenceRequest
{
    public required FolderCoverPreference Preference { get; init; }
}

/// <summary>
/// A folder's cover preference: its own row, the effective value with where it comes from. The effective value is what the works
/// below the folder get (the folder's own, else the nearest ancestor's, else the library's "Show web covers" switch).
/// </summary>
public sealed record FolderCoverPreferenceDto
{
    public required string NodeId { get; init; }

    /// <summary>The folder's own preference, or null when it inherits.</summary>
    public FolderCoverPreference? Preference { get; init; }

    public required FolderCoverPreference Effective { get; init; }

    /// <summary>The folder the effective value comes from (the folder itself when it has its own row); null = the library's switch.</summary>
    public string? SourceNodeId { get; init; }

    /// <summary>The display name of that folder (for "Inherit (File covers from X)"); null when it comes from the library.</summary>
    public string? SourceName { get; init; }
}
