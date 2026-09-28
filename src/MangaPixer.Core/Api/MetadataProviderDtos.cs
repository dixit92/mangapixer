namespace com.lifepixer.mangapixer.Core.Api;

// The provider allowlist (1.28.0): the approved sites MangaPixer may contact for
// series metadata, shown as chips in Metadata Manager > Settings. Each can be removed
// and added back; a removed site gets no request of any kind.

/// <summary>One approved metadata site.</summary>
public sealed record MetadataProviderDto
{
    /// <summary>Stable slug (<c>mangaupdates</c>, <c>anilist</c>).</summary>
    public required string Id { get; init; }
    public required string Name { get; init; }

    /// <summary>Every host MangaPixer contacts for this site.</summary>
    public required IReadOnlyList<string> Hosts { get; init; }

    /// <summary>One line: what the site is used for.</summary>
    public required string UsedFor { get; init; }

    /// <summary>One line: what is sent to it.</summary>
    public required string Sends { get; init; }

    /// <summary>False when an admin removed it from the allowlist.</summary>
    public required bool Allowed { get; init; }
}
