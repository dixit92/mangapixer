namespace com.lifepixer.mangapixer.Server.Features.Reading;

using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The database half of <see cref="TallStripRule"/> (1.41.0): the sample of a set of archives' measured pages, and its verdict. Used by
/// Identify's local facts, automatic matching and (per folder, as one aggregate) the reading-mode suggestions.
/// </summary>
public static class TallStripSampler
{
    /// <summary>
    /// Tall strips over the first <see cref="TallStripRule.SamplePages"/> measured pages of <paramref name="archiveIds"/> (archive id,
    /// then page order); null when fewer than <see cref="TallStripRule.MinMeasuredPages"/> are measured.
    /// </summary>
    public static async Task<bool?> TallStripsAsync(MangaPixerDbContext db, IReadOnlyCollection<long> archiveIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(archiveIds);
        if (archiveIds.Count == 0)
            return null;
        var dims = await db.PageEntries.AsNoTracking()
            .Where(p => archiveIds.Contains(p.ItemId) && p.Width != null && p.Height != null && p.Width > 0)
            .OrderBy(p => p.ItemId).ThenBy(p => p.Ordinal)
            .Select(p => new { Width = p.Width!.Value, Height = p.Height!.Value })
            .Take(TallStripRule.SamplePages)
            .ToListAsync(ct);
        return TallStripRule.Classify(dims.Select(d => (d.Width, d.Height)).ToList());
    }
}
