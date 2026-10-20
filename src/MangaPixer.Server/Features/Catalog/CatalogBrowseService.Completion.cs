namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Operations.Perf;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

// 1.44.0 lane C - the browse filter by series completion (owner-approved design, 2026-10-17). A level's linked series folders are
// answered with the shared progress engine (SeriesProgressLoader -> SeriesAnswers), the same answer as the Completion tab, and the
// matching ids are folded into the base query BEFORE counting - like the read-state pre-pass - so TotalCount, HasMore and every
// cursor stay honest. Other folders and loose files drop while the filter applies. The answers of a level are kept in memory for
// 60 s for the following pages. Stored rows only; never contacts a provider.
// 1.46.0 lane C: the answers do not depend on the viewer, so a first page reuses a level's answers while everything they were computed
// from is unchanged (CompletionInputs - checked on every first page) and at most CompletionAnswersMaxAge old; otherwise it recomputes,
// as before. A recompute reads the volume contents answers of all the level's series together and each map's JSON once.

public sealed partial class CatalogBrowseService
{
    /// <summary>How long a level's answers serve the following pages of the same level (owner, 2026-10-17: 60 s).</summary>
    public static readonly TimeSpan CompletionMemoAge = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 1.46.0 lane C: the longest a level's answers serve a first page with unchanged inputs - a bound for a change that moved no input
    /// (a scan stopped between writing rows and raising the revision); the next scan or this age recomputes.
    /// </summary>
    public static readonly TimeSpan CompletionAnswersMaxAge = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A level's answers: <c>At</c> = when a page last took them (the 60 s memo), <c>ComputedAt</c> = when they were computed, with the
    /// stamps of their inputs (<see cref="CompletionInputs"/>) and the scope folders the ComicInfo stamp is read over.
    /// </summary>
    private sealed record LevelAnswers(IReadOnlyDictionary<long, SeriesAnswer?> Answers, DateTimeOffset At, DateTimeOffset ComputedAt,
        string Inputs, IReadOnlyList<long> ScopeFolders, string ComicInfo);

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
    /// level still holds the same linked series; the first page reuses them only while their inputs are unchanged (1.46.0), else
    /// recomputes, and refreshes them either way.
    /// </summary>
    private async Task<(IQueryable<CatalogNodeEntity> Query, BrowseCompletionCountsDto Counts)> ApplyCompletionAsync(
        IQueryable<CatalogNodeEntity> filtered, Dictionary<long, long> linked, long libraryId, long? parentId, SeriesAnswer wanted,
        bool continuation, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var key = $"browse-completion:{libraryId}:{parentId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "root"}";
        var now = _time.GetUtcNow();
        var memo = false;
        var reused = false;
        IReadOnlyDictionary<long, SeriesAnswer?> answers;
        var cached = _cache?.Get<LevelAnswers>(key);
        if (continuation && cached is not null && now - cached.At < CompletionMemoAge
            && cached.Answers.Count == linked.Count && linked.Keys.All(cached.Answers.ContainsKey))
        {
            answers = cached.Answers;
            memo = true;
        }
        else
        {
            // 1.46.0 lane C: the stamps are read BEFORE the answers, so answers stored under them are never older than the stamps say.
            var phase = PerfPhase.Start(); // measurement-only laps (no-op unless request timing is on)
            var inputs = await CompletionInputs.BaseAsync(_db, libraryId, linked, ct);
            phase = phase.Lap("completion.inputs");
            if (cached is not null && cached.Inputs == inputs && now - cached.ComputedAt < CompletionAnswersMaxAge
                && cached.ComicInfo == CompletionInputs.ComicInfo(await SeriesProgressLoader.ComicInfoAsync(_db, cached.ScopeFolders, ct)))
            {
                answers = cached.Answers;
                reused = true;
                _cache?.Set(key, cached with { At = now }, CompletionAnswersMaxAge);
                phase.Lap("completion.reuse");
            }
            else
            {
                var loaded = await new SeriesProgressLoader(_db).LoadScopedAsync(
                    linked.Select(l => new SeriesProgressTarget(l.Key, l.Value)).ToList(), ct);
                var progress = loaded.Entries;
                answers = linked.Keys.ToDictionary(id => id, id => progress.TryGetValue(id, out var e) && !e.Result.Facts.TrackingOff
                    ? e.Result.Answer
                    : (SeriesAnswer?)null);
                _cache?.Set(key, new LevelAnswers(answers, now, now, inputs, loaded.ScopeFolders, CompletionInputs.ComicInfo(loaded.ComicInfo)),
                    CompletionAnswersMaxAge);
                phase.Lap("completion.compute");
            }
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
            "Completion filter: {Linked} linked series, {Kept} kept, memo {Memo}, reused {Reused}, {ElapsedMs} ms", linked.Count, kept.Count, memo,
            reused, sw.ElapsedMilliseconds);
        return (filtered.Where(n => kept.Contains(n.Id)), counts);
    }
}
