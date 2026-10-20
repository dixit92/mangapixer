namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using Microsoft.EntityFrameworkCore;

// 1.45.1 (B4): the gold tracker rings of the cards on screen, without the listing. After Back from the reader the browse page used to
// re-ask every page that built its list, twice (at once and 3 s later) - two full listings with every enrichment per return. This answers
// only `trackerRead` for the given card ids through the listing's own TrackerCardMarks (same rule, same service).
public sealed partial class CatalogBrowseService
{
    /// <summary>The most card ids one request may ask for (the client splits a longer list).</summary>
    public const int MaxTrackerMarkIds = 200;

    /// <summary>
    /// The rings of <paramref name="ids"/> (cards of the folder <paramref name="parentPublicId"/>, or of the library root): node ids of the
    /// library and the folder's volume-stack ids; anything else is left out. Null when the library or the folder is unknown, or the viewer
    /// cannot open the library (no new data: the viewer's own tracker numbers for nodes the listing would show them).
    /// </summary>
    public async Task<TrackerCardMarksDto?> TrackerCardMarksAsync(long userId, string libraryPublicId, string? parentPublicId,
        IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        var library = await _db.Libraries.AsNoTracking().Where(l => l.PublicId == libraryPublicId).Select(l => new { l.Id, l.PublicId })
            .FirstOrDefaultAsync(ct);
        if (library is null || !await _auth.CanAccessLibraryAsync(userId, library.Id, ct))
            return null;
        long? folderId = null;
        if (parentPublicId is not null)
        {
            var folder = await _db.CatalogNodes.AsNoTracking()
                .Where(n => n.PublicId == parentPublicId && n.LibraryId == library.Id && n.Kind == (int)CatalogNodeKind.Folder
                    && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
                .Select(n => (long?)n.Id)
                .FirstOrDefaultAsync(ct);
            if (folder is null)
                return null;
            folderId = folder;
        }

        var wanted = ids.Where(id => !string.IsNullOrEmpty(id)).Distinct(StringComparer.Ordinal).ToList();
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == library.Id && wanted.Contains(n.PublicId) && n.Availability != (int)CatalogNodeAvailability.Tombstoned)
            .Select(n => new { n.PublicId, n.Kind, n.DisplayName, n.Availability })
            .ToListAsync(ct);
        var cards = nodes.Select(n => new CatalogNodeDto
        {
            Id = n.PublicId,
            ParentId = parentPublicId ?? string.Empty,
            LibraryId = library.PublicId,
            Kind = (CatalogNodeKind)n.Kind,
            DisplayName = n.DisplayName,
            Availability = (CatalogNodeAvailability)n.Availability,
        }).ToList();
        // A volume stack of this folder's Volumes view: its key is enough - the marks read the stack from the folder's own entries.
        foreach (var id in wanted)
        {
            if (parentPublicId is null || !VolumeStackId.TryDecode(id, out var stackFolder, out var key)
                || !string.Equals(stackFolder, parentPublicId, StringComparison.Ordinal))
                continue;
            cards.Add(new CatalogNodeDto
            {
                Id = id,
                ParentId = parentPublicId,
                LibraryId = library.PublicId,
                Kind = CatalogNodeKind.VolumeStack,
                DisplayName = string.Empty,
                Availability = CatalogNodeAvailability.Available,
                VolumeStack = new VolumeStackSummaryDto
                {
                    Key = key,
                    Label = string.Empty,
                    PresentCount = 0,
                    MissingCount = 0,
                    ExtraCount = 0,
                    HasVolumeArchive = false,
                    Confidence = default,
                },
            });
        }

        var marked = await WithTrackerReadAsync(cards, userId, folderId, ct);
        return new TrackerCardMarksDto { Items = marked.Select(c => new TrackerCardMarkDto { Id = c.Id, TrackerRead = c.TrackerRead }).ToList() };
    }
}
