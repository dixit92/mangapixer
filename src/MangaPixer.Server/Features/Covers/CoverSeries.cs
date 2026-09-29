namespace com.lifepixer.mangapixer.Server.Features.Covers;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>What the cover layer reads about a linked series: its MangaDex companion record and that record's stored covers.</summary>
internal static class CoverSeries
{
    /// <summary>The companion provider whose covers the layer uses.</summary>
    public const string CompanionProvider = "mangadex";

    /// <summary>The MangaDex record of a linked series record (Auto or Confirmed companion), or null.</summary>
    public static Task<long?> CompanionRecordIdAsync(MangaPixerDbContext db, long seriesRecordId, CancellationToken ct) =>
        db.MetadataCompanions.AsNoTracking()
            .Where(c => c.RecordId == seriesRecordId && c.Provider == CompanionProvider && c.CompanionRecordId != null
                && (c.State == (int)CompanionState.Auto || c.State == (int)CompanionState.Confirmed))
            .Select(c => c.CompanionRecordId)
            .FirstOrDefaultAsync(ct);
}
