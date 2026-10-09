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
    /// 2 (1.32.0): comics - album / issue / extra unit tokens and comics format words in names (lane A), Grand Comics Database
    /// candidates with their own evidence (lane B, bits 19-22), a Webtoon(s) category folder giving its origin again (it never
    /// did since 1.27.0: compared in the wrong form), plus 1.31.1's "Episode N next to a volume is a part" (shipped unbumped).
    /// 3 (1.34.2): dated doujin names (<c>Creator] [yyyy-mm] Character (Tag) (Title)</c>, <see cref="DatedDoujinName"/>) are searched,
    /// grouped and scored by their title instead of the character, with the character as a last, review-only search
    /// (<see cref="QueryVariantKind.CharacterName"/>), and count as doujin-shaped also without the tag's opening bracket.
    /// 4 (1.39.0, owner): a record author written with another name in brackets (<c>Main Name (Other Name)</c>, MangaUpdates' form for an
    /// author's alias) is known by each of those names (<see cref="AutoMatchText.AuthorNameForms"/>): archive creator tags and name hints
    /// that give the alias now agree with the record (no <see cref="MatchReason.AuthorConflict"/>), a disambiguator naming the alias is
    /// the record's own, and a folder named after the alias is a provider author's folder.
    /// </summary>
    public const int Revision = 4;
}
