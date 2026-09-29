namespace com.lifepixer.mangapixer.Server.Features.Covers;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>A virtual volume stack's web cover: the stored cover of its volume and the stack's versioned cover URL.</summary>
public sealed record StackCover(string Key, long VolumeCoverId, string VolumeCoverPublicId, int StoredVersion, string Version, string Url);

/// <summary>
/// Web volume covers on virtual volume STACKS (1.29.0, design 7.4 / P2.2): a chapter-only stack ("Vol. 3 - 10 chapters", no
/// real volume archive) shows the stored web cover of its volume - preferred language, else the origin language - instead
/// of its first chapter's page 1. A stack with a real volume archive keeps that archive's resolved cover (not handled here).
/// The same read-time rules as the cover layer: shown only while "Volume covers from the web" is on and the library's
/// "Show saved web covers" is on, never under Don't match or without a linked series. Stored data only - no request.
/// The image is served to anyone who can see the folder by <c>GET /nodes/{folderId}/volumes/{key}/cover?v=</c>.
/// </summary>
public sealed class StackCoverService(MangaPixerDbContext db)
{
    /// <summary>
    /// The web covers of the given stacks of one folder, by stack key; stacks without one (a real volume archive, no stored
    /// cover, a fractional volume, web covers hidden, not linked) are omitted.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, StackCover>> ResolveAsync(long folderId, string folderPublicId, long libraryId,
        IReadOnlyCollection<VolumeStack> stacks, CancellationToken ct)
    {
        var result = new Dictionary<string, StackCover>(StringComparer.Ordinal);
        var wanted = stacks.Where(s => !s.HasVolumeArchive && s.Volume >= 0 && decimal.Truncate(s.Volume) == s.Volume)
            .GroupBy(s => s.Key, StringComparer.Ordinal).Select(g => g.First()).ToList();
        if (wanted.Count == 0)
            return result;

        var settings = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataVolumeCoversEnabled, s.MetadataCoverLanguage }).FirstOrDefaultAsync(ct);
        if (settings is { MetadataVolumeCoversEnabled: false })
            return result;
        if (await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.WebCoversHidden).FirstOrDefaultAsync(ct))
            return result;

        var links = await CoverLinks.NearestAsync(db, [folderId], ct);
        if (!links.TryGetValue(folderId, out var link) || !link.IsLinked)
            return result;
        var companionId = await CoverSeries.CompanionRecordIdAsync(db, link.RecordId!.Value, ct);
        if (companionId is null)
            return result;

        var origins = await db.MetadataRecords.AsNoTracking().Where(r => r.Id == companionId || r.Id == link.RecordId)
            .Select(r => new { r.Id, r.Origin }).ToListAsync(ct);
        var origin = origins.FirstOrDefault(o => o.Id == companionId)?.Origin ?? origins.FirstOrDefault(o => o.Id == link.RecordId)?.Origin;
        var originLocales = CoverRules.OriginLocales((MetadataOrigin?)origin);
        var preferred = string.IsNullOrWhiteSpace(settings?.MetadataCoverLanguage) ? "en" : settings.MetadataCoverLanguage;

        var volumes = wanted.Select(s => (int)s.Volume).Distinct().ToList();
        var covers = await db.VolumeCovers.AsNoTracking()
            .Where(v => v.ProviderRecordId == companionId && v.Kind == (int)VolumeCoverKind.Volume && v.Variant == 0
                && v.State == (int)VolumeCoverState.Stored && v.StoredVersion > 0 && v.Volume != null && volumes.Contains(v.Volume.Value))
            .Select(v => new { v.Id, v.PublicId, v.Volume, v.Locale, v.StoredVersion })
            .ToListAsync(ct);

        foreach (var stack in wanted)
        {
            var volume = (int)stack.Volume;
            var stored = covers.Where(c => c.Volume == volume).OrderBy(c => c.Id)
                .Select(c => (c.Locale, new WebCoverCandidate(AutoCoverSource.WebVolume, c.Id, null))).ToList();
            if (CoverRules.PickLanguage(stored, preferred, originLocales) is not { VolumeCoverId: { } coverId })
                continue;
            var cover = covers.First(c => c.Id == coverId);
            var version = CoverResolutionService.Token("s", cover.Id, cover.StoredVersion);
            result[stack.Key] = new StackCover(stack.Key, cover.Id, cover.PublicId, cover.StoredVersion, version,
                UrlFor(folderPublicId, stack.Key, version));
        }
        return result;
    }

    /// <summary>The stack cover URL (served through the folder's library access).</summary>
    public static string UrlFor(string folderPublicId, string key, string version) =>
        string.Create(CultureInfo.InvariantCulture, $"/api/v1/nodes/{folderPublicId}/volumes/{Uri.EscapeDataString(key)}/cover?v={version}");
}
