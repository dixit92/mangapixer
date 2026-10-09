namespace com.lifepixer.mangapixer.Server.Features.Metadata.Reach;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Features.Covers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The Completion tab (1.32.0; the Official releases tab of 1.30.0), admin-only: every FOLDER with its own confirmed or automatic link,
/// evaluated with the shared progress engine (<see cref="SeriesProgressLoader"/>) and listed by its one answer
/// (<see cref="SeriesAnswer"/>: has the series ended, and does the folder hold all of it), with the upgrades (volumes released officially
/// in the preferred language that the folder holds only as chapters) as a separate switch. Stored rows only; it never contacts a
/// provider. Logs counts and timings, never names or titles.
/// </summary>
public sealed class OfficialReleasesService(MangaPixerDbContext db, ILogger<OfficialReleasesService> logger, ICoverResolver? covers = null)
{
    public const int MaxLimit = 200;

    private readonly ICoverResolver _covers = covers ?? new FileCoverResolver(db);

    private sealed record LinkedRow(long NodeId, string PublicId, string DisplayName, long LibraryId, int State, long RecordId, string Title);

    /// <summary>
    /// A page of the tab. Error <c>library_not_found</c> for an unknown library filter. <paramref name="basis"/> (owner, 1.30.0 RC)
    /// keeps only finished / complete series of that basis (the edition). 1.32.0: <paramref name="answer"/> keeps one answer (it wins
    /// over <paramref name="filter"/>); <paramref name="upgrades"/> keeps only series with an upgrade.
    /// </summary>
    public async Task<(string? Error, OfficialReleasesPageDto? Page)> ListAsync(
        string? libraryPublicId, OfficialReleasesFilter filter, string? cursor, int limit, CancellationToken ct = default,
        CompletionBasis? basis = null, SeriesAnswer? answer = null, bool upgrades = false)
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
        var all = rows.Select(r => (Row: r, Entry: progress[r.NodeId])).ToList();
        // 1.39.0: a folder whose tracking an admin turned off gives no answer - left out of the tab and its counts (counted apart).
        var evaluated = all.Where(x => !x.Entry.Result.Facts.TrackingOff).ToList();

        // By answer (1.32.0) for an answer filter and All; the 1.30.0 filters keep their order.
        var byAnswer = answer is not null || filter == OfficialReleasesFilter.All;
        var ordered = (byAnswer
                ? evaluated
                    .OrderBy(x => AnswerRank(x.Entry.Result.Answer))
                    .ThenBy(x => MissingKey(x.Entry.Result))
                    .ThenBy(x => x.Entry.Result.Answer == SeriesAnswer.CantTell ? (int)x.Entry.Result.AnswerReason : 0)
                : evaluated
                    .OrderBy(x => Rank(x.Entry))
                    .ThenByDescending(x => x.Entry.Result.UpgradeVolumes.Count)
                    .ThenByDescending(x => (x.Entry.Result.CompletionTarget ?? 0) - (x.Entry.Result.CompletionHeld ?? 0)))
            .ThenBy(x => x.Row.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Row.NodeId)
            .ToList();
        var counted = upgrades ? ordered.Where(x => IsUpgrade(x.Entry)).ToList() : ordered;
        int CountOf(SeriesAnswer a) => counted.Count(x => x.Entry.Result.Answer == a);
        var summary = new OfficialReleasesSummaryDto
        {
            Series = ordered.Count,
            Upgrades = ordered.Count(x => IsUpgrade(x.Entry)),
            FinishedNotHeld = ordered.Count(x => x.Entry.Result.Completion == SeriesCompletion.FinishedNotHeld),
            CompleteCollections = ordered.Count(x => x.Entry.Result.Completion == SeriesCompletion.CompleteCollection),
            HaveItAll = CountOf(SeriesAnswer.HaveItAll),
            FinishedMissing = CountOf(SeriesAnswer.FinishedMissing),
            UpToDate = CountOf(SeriesAnswer.UpToDate),
            MissingSome = CountOf(SeriesAnswer.MissingSome),
            CantTell = CountOf(SeriesAnswer.CantTell),
            NotTracked = all.Count - evaluated.Count,
        };
        var filtered = counted.Where(x => (answer is { } a ? x.Entry.Result.Answer == a : Matches(x.Entry, filter))
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
        logger.LogDebug("Completion: {Series} linked series, {HaveItAll} have it all, {Upgrades} with upgrades, page {Count} in {ElapsedMs} ms",
            summary.Series, summary.HaveItAll, summary.Upgrades, items.Count, sw.ElapsedMilliseconds);
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

    // The answer order of the Completion tab: the two Finished answers first (what can move), then what is missing, then the rest.
    private static int AnswerRank(SeriesAnswer answer) => answer switch
    {
        SeriesAnswer.HaveItAll => 0,
        SeriesAnswer.FinishedMissing => 1,
        SeriesAnswer.MissingSome => 2,
        SeriesAnswer.UpToDate => 3,
        _ => 4,
    };

    // Within the missing answers, the closest to whole first: the finished edition's shortfall, else the missing volumes + chapters.
    private static int MissingKey(ProgressResult r) => r.Answer switch
    {
        SeriesAnswer.FinishedMissing when r.CompletionTarget is { } target => Math.Max(0, target - (r.CompletionHeld ?? 0)),
        SeriesAnswer.FinishedMissing or SeriesAnswer.MissingSome => r.MissingVolumes.Count + r.MissingChapterCount,
        _ => 0,
    };

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
