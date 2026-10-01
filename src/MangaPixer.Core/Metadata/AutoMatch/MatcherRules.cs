namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>
/// The revision of the automatic matcher's rules (1.31.0): what <see cref="MatchScorer"/> (with <see cref="CountEvidence"/>,
/// <see cref="SeriesFamilies"/> and the other evidence) decides for the same inputs, and which <see cref="MatchReason"/> chips it
/// puts on a result. Stamped on each queue row when the work is scored (<c>metadata_match_queue.RulesRevision</c>; null on a
/// row scored before the stamp existed = older than every revision).
///
/// Bump it when scoring or the reasons change in a way an admin would see on a work that is still waiting in Needs review
/// (a new or removed chip, a changed band, a different rank). The next background pass then checks those works once more
/// under the new rules. Do NOT bump it for a change that cannot alter an earlier result (a new log line, a refactor, a new
/// reason that only appears for input the old rules never produced). The cover layer has its own counter
/// (<c>CoverDecisionService.RulesRevision</c>); this one is the matcher's.
/// </summary>
public static class MatcherRules
{
    /// <summary>
    /// 1: the first stamped revision (1.31.0). Everything scored by 1.27.0 - 1.30.x carries no stamp and is therefore older -
    /// including the count rule of 1.29.0 and the matcher changes of 1.30.0.
    /// </summary>
    public const int Revision = 1;
}
