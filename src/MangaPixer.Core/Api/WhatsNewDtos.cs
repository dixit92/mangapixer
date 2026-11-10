namespace com.lifepixer.mangapixer.Core.Api;

// "What's new" (1.53.0), admin-only: the release notes of the running build, read from the CHANGELOG.md embedded in the server at
// build time. Text only - no HTML ever passes through; inline formatting arrives as flagged parts and a link only ever points at an app
// path (starting with a single "/").

/// <summary>
/// <c>GET /api/v1/admin/whats-new</c>: the running version and the release-note sections the admin has not seen yet (newer than the
/// version they last saw, not newer than the running one), newest first. Empty when there is nothing new or the notes cannot be read.
/// </summary>
public sealed record WhatsNewDto
{
    /// <summary>The running version, <c>major.minor.patch</c> (build metadata and a pre-release label left out).</summary>
    public required string Current { get; init; }

    /// <summary>The sections to show, newest first.</summary>
    public required IReadOnlyList<WhatsNewSectionDto> Sections { get; init; }
}

/// <summary>One released version's notes (a <c>## [x.y.z] - date</c> section of the CHANGELOG).</summary>
public sealed record WhatsNewSectionDto
{
    /// <summary>The version as written in the heading, e.g. <c>1.53.0</c>.</summary>
    public required string Version { get; init; }

    /// <summary>The release date as written (<c>yyyy-MM-dd</c>), or null when the heading has none.</summary>
    public string? Date { get; init; }

    /// <summary>The groups in file order (<c>Added</c>, <c>Changed</c>, <c>Fixed</c>, ...).</summary>
    public required IReadOnlyList<WhatsNewGroupDto> Groups { get; init; }
}

/// <summary>One <c>### Name</c> group and its bullets.</summary>
public sealed record WhatsNewGroupDto
{
    public required string Name { get; init; }

    public required IReadOnlyList<WhatsNewItemDto> Items { get; init; }
}

/// <summary>One bullet (its wrapped lines joined), as a run of inline parts.</summary>
public sealed record WhatsNewItemDto
{
    public required IReadOnlyList<WhatsNewInlineDto> Parts { get; init; }
}

/// <summary>
/// A run of text with the small inline subset the notes keep: <c>**bold**</c>, <c>`code`</c> and a Markdown link whose target is an app
/// path (<see cref="Link"/>; any other link arrives as its text only).
/// </summary>
public sealed record WhatsNewInlineDto
{
    public required string Text { get; init; }

    public bool Bold { get; init; }

    public bool Code { get; init; }

    /// <summary>An in-app path starting with a single <c>/</c> (e.g. <c>/admin</c>), or null.</summary>
    public string? Link { get; init; }
}
