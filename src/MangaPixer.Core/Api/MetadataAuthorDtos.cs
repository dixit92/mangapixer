namespace com.lifepixer.mangapixer.Core.Api;

/// <summary>
/// 1.38.0 "Artists' other names": the state of the admin-run look-up of MangaUpdates author records (the author ids stored series
/// records list for their creators). Counts and ids only - never a name.
/// </summary>
public sealed record AuthorAliasStatusDto
{
    /// <summary>Known authors: distinct MangaUpdates author ids on stored records linked in a library whose "Fetch from the web" is on.</summary>
    public int Eligible { get; init; }

    /// <summary>Of <see cref="Eligible"/>: ids with a stored answer (found or not found) that is not older than <see cref="RefreshAfterDays"/>.</summary>
    public int Fetched { get; init; }

    /// <summary>Of <see cref="Eligible"/>: ids a look-up would request now (never fetched, failed last time, or older than <see cref="RefreshAfterDays"/>).</summary>
    public int ToFetch { get; init; }

    /// <summary>Stored author records with other names (all libraries).</summary>
    public int WithOtherNames { get; init; }

    /// <summary>A stored answer is asked again only when it is older than this many days.</summary>
    public int RefreshAfterDays { get; init; }

    /// <summary>Seconds between two requests of a look-up (the pacing).</summary>
    public int SecondsPerRequest { get; init; }

    /// <summary>
    /// Why a look-up cannot start now, as the gateway's code (<c>metadata_disabled</c>, <c>metadata_network_disabled</c>,
    /// <c>provider_not_allowed</c>, <c>library_metadata_disabled</c> when no library has Fetch on, <c>budget_exhausted</c>,
    /// <c>provider_backoff</c>), or null.
    /// </summary>
    public string? BlockedReason { get; init; }

    /// <summary>The running look-up, or null when none runs.</summary>
    public AuthorAliasRunDto? Running { get; init; }

    /// <summary>The last finished look-up since the server started, or null.</summary>
    public AuthorAliasRunDto? LastRun { get; init; }
}

/// <summary>One look-up run of artists' other names (1.38.0).</summary>
public sealed record AuthorAliasRunDto
{
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }

    /// <summary>Author ids the run set out to request.</summary>
    public int Total { get; init; }

    /// <summary>Requests sent so far.</summary>
    public int Requests { get; init; }

    /// <summary>Author records stored (found).</summary>
    public int Stored { get; init; }

    /// <summary>Ids MangaUpdates does not know (stored as not found).</summary>
    public int NotFound { get; init; }

    /// <summary>Requests whose answer could not be used (asked again next time).</summary>
    public int Failed { get; init; }

    /// <summary>
    /// <c>running</c>, <c>completed</c> (every id requested), <c>cancelled</c>, <c>budget_exhausted</c>, <c>provider_backoff</c>,
    /// <c>switched_off</c> (a switch, the consent or the allowlist stopped it) or <c>failed</c>.
    /// </summary>
    public string Outcome { get; init; } = "running";

    /// <summary>When MangaUpdates allows the next request after a backoff stop, else null.</summary>
    public DateTimeOffset? RetryAt { get; init; }
}
