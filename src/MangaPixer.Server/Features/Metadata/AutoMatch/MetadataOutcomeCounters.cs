namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The local-only outcome counters on <c>metadata_match_runs</c> (stage 2, section
/// 2 "how to measure and tune"): how admins judged automatic results. Counted on
/// the run that decided the node; never sent anywhere.
/// </summary>
public static class MetadataOutcomeCounters
{
    public enum Outcome
    {
        /// <summary>An automatic link was unlinked, re-identified or set to Don't match.</summary>
        AutoChanged,

        /// <summary>A review row was linked to its top stored candidate.</summary>
        AcceptedTop,

        /// <summary>A review row was linked to another record.</summary>
        AcceptedOther,

        /// <summary>A review row was set to Don't match.</summary>
        ReviewDontMatch,
    }

    /// <summary>
    /// Classifies an admin link change of <paramref name="nodeId"/> from its previous
    /// row state (null = none) and counts it. Changes of manual links count nothing.
    /// </summary>
    public static async Task CountChangeAsync(MangaPixerDbContext db, long nodeId, NodeSeriesLinkEntity? previous,
        SeriesLinkState? newState, string? newExternalId, CancellationToken ct)
    {
        if (previous is null)
            return;
        Outcome? outcome = null;
        switch ((SeriesLinkState)previous.State)
        {
            case SeriesLinkState.Auto when newState != SeriesLinkState.Auto:
                // Confirming the same record is agreement, not a change.
                var sameRecord = newState == SeriesLinkState.Confirmed && previous.RecordId is { } rid
                    && await db.MetadataRecords.AnyAsync(r => r.Id == rid && r.ExternalId == newExternalId, ct);
                if (!sameRecord)
                    outcome = Outcome.AutoChanged;
                break;
            case SeriesLinkState.NeedsReview when newState == SeriesLinkState.DontMatch:
                outcome = Outcome.ReviewDontMatch;
                break;
            case SeriesLinkState.NeedsReview when newState == SeriesLinkState.Confirmed:
                outcome = await db.MetadataMatchCandidates.AnyAsync(c => c.NodeId == nodeId && c.Rank == 1 && c.ExternalId == newExternalId, ct)
                    ? Outcome.AcceptedTop
                    : Outcome.AcceptedOther;
                break;
        }
        if (outcome is { } o)
            await CountAsync(db, nodeId, o, ct);
    }

    public static async Task CountAsync(MangaPixerDbContext db, long nodeId, Outcome outcome, CancellationToken ct)
    {
        var runId = await db.MetadataMatchQueue.AsNoTracking().Where(q => q.NodeId == nodeId).Select(q => q.RunId).FirstOrDefaultAsync(ct);
        if (runId is not { } id)
            return;
        var runs = db.MetadataMatchRuns.Where(r => r.Id == id);
        _ = outcome switch
        {
            Outcome.AutoChanged => await runs.ExecuteUpdateAsync(s => s.SetProperty(r => r.AutoChangedByAdmin, r => r.AutoChangedByAdmin + 1), ct),
            Outcome.AcceptedTop => await runs.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewAcceptedTop, r => r.ReviewAcceptedTop + 1), ct),
            Outcome.AcceptedOther => await runs.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewAcceptedOther, r => r.ReviewAcceptedOther + 1), ct),
            _ => await runs.ExecuteUpdateAsync(s => s.SetProperty(r => r.ReviewDontMatch, r => r.ReviewDontMatch + 1), ct),
        };
    }
}
