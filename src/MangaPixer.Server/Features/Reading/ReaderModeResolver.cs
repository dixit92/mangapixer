namespace com.lifepixer.mangaplex.Server.Features.Reading;

using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Resolves the effective default reader mode for an item (1.2.0). Combines the
/// per-user item override with the global folder/library defaults set by admins.
///
/// Precedence (first hit wins):
///   1. per-user item override (<c>ItemReaderOverrides.ReaderMode</c>)
///   2. nearest ancestor folder default (<c>folder_reader_defaults</c>)
///   3. library default (<c>libraries.DefaultReaderMode</c>)
///   4. user's personal default (<c>ReaderPreferences.DefaultReaderMode</c>)
///   5. hardcoded <see cref="ReaderMode.PagedLtr"/>
/// </summary>
public sealed class ReaderModeResolver
{
    private readonly MangaPlexDbContext _db;

    public ReaderModeResolver(MangaPlexDbContext db) => _db = db;

    public async Task<ReaderMode> ResolveAsync(long userId, CatalogNodeEntity itemNode, CancellationToken ct = default)
    {
        // 1. Per-user item override.
        var itemOverride = await _db.ItemReaderOverrides
            .Where(o => o.UserId == userId && o.ItemId == itemNode.Id)
            .Select(o => o.ReaderMode)
            .FirstOrDefaultAsync(ct);
        if (itemOverride.HasValue)
            return (ReaderMode)itemOverride.Value;

        // 2. Nearest ancestor folder default. Walk parents nearest-first (bounded).
        var ancestorIds = new List<long>();
        var parentId = itemNode.ParentId;
        var guard = 0;
        while (parentId.HasValue && guard++ < 64)
        {
            ancestorIds.Add(parentId.Value);
            parentId = await _db.CatalogNodes
                .Where(n => n.Id == parentId.Value)
                .Select(n => n.ParentId)
                .FirstOrDefaultAsync(ct);
        }
        if (ancestorIds.Count > 0)
        {
            var folderDefaults = await _db.FolderReaderDefaults
                .Where(f => ancestorIds.Contains(f.NodeId))
                .Select(f => new { f.NodeId, f.ReaderMode })
                .ToListAsync(ct);
            // ancestorIds is nearest-first; the closest ancestor with an override wins.
            foreach (var id in ancestorIds)
            {
                var match = folderDefaults.FirstOrDefault(f => f.NodeId == id);
                if (match is not null)
                    return (ReaderMode)match.ReaderMode;
            }
        }

        // 3. Library default.
        var libDefault = await _db.Libraries
            .Where(l => l.Id == itemNode.LibraryId)
            .Select(l => l.DefaultReaderMode)
            .FirstOrDefaultAsync(ct);
        if (libDefault.HasValue)
            return (ReaderMode)libDefault.Value;

        // 4. User's personal default.
        var userDefault = await _db.ReaderPreferences
            .Where(p => p.UserId == userId)
            .Select(p => (int?)p.DefaultReaderMode)
            .FirstOrDefaultAsync(ct);
        if (userDefault.HasValue)
            return (ReaderMode)userDefault.Value;

        // 5. Fallback.
        return ReaderMode.PagedLtr;
    }
}
