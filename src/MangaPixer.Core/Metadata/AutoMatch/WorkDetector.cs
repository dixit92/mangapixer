namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Globalization;

/// <summary>
/// The work detector (metadata stage 2, design section 1 + owner decision 6): classifies a
/// folder from its shape alone - display names and counts, no IO - so only folders that ARE one
/// work are matched at folder level, collections are matched archive by archive (numbered
/// mini-series grouped), and anything unclear goes to review.
///
/// Order of the rules:
/// <list type="number">
/// <item>Depth 0 (the library root) is <see cref="WorkClass.Excluded"/>; a unit-named folder below
/// a non-root parent (<c>Volumes</c>, <c>Season 2</c>, <c>Part 3</c>) is <see cref="WorkClass.UnitSub"/>.</item>
/// <item>Subfolders (empty ones ignored): two or more non-unit subfolders make a franchise container
/// (at least half related to the parent's name, or <c>Part N - subtitle</c> children) or a collection
/// container; exactly one makes a wrapper (no archives) or <see cref="WorkClass.Mixed"/>; only unit
/// subfolders make <see cref="WorkClass.SeriesWithUnits"/>.</item>
/// <item>A leaf named like the creator its archives carry (the dominant <c>[circle (artist)]</c> tag,
/// or a caller-supplied provider author name) is an <see cref="WorkClass.ArtistCollection"/>.</item>
/// <item>One archive is a <see cref="WorkClass.OneShot"/>.</item>
/// <item>The E6 discriminator, on archive base titles with every bracket group removed (so a creator
/// tag repeated in the names is not "coherence"): unit-named share &gt;= 0.8, or one base title
/// &gt;= 80%, or &gt;= 60% of the titled archives matching the folder -> <see cref="WorkClass.Series"/>;
/// distinct-base ratio &gt;= 0.6 with no base at 50% or more -> <see cref="WorkClass.CollectionLeaf"/>;
/// otherwise <see cref="WorkClass.Ambiguous"/> (review only).</item>
/// </list>
/// Reasons never contain names (counts and shares only), so they are safe to log.
/// </summary>
public sealed class WorkDetector : IWorkDetector
{
    /// <summary>Unit-named share of a leaf's archives at or above which the folder is one series.</summary>
    public const double UnitNamedShare = 0.80;

    /// <summary>Share of one base title at or above which the folder is one series.</summary>
    public const double DominantBaseShare = 0.80;

    /// <summary>Share of titled archives matching the folder name at or above which the folder is one series.</summary>
    public const double FolderMatchShare = 0.60;

    /// <summary>Title score at or above which an archive base title matches a folder variant.</summary>
    public const double FolderMatchScore = 0.80;

    /// <summary>Distinct-base ratio at or above which (with no dominant base) archives are separate works.</summary>
    public const double ArchiveLevelDistinctRatio = 0.60;

    /// <summary>A collection has no base title at or above this share.</summary>
    public const double ArchiveLevelMaxBaseShare = 0.50;

    /// <summary>Share of archives carrying the dominant creator tag needed to call the folder an artist folder.</summary>
    public const double ArtistTagShare = 0.50;

    /// <summary>Share of doujin-shaped archive names at or above which the Content suggestion is made.</summary>
    public const double DoujinShare = 0.50;

    /// <summary>Title score at or above which a child folder counts as related to its parent.</summary>
    public const double FranchiseRelatedScore = 0.60;

    public WorkClassification Classify(FolderShape folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        var archives = folder.ArchiveNames ?? [];
        var subfolders = (folder.Subfolders ?? []).Where(s => s.DescendantArchiveCount > 0).ToList();

        if (folder.Depth <= 0)
            return Result(WorkClass.Excluded, MatchLevel.None, ["library root"]);

        if (folder.Depth >= 2 && AutoMatchText.IsUnitFolderName(folder.DisplayName))
            return Result(WorkClass.UnitSub, MatchLevel.None, ["unit subfolder name below a non-root parent"]);

        var anatomies = archives.Select(ArchiveNameAnatomy.Parse).ToList();
        var content = SuggestContent(anatomies);
        var unitSubs = subfolders.Where(s => AutoMatchText.IsUnitFolderName(s.DisplayName)).ToList();
        var workSubs = subfolders.Where(s => !AutoMatchText.IsUnitFolderName(s.DisplayName)).ToList();

        if (workSubs.Count >= 2)
            return ClassifyContainer(folder, workSubs, archives, content);

        if (workSubs.Count == 1)
        {
            if (archives.Count == 0)
                return Result(WorkClass.Wrapper, MatchLevel.None, ["exactly one non-unit subfolder and no archives"], content: content);

            // Loose archives that are separate works (one-shots, or a whole series in one archive) are
            // matched one by one (owner, 2026-09-26); loose units of one work keep the folder review-only.
            var mixedGroups = LooseWorkGroups(folder, archives, content);
            return mixedGroups.Count > 0
                ? Result(WorkClass.Mixed, MatchLevel.Archive,
                    [Invariant($"one non-unit subfolder plus {archives.Count} loose archives, matched one by one")], mixedGroups, content)
                : Result(WorkClass.Mixed, MatchLevel.ReviewOnly,
                    [Invariant($"one non-unit subfolder plus {archives.Count} loose archives")], content: content);
        }

        if (unitSubs.Count > 0)
        {
            return Result(WorkClass.SeriesWithUnits, MatchLevel.Folder,
                [Invariant($"{unitSubs.Count} unit subfolders, {archives.Count} loose archives")], content: content);
        }

        if (archives.Count == 0)
            return Result(WorkClass.Excluded, MatchLevel.None, ["no archives"]);

        if (IsArtistFolder(folder, anatomies, out var artistReason))
        {
            return Result(WorkClass.ArtistCollection, MatchLevel.Archive, [artistReason],
                GroupArchives(archives), content);
        }

        if (archives.Count == 1)
            return Result(WorkClass.OneShot, MatchLevel.Folder, ["exactly one archive"], content: content);

        return ClassifyLeaf(folder, archives, content);
    }

    private static WorkClassification ClassifyContainer(
        FolderShape folder, List<ChildFolderShape> workSubs, IReadOnlyList<string> archives, ContentSuggestion content)
    {
        var parent = TitleNormalizer.Normalize(folder.DisplayName).Primary;
        var related = workSubs.Count(s =>
        {
            if (AutoMatchText.IsPartWithSubtitle(s.DisplayName))
                return true;
            if (parent.Length == 0)
                return false;
            var child = TitleNormalizer.Normalize(s.DisplayName).Primary;
            return AutoMatchText.ContainsTokens(child, parent) || TitleSimilarity.Score(child, parent) >= FranchiseRelatedScore;
        });

        // The container itself is never matched (its subfolders are the candidates), but loose archives
        // that are separate works are matched one by one (owner, 2026-09-26): level Archive + groups.
        var groups = archives.Count > 0 ? LooseWorkGroups(folder, archives, content) : [];
        var level = groups.Count > 0 ? MatchLevel.Archive : MatchLevel.None;
        var loose = archives.Count == 0 ? string.Empty
            : groups.Count > 0 ? Invariant($", {archives.Count} loose archives matched one by one")
            : Invariant($", {archives.Count} loose archives (units of one work - not matched)");
        return related * 2 >= workSubs.Count
            ? Result(WorkClass.FranchiseContainer, level,
                [Invariant($"{related} of {workSubs.Count} subfolders related to the folder name{loose}")], groups, content)
            : Result(WorkClass.CollectionContainer, level,
                [Invariant($"{workSubs.Count} unrelated subfolders{loose}")], groups, content);
    }

    /// <summary>
    /// Archive groups for archives lying loose next to subfolders, or none when they look like units of
    /// one work (<c>Vol 01</c>, <c>Vol 02</c> ...), which must not be matched one by one. A single titled
    /// archive is its own work: a one-shot, or a whole series in one archive.
    /// </summary>
    private static IReadOnlyList<ArchiveGroup> LooseWorkGroups(FolderShape folder, IReadOnlyList<string> archives, ContentSuggestion content)
    {
        if (archives.Count == 1)
            return GroupArchives(archives);
        // Only "units of one work" is excluded. The folder-level collection thresholds (E6) are
        // calibrated for whole folders and call two different titles ambiguous; loose archives outside
        // any series folder are works of their own (numbered mini-series still grouped).
        var leaf = ClassifyLeaf(folder, archives, content);
        return leaf.Class == WorkClass.Series ? [] : GroupArchives(archives);
    }

    private static WorkClassification ClassifyLeaf(FolderShape folder, IReadOnlyList<string> archives, ContentSuggestion content)
    {
        var total = archives.Count;
        var bases = archives.Select(TitleNormalizer.ArchiveBaseTitle).ToList();
        var keys = bases.Select(TitleNormalizer.ScoringForm).ToList();
        var unitNamed = keys.Count(k => k.Length == 0);
        var titled = keys.Where(k => k.Length > 0).ToList();
        var byKey = titled.GroupBy(k => k, StringComparer.Ordinal).Select(g => g.Count()).ToList();
        var maxBase = byKey.Count == 0 ? 0 : byKey.Max();
        var distinct = byKey.Count;

        var folderTitle = TitleNormalizer.Normalize(folder.DisplayName);
        var folderVariants = folderTitle.Variants.Concat(folderTitle.Derived.Select(d => d.Text)).ToList();
        var folderKeys = folderVariants.Select(TitleNormalizer.ScoringForm).Where(k => k.Length > 0).ToHashSet(StringComparer.Ordinal);
        var matching = bases.Count(b => b.Length > 0
            && (folderKeys.Contains(TitleNormalizer.ScoringForm(b)) || TitleSimilarity.Best([b], folderVariants) >= FolderMatchScore));

        var unitShare = (double)unitNamed / total;
        var baseShare = (double)maxBase / total;
        var matchShare = titled.Count == 0 ? 0 : (double)matching / titled.Count;
        var distinctRatio = (double)distinct / total;
        var shares = Invariant($"unit-named {unitShare:0.00}, top base {baseShare:0.00}, folder match {matchShare:0.00}, distinct {distinctRatio:0.00}");

        if (unitShare >= UnitNamedShare || baseShare >= DominantBaseShare || matchShare >= FolderMatchShare)
            return Result(WorkClass.Series, MatchLevel.Folder, ["archives are units of one work: " + shares], content: content);

        // "Head - Subtitle" names sharing one head: a series with subtitled volumes or a creator
        // folder with unbracketed names - the shape cannot tell, so neither level is safe.
        var heads = bases.Where(b => b.Length > 0)
            .Select(b => TitleNormalizer.DerivedVariants(b).FirstOrDefault(d => d.Kind == DerivedTitleKind.SubtitleSplit)?.Text)
            .OfType<string>()
            .GroupBy(TitleNormalizer.ScoringForm, StringComparer.Ordinal)
            .Select(g => g.Count())
            .DefaultIfEmpty(0)
            .Max();
        if (titled.Count > 0 && (double)heads / titled.Count >= DominantBaseShare)
        {
            return Result(WorkClass.Ambiguous, MatchLevel.ReviewOnly,
                ["archives share one title before a subtitle separator: " + shares], content: content);
        }

        if (distinctRatio >= ArchiveLevelDistinctRatio && baseShare < ArchiveLevelMaxBaseShare)
        {
            return Result(WorkClass.CollectionLeaf, MatchLevel.Archive, ["archives are different works: " + shares],
                GroupArchives(archives), content);
        }

        return Result(WorkClass.Ambiguous, MatchLevel.ReviewOnly, ["neither one work nor a collection: " + shares], content: content);
    }

    /// <summary>
    /// An artist folder: the folder name equals a caller-supplied provider author, or the creator tag
    /// most archives carry (<c>[circle (artist)]</c> or a lone <c>[tag]</c>) in at least half of the
    /// archives. An unbracketed <c>Name - Title</c> prefix is deliberately NOT a creator tag: it is
    /// indistinguishable from the common <c>Title - Chapter 001</c> / <c>Title - Subtitle</c> naming.
    /// </summary>
    private static bool IsArtistFolder(FolderShape folder, List<ArchiveNameAnatomy> anatomies, out string reason)
    {
        reason = string.Empty;
        var folderName = TitleNormalizer.Normalize(folder.DisplayName).Primary;
        if (folderName.Length == 0 || AutoMatchText.IsCategoryWord(folderName))
            return false;

        if (folder.KnownAuthorNames is { Count: > 0 } known
            && known.Any(a => AutoMatchText.IsAuthorLike(a, requireTwoTokens: false) && AutoMatchText.NamesEqual(a, folderName)))
        {
            reason = "folder name equals a provider author name";
            return true;
        }

        var carrying = anatomies.Count(a => a.CreatorTags.Any(t => AutoMatchText.NamesEqual(t, folderName)));

        if (carrying > 0 && (double)carrying / anatomies.Count >= ArtistTagShare)
        {
            reason = Invariant($"folder name equals the creator tag of {carrying} of {anatomies.Count} archives");
            return true;
        }
        return false;
    }

    /// <summary>
    /// Archive-level groups: archives sharing one base title (a numbered mini-series,
    /// <c>Title 1/2/3</c>) form one group queried by that base; any other archive is its own group
    /// queried by its clean title. Unit-named archives with no title are left out (nothing to query).
    /// </summary>
    internal static IReadOnlyList<ArchiveGroup> GroupArchives(IReadOnlyList<string> archives)
    {
        var order = new List<string>();
        var members = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var spelling = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < archives.Count; i++)
        {
            var baseTitle = TitleNormalizer.ArchiveBaseTitle(archives[i]);
            var key = TitleNormalizer.ScoringForm(baseTitle);
            if (key.Length == 0) continue;
            if (!members.TryGetValue(key, out var list))
            {
                members[key] = list = [];
                order.Add(key);
                spelling[key] = baseTitle;
            }
            else if (string.CompareOrdinal(baseTitle, spelling[key]) < 0)
            {
                spelling[key] = baseTitle;
            }
            list.Add(i);
        }

        var groups = new List<ArchiveGroup>(order.Count);
        foreach (var key in order)
        {
            var idx = members[key];
            var query = idx.Count == 1 && TitleNormalizer.Normalize(archives[idx[0]]).Primary is { Length: > 0 } primary
                ? primary
                : spelling[key];
            groups.Add(new ArchiveGroup(query, idx));
        }
        return groups;
    }

    private static ContentSuggestion SuggestContent(List<ArchiveNameAnatomy> anatomies)
    {
        if (anatomies.Count == 0)
            return ContentSuggestion.None;
        var doujin = anatomies.Count(a => a.IsDoujinShaped);
        return doujin > 0 && (double)doujin / anatomies.Count >= DoujinShare
            ? ContentSuggestion.DoujinshiAndAdultOneShots
            : ContentSuggestion.None;
    }

    private static WorkClassification Result(
        WorkClass cls, MatchLevel level, IReadOnlyList<string> reasons,
        IReadOnlyList<ArchiveGroup>? groups = null, ContentSuggestion content = ContentSuggestion.None) =>
        new(cls, level, reasons, groups ?? [], content);

    private static string Invariant(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}
