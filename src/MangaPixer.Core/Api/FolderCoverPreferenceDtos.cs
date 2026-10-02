namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>Sets a folder's own cover preference (<c>PUT /admin/folders/{nodeId}/cover-preference</c>); DELETE clears it (inherit).</summary>
public sealed record SetFolderCoverPreferenceRequest
{
    public required FolderCoverPreference Preference { get; init; }
}

/// <summary>
/// A folder's cover preference: its own row, the effective value, and what "Inherit" would give it. The effective value is what the
/// works below the folder get (the folder's own, else <see cref="Inherited"/>).
/// </summary>
public sealed record FolderCoverPreferenceDto
{
    public required string NodeId { get; init; }

    /// <summary>The folder's own preference, or null when it inherits.</summary>
    public FolderCoverPreference? Preference { get; init; }

    public required FolderCoverPreference Effective { get; init; }

    /// <summary>What the folder gets when it inherits: the nearest ancestor's value, else the library's "Show saved web covers" switch.</summary>
    public required FolderCoverPreference Inherited { get; init; }

    /// <summary>The ancestor folder the inherited value comes from; null = the library's switch.</summary>
    public string? InheritedSourceNodeId { get; init; }

    /// <summary>The display name of that ancestor (for "Inherit (File covers from X)"); null when it comes from the library.</summary>
    public string? InheritedSourceName { get; init; }
}
