namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

/// <summary>One MangaUpdates author as stored on this server: the record's main name and the author's other names (pen names,
/// other spellings and scripts).</summary>
public sealed record AuthorAliases(string AuthorId, string Name, IReadOnlyList<string> OtherNames);

/// <summary>
/// Artists' other names (1.38.0): the stored MangaUpdates author records, by the <c>author_id</c> a stored series record lists for a
/// creator (<c>MetadataJson.Creator.ProviderId</c>). Read by "Match folders by name" so a folder named with any of an artist's names
/// matches. Stored data only - this never sends a request; the author records are fetched only when an admin asks.
/// </summary>
public interface IAuthorAliasSource
{
    /// <summary>The stored author records among <paramref name="authorIds"/>, keyed by author id; ids without a stored record are absent.</summary>
    Task<IReadOnlyDictionary<string, AuthorAliases>> GetAsync(IReadOnlyCollection<string> authorIds, CancellationToken ct);
}

/// <summary>No stored author records (the seam before the author-record store exists): every lookup is empty.</summary>
public sealed class NoAuthorAliases : IAuthorAliasSource
{
    private static readonly IReadOnlyDictionary<string, AuthorAliases> Empty = new Dictionary<string, AuthorAliases>();

    public Task<IReadOnlyDictionary<string, AuthorAliases>> GetAsync(IReadOnlyCollection<string> authorIds, CancellationToken ct) =>
        Task.FromResult(Empty);
}
