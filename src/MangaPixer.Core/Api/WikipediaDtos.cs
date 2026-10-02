namespace com.lifepixer.mangapixer.Core.Api;

using com.lifepixer.mangapixer.Core.Metadata;

// The Wikipedia companion of a linked series (1.32.0): where its volume list was completed from, and each volume's English release
// date and ISBN. No DTO carries chapter titles or summaries - the server stores only numbers, dates, ISBNs, page titles and revisions.

/// <summary>Where a volume list was read from: a small source line with a link ("Volume list: MangaDex, completed from Wikipedia").</summary>
public sealed record ListCreditDto
{
    /// <summary>The site credited (<c>Wikipedia</c>).</summary>
    public required string Name { get; init; }

    /// <summary>The page the list was read from (an <c>https://en.wikipedia.org/wiki/...</c> address).</summary>
    public required string Url { get; init; }

    /// <summary>The page title.</summary>
    public required string Title { get; init; }
}

/// <summary>A Wikipedia page a stored list was read from, with the revision that was read.</summary>
public sealed record WikipediaPageDto
{
    public required string Title { get; init; }
    public required string Url { get; init; }
    public long Revision { get; init; }
}

/// <summary>One volume's English release data from the Wikipedia list.</summary>
public sealed record WikipediaVolumeDto
{
    public required string Volume { get; init; }

    /// <summary>The earliest English release date as the page states it: <c>2026-12-08</c>, <c>2002-02</c> or <c>2002</c>. May be in the future (announced).</summary>
    public string? EnglishDate { get; init; }

    /// <summary>The first valid English ISBN, digits only.</summary>
    public string? EnglishIsbn { get; init; }
}

/// <summary>The Wikipedia companion of a series (<c>GET /nodes/{nodeId}/wikipedia</c>, admin).</summary>
public sealed record WikipediaListDto
{
    public required WikipediaListState State { get; init; }
    public required WikipediaListMethod Method { get; init; }

    /// <summary>A sanitized code for why the last list was refused or the last attempt failed (<c>no_page</c>, <c>no_list</c>, <c>disagrees_with_mangadex</c> ...).</summary>
    public string? Code { get; init; }

    /// <summary>The page an admin chose, or null.</summary>
    public string? AdminTitle { get; init; }

    public IReadOnlyList<WikipediaPageDto> Pages { get; init; } = [];

    /// <summary>Volumes of the stored list that have chapters.</summary>
    public int Volumes { get; init; }

    public IReadOnlyList<WikipediaVolumeDto> Details { get; init; } = [];
    public DateTimeOffset? CheckedAt { get; init; }
    public DateTimeOffset? NextCheckAt { get; init; }
}

/// <summary>Chooses the Wikipedia page of a series (<c>PUT /nodes/{nodeId}/wikipedia</c>, admin): a page title or an en.wikipedia.org address.</summary>
public sealed record WikipediaPageRequest
{
    public required string Page { get; init; }
}
