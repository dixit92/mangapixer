namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Reach;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

// 1.44.0 lane C - DESIGN PROTOTYPE (throwaway, phase 1): the browse filter by series completion. Measures what it costs to
// answer every linked series folder of one browse level with the shared progress engine (SeriesProgressLoader -> SeriesAnswers),
// the same answer the Completion tab shows. Not the final shape; the design note decides it.

public sealed partial class CatalogBrowseService
{
    /// <summary>The level's linked series folders and their answers, with what the pass cost (prototype measurement).</summary>
    internal sealed record CompletionLevel(IReadOnlyDictionary<long, SeriesAnswer?> Answers, int Folders, long LinkMs, long LoadMs);

    /// <summary>
    /// Every child FOLDER of the level with its own Confirmed / Auto link to a record (the Completion tab's set), answered from stored
    /// rows only. A folder whose tracking an admin turned off has no answer (null). Never contacts a provider.
    /// </summary>
    internal async Task<CompletionLevel> CompletionOfLevelAsync(IQueryable<CatalogNodeEntity> level, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var confirmed = (int)SeriesLinkState.Confirmed;
        var auto = (int)SeriesLinkState.Auto;
        var folder = (int)CatalogNodeKind.Folder;
        var linked = await (
            from n in level
            where n.Kind == folder
            join l in _db.NodeSeriesLinks.AsNoTracking() on n.Id equals l.NodeId
            where (l.State == confirmed || l.State == auto) && l.RecordId != null
            select new { n.Id, RecordId = l.RecordId!.Value }).ToListAsync(ct);
        var linkMs = sw.ElapsedMilliseconds;
        sw.Restart();
        var progress = linked.Count == 0
            ? new Dictionary<long, SeriesProgressEntry>()
            : await new SeriesProgressLoader(_db).LoadAsync(linked.Select(x => new SeriesProgressTarget(x.Id, x.RecordId)).ToList(), ct);
        var answers = linked.ToDictionary(x => x.Id, x => progress.TryGetValue(x.Id, out var e) && !e.Result.Facts.TrackingOff
            ? e.Result.Answer
            : (SeriesAnswer?)null);
        return new CompletionLevel(answers, linked.Count, linkMs, sw.ElapsedMilliseconds);
    }
}
