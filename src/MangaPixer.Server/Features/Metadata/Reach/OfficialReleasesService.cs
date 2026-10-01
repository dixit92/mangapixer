namespace com.lifepixer.mangapixer.Server.Features.Metadata.Reach;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The Official releases tab (1.30.0), admin-only: every FOLDER with its own confirmed or automatic link, evaluated with the shared
/// progress engine (<see cref="SeriesProgressLoader"/>) - the volumes released officially in the preferred language that the folder
/// holds only as chapters, finished series it does not hold whole, and complete collections. Stored rows only; it never contacts
/// a provider. Logs counts and timings, never names or titles.
/// </summary>
public sealed class OfficialReleasesService(MangaPixerDbContext db, ILogger<OfficialReleasesService> logger, ICoverResolver? covers = null)
{
    public const int MaxLimit = 200;

    private readonly ICoverResolver _covers = covers ?? new FileCoverResolver(db);

    private sealed record LinkedRow(long NodeId, string PublicId, string DisplayName, long LibraryId, int State, long RecordId, string Title);

    /// <summary>
    /// A page of the tab. Error <c>library_not_found</c> for an unknown library filter. <paramref name="basis"/> (owner, 1.30.0 RC)
    /// keeps only finished / complete series of that basis - the official edition, the fan translation or the original run.
    /// </summary>
    public async Task<(string? Error, OfficialReleasesPageDto? Page)> ListAsync(
        string? libraryPublicId, OfficialReleasesFilter filter, string? cursor, int limit, CancellationToken ct = default,
        CompletionBasis? basis = null)
    {
        long? libraryId = null;
        if (!string.IsNullOrEmpty(libraryPublicId))
        {
            libraryId = await db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            if (libraryId is null)
                return ("library_not_found", null);
        }
        limit = Math.Clamp(limit, 1, MaxLimit);
        var offset = int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var o) ? o : 0;

        var sw = Stopwatch.StartNew();
        var rows = await LinkedFoldersAsync(libraryId, ct);
        var progress = await new SeriesProgressLoader(db).LoadAsync(rows.Select(r => new SeriesProgressTarget(r.NodeId, r.RecordId)).ToList(), ct);
        var evaluated = rows.Select(r => (Row: r, Entry: progress[r.NodeId])).ToList();

        var ordered = evaluated
            .OrderBy(x => Rank(x.Entry))
            .ThenByDescending(x => x.Entry.Result.UpgradeVolumes.Count)
            .ThenByDescending(x => (x.Entry.Result.CompletionTarget ?? 0) - (x.Entry.Result.CompletionHeld ?? 0))
            .ThenBy(x => x.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.NodeId)
            .ToList();
        var summary = new OfficialReleasesSummaryDto
        {
            Series = ordered.Count,
            Upgrades = ordered.Count(x => IsUpgrade(x.Entry)),
            FinishedNotHeld = ordered.Count(x => x.Entry.Result.Completion == SeriesCompletion.FinishedNotHeld),
            CompleteCollections = ordered.Count(x => x.Entry.Result.Completion == SeriesCompletion.CompleteCollection),
        };
        var filtered = ordered.Where(x => Matches(x.Entry, filter)
            && (basis is null || (x.Entry.Result.Completion != SeriesCompletion.None && x.Entry.Result.CompletionBasis == basis))).ToList();
        var page = filtered.Skip(offset).Take(limit).ToList();

        var libraryIds = page.Select(p => p.Row.LibraryId).Distinct().ToList();
        var libraries = await db.Libraries.AsNoTracking().Where(l => libraryIds.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PublicId, l.DisplayName), ct);
        var coverUrls = page.Count == 0
            ? new Dictionary<long, string>()
            : (IReadOnlyDictionary<long, string>)await _covers.ResolveUrlsAsync(page.Select(p => new CoverTarget(p.Row.NodeId, p.Row.PublicId, true)).ToList(), ct);
        var items = page.Select(p =>
        {
            var library = libraries.GetValueOrDefault(p.Row.LibraryId);
            return new OfficialReleaseRowDto
            {
                NodeId = p.Row.PublicId,
                DisplayName = p.Row.DisplayName,
                LibraryId = library.PublicId ?? string.Empty,
                LibraryName = library.DisplayName ?? string.Empty,
                CoverUrl = coverUrls.GetValueOrDefault(p.Row.NodeId),
                RecordTitle = p.Row.Title,
                LinkState = (SeriesLinkState)p.Row.State,
                Progress = p.Entry.Dto,
            };
        }).ToList();

        var next = offset + page.Count;
        logger.LogDebug("Official releases: {Series} linked series, {Upgrades} with upgrades, {Finished} finished not held, page {Count} in {ElapsedMs} ms",
            summary.Series, summary.Upgrades, summary.FinishedNotHeld, items.Count, sw.ElapsedMilliseconds);
        return (null, new OfficialReleasesPageDto
        {
            Items = items,
            Summary = summary,
            Total = filtered.Count,
            NextCursor = next < filtered.Count ? next.ToString(CultureInfo.InvariantCulture) : null,
            Language = await ReleasedInLanguage.PreferredAsync(db, ct),
        });
    }

    private static bool IsUpgrade(SeriesProgressEntry e) => e.Result.UpgradeVolumes.Count > 0;

    private static int Rank(SeriesProgressEntry e) =>
        IsUpgrade(e) ? 0
            : e.Result.Completion == SeriesCompletion.FinishedNotHeld ? 1
            : e.Result.Completion == SeriesCompletion.CompleteCollection ? 2
            : 3;

    private static bool Matches(SeriesProgressEntry e, OfficialReleasesFilter filter) => filter switch
    {
        OfficialReleasesFilter.Upgrades => IsUpgrade(e),
        OfficialReleasesFilter.Finished => e.Result.Completion == SeriesCompletion.FinishedNotHeld,
        OfficialReleasesFilter.Complete => e.Result.Completion == SeriesCompletion.CompleteCollection,
        OfficialReleasesFilter.All => true,
        _ => IsUpgrade(e) || e.Result.Completion == SeriesCompletion.FinishedNotHeld,
    };

    private async Task<List<LinkedRow>> LinkedFoldersAsync(long? libraryId, CancellationToken ct)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var query =
            from l in db.NodeSeriesLinks.AsNoTracking()
            where (l.State == confirmed || l.State == auto) && l.RecordId != null && (libraryId == null || l.LibraryId == libraryId)
            join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
            where n.Kind == folder && n.Availability != tombstoned
            join r in db.MetadataRecords.AsNoTracking() on l.RecordId equals r.Id
            select new LinkedRow(n.Id, n.PublicId, n.DisplayName, n.LibraryId, l.State, r.Id, r.Title);
        return await query.ToListAsync(ct);
    }
}
