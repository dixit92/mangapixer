namespace com.lifepixer.mangapixer.Server.Features.Metadata.Authors;

using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>The stored answer of one author-record request (<c>metadata_authors.Status</c>, 1.38.0).</summary>
public enum MetadataAuthorStatus
{
    /// <summary>The author record was read: name and other names stored.</summary>
    Ok = 0,

    /// <summary>MangaUpdates does not know the id (404). Asked again only after the refresh age.</summary>
    NotFound = 1,

    /// <summary>The answer could not be used (an error or an unreadable body). Asked again by the next look-up.</summary>
    Failed = 2,
}

/// <summary>
/// Artists' other names (1.38.0) - <see cref="IAuthorAliasSource"/> over the stored MangaUpdates author records
/// (<c>metadata_authors</c>, rows with a name: read ok, or read before and failed on a re-check). Stored data only: this never sends a
/// request.
/// </summary>
public sealed class StoredAuthorAliases : IAuthorAliasSource
{
    /// <summary>Ids per query (SQLite parameter limits).</summary>
    private const int Chunk = 500;

    private readonly MangaPixerDbContext _db;

    public StoredAuthorAliases(MangaPixerDbContext db) => _db = db;

    public async Task<IReadOnlyDictionary<string, AuthorAliases>> GetAsync(IReadOnlyCollection<string> authorIds, CancellationToken ct)
    {
        var result = new Dictionary<string, AuthorAliases>(StringComparer.Ordinal);
        var ids = authorIds.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        for (var i = 0; i < ids.Count; i += Chunk)
        {
            var chunk = ids.Skip(i).Take(Chunk).ToList();
            var rows = await _db.MetadataAuthors.AsNoTracking()
                .Where(a => a.Provider == MangaUpdatesProvider.ProviderId && a.Name != null && chunk.Contains(a.ExternalId))
                .Select(a => new { a.ExternalId, a.Name, a.OtherNamesJson })
                .ToListAsync(ct);
            foreach (var row in rows)
            {
                if (string.IsNullOrEmpty(row.Name))
                    continue;
                result[row.ExternalId] = new AuthorAliases(row.ExternalId, row.Name, MetadataJson.ReadList<string>(row.OtherNamesJson));
            }
        }
        return result;
    }
}

/// <summary>Writes and removes the stored author records (1.38.0). Ids, counts only in anything logged by callers.</summary>
public static class MetadataAuthorStore
{
    /// <summary>Stores (inserts or replaces) the answer for one author id.</summary>
    public static async Task SaveAsync(
        MangaPixerDbContext db, string externalId, MetadataAuthorStatus status, ProviderAuthorRecord? record, DateTimeOffset fetchedAt,
        CancellationToken ct)
    {
        var row = await db.MetadataAuthors
            .FirstOrDefaultAsync(a => a.Provider == MangaUpdatesProvider.ProviderId && a.ExternalId == externalId, ct);
        if (row is null)
        {
            row = new MetadataAuthorEntity { Provider = MangaUpdatesProvider.ProviderId, ExternalId = externalId };
            db.MetadataAuthors.Add(row);
        }
        if (status == MetadataAuthorStatus.Failed && row.Status == (int)MetadataAuthorStatus.Ok && row.Name is not null)
        {
            // A failed re-check keeps the names read before; it is asked again by the next look-up.
            row.Status = (int)MetadataAuthorStatus.Failed;
            row.FetchedAt = fetchedAt;
        }
        else
        {
            row.Status = (int)status;
            row.Name = status == MetadataAuthorStatus.Ok ? record?.Name : null;
            row.OtherNamesJson = status == MetadataAuthorStatus.Ok && record is { OtherNames.Count: > 0 }
                ? MetadataJson.WriteList(record.OtherNames)
                : null;
            row.FetchedAt = fetchedAt;
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>"Delete fetched web data" for the whole server: every stored author record. Returns how many went.</summary>
    public static Task<int> DeleteAllAsync(MangaPixerDbContext db, CancellationToken ct) => db.MetadataAuthors.ExecuteDeleteAsync(ct);

    /// <summary>
    /// After a per-library purge: removes the author records no remaining stored MangaUpdates record names for a creator. Returns how
    /// many went.
    /// </summary>
    public static async Task<int> DeleteUnreferencedAsync(MangaPixerDbContext db, CancellationToken ct)
    {
        var named = new HashSet<string>(StringComparer.Ordinal);
        var creatorLists = await db.MetadataRecords.AsNoTracking()
            .Where(r => r.Provider == MangaUpdatesProvider.ProviderId && r.CreatorsJson != null)
            .Select(r => r.CreatorsJson)
            .ToListAsync(ct);
        foreach (var json in creatorLists)
        {
            foreach (var creator in MetadataJson.ReadList<MetadataJson.Creator>(json))
            {
                if (creator.ProviderId is { Length: > 0 } id)
                    named.Add(id);
            }
        }

        var stored = await db.MetadataAuthors.AsNoTracking()
            .Where(a => a.Provider == MangaUpdatesProvider.ProviderId)
            .Select(a => new { a.Id, a.ExternalId })
            .ToListAsync(ct);
        var gone = stored.Where(a => !named.Contains(a.ExternalId)).Select(a => a.Id).ToList();
        var removed = 0;
        for (var i = 0; i < gone.Count; i += 500)
        {
            var chunk = gone.Skip(i).Take(500).ToList();
            removed += await db.MetadataAuthors.Where(a => chunk.Contains(a.Id)).ExecuteDeleteAsync(ct);
        }
        return removed;
    }
}
