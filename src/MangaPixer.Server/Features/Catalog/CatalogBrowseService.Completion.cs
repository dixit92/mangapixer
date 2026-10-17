namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

// 1.44.0 lane C - the browse filter by series completion (owner-approved design, 2026-10-17). A level's linked series folders are
// answered with the shared progress engine (SeriesProgressLoader -> SeriesAnswers), the same answer as the Completion tab, and the
// matching ids are folded into the base query BEFORE counting - like the read-state pre-pass - so TotalCount, HasMore and every
// cursor stay honest. Other folders and loose files drop while the filter applies. The answers of a level are kept in memory for
// 60 s for the following pages (the first page always recomputes). Stored rows only; never contacts a provider.

public sealed partial class CatalogBrowseService
{
    /// <summary>How long a level's answers serve the following pages of the same level (owner, 2026-10-17: 60 s).</summary>
    public static readonly TimeSpan CompletionMemoAge = TimeSpan.FromSeconds(60);

    private sealed record LevelAnswers(IReadOnlyDictionary<long, SeriesAnswer?> Answers, DateTimeOffset At);

    /// <summary>
    /// The level's linked series folders by node id, with their record: child FOLDERS with their own <c>Confirmed</c> or <c>Auto</c> link
    /// to a record (the Completion tab's set), not tombstoned (the level query already drops tombstones).
    /// </summary>
    private async Task<Dictionary<long, long>> LinkedSeriesOfLevelAsync(IQueryable<CatalogNodeEntity> level, CancellationToken ct)
    {
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var folder = (int)CatalogNodeKind.Folder;
        var rows = await (
            from n in level
            where n.Kind == folder
            join l in _db.NodeSeriesLinks.AsNoTracking() on n.Id equals l.NodeId
            where (l.State == confirmed || l.State == auto) && l.RecordId != null
            select new { n.Id, RecordId = l.RecordId!.Value }).ToListAsync(ct);
        return rows.ToDictionary(r => r.Id, r => r.RecordId);
    }

    /// <summary>
    /// Keeps only the linked series of <paramref name="filtered"/> whose answer is <paramref name="wanted"/>, and counts the answers of
    /// the linked series that survive the other filters. A folder whose tracking an admin turned off has no answer. A continuation page
    /// (<paramref name="continuation"/>) reuses the level's answers while they are under <see cref="CompletionMemoAge"/> old and the
    /// level still holds the same linked series; the first page recomputes and refreshes them.
    /// </summary>
    private async Task<(IQueryable<CatalogNodeEntity> Query, BrowseCompletionCountsDto Counts)> ApplyCompletionAsync(
        IQueryable<CatalogNodeEntity> filtered, Dictionary<long, long> linked, long libraryId, long? parentId, SeriesAnswer wanted,
        bool continuation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var key = $"browse-completion:{libraryId}:{parentId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "root"}";
        var now = _time.GetUtcNow();
        var memo = false;
        IReadOnlyDictionary<long, SeriesAnswer?> answers;
        if (continuation && _cache?.Get<LevelAnswers>(key) is { } cached && now - cached.At < CompletionMemoAge
            && cached.Answers.Count == linked.Count && linked.Keys.All(cached.Answers.ContainsKey))
        {
            answers = cached.Answers;
            memo = true;
        }
        else
        {
            var progress = await new SeriesProgressLoader(_db).LoadAsync(linked.Select(l => new SeriesProgressTarget(l.Key, l.Value)).ToList(), ct);
            answers = linked.Keys.ToDictionary(id => id, id => progress.TryGetValue(id, out var e) && !e.Result.Facts.TrackingOff
                ? e.Result.Answer
                : (SeriesAnswer?)null);
            _cache?.Set(key, new LevelAnswers(answers, now), CompletionMemoAge * 2);
        }

        var linkedIds = linked.Keys.ToList();
        var surviving = await filtered.Where(n => linkedIds.Contains(n.Id)).Select(n => n.Id).ToListAsync(ct);
        int CountOf(SeriesAnswer a) => surviving.Count(id => answers.GetValueOrDefault(id) == a);
        var counts = new BrowseCompletionCountsDto
        {
            HaveItAll = CountOf(SeriesAnswer.HaveItAll),
            FinishedMissing = CountOf(SeriesAnswer.FinishedMissing),
            UpToDate = CountOf(SeriesAnswer.UpToDate),
            MissingSome = CountOf(SeriesAnswer.MissingSome),
            CantTell = CountOf(SeriesAnswer.CantTell),
        };
        var kept = surviving.Where(id => answers.GetValueOrDefault(id) == wanted).ToList();
        _logger?.LogDebug(LogEvents.Catalog.CompletionFilterApplied,
            "Completion filter: {Linked} linked series, {Kept} kept, memo {Memo}, {ElapsedMs} ms", linked.Count, kept.Count, memo, sw.ElapsedMilliseconds);
        return (filtered.Where(n => kept.Contains(n.Id)), counts);
    }
}
