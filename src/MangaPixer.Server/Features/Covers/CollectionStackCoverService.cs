namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>The stored poster of a collected volume's record, as a stack of stories shows it: its versioned URL.</summary>
public sealed record CollectionPoster(long RecordId, int ImageVersion, string Version, string Url);

/// <summary>The head of a stack of stories collected in one volume: the record's title and, when shown, its stored poster.</summary>
public sealed record CollectionStackHead(string Key, long RecordId, string? Title, CollectionPoster? Poster);

/// <summary>
/// The title and cover of stacks of stories collected in one volume (1.37.0, "tankoubon stacks"). The cover is the record's STORED
/// poster (the image MangaPixer saved when the record was fetched) under the cover layer's own rules for a web image: "Volume covers from
/// the web" on and the folder's cover preference (nearest folder, else the library's "Show saved web covers") showing web covers.
/// Otherwise no poster - the caller falls back to the first story's cover. Stored data only, never a request; the image is served to
/// anyone who can see the folder by <c>GET /nodes/{folderId}/collection-stacks/{key}/cover?v=</c>.
/// </summary>
public sealed class CollectionStackCoverService(MangaPixerDbContext db)
{
    /// <summary>The heads of the given stacks of one folder by key (key -> record id); a record that no longer exists is omitted.</summary>
    public async Task<IReadOnlyDictionary<string, CollectionStackHead>> ResolveAsync(long folderId, string folderPublicId, long libraryId,
        IReadOnlyDictionary<string, long> recordByKey, CancellationToken ct)
    {
        var result = new Dictionary<string, CollectionStackHead>(StringComparer.Ordinal);
        if (recordByKey.Count == 0)
            return result;

        var ids = recordByKey.Values.Distinct().ToList();
        var records = await db.MetadataRecords.AsNoTracking().Where(r => ids.Contains(r.Id))
            .Select(r => new { r.Id, r.Title, r.ImageState, r.ImageVersion })
            .ToDictionaryAsync(r => r.Id, ct);
        var webShown = await WebShownAsync(folderId, libraryId, ct);
        foreach (var (key, recordId) in recordByKey)
        {
            if (!records.TryGetValue(recordId, out var record))
                continue;
            CollectionPoster? poster = null;
            if (webShown && record.ImageState == 1)
            {
                var version = CoverResolutionService.Token("cs", record.Id, record.ImageVersion);
                poster = new CollectionPoster(record.Id, record.ImageVersion, version, UrlFor(folderPublicId, key, version));
            }
            result[key] = new CollectionStackHead(key, recordId, string.IsNullOrWhiteSpace(record.Title) ? null : record.Title, poster);
        }
        return result;
    }

    /// <summary>The cover layer's gate for a web image on this folder (1.32.0): the global switch, then the folder / library preference.</summary>
    private async Task<bool> WebShownAsync(long folderId, long libraryId, CancellationToken ct)
    {
        var enabled = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => (bool?)s.MetadataVolumeCoversEnabled).FirstOrDefaultAsync(ct);
        if (enabled == false)
            return false;
        var libraryHidden = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.WebCoversHidden).FirstOrDefaultAsync(ct);
        return FolderCoverRules.WebShown((await FolderCoverPreferences.OfAsync(db, folderId, ct))?.Preference, libraryHidden);
    }

    /// <summary>The stack cover URL (served through the folder's library access).</summary>
    public static string UrlFor(string folderPublicId, string key, string version) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/nodes/{folderPublicId}/collection-stacks/{Uri.EscapeDataString(key)}/cover?v={version}");
}
