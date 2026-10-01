namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

/// <summary>Role codes of a candidate in its series family (the review DTO's vocabulary, 1.30.0).</summary>
public static class SeriesFamilyRole
{
    public const string MainStory = "main_story";
    public const string SpinOff = "spin_off";
    public const string SideStory = "side_story";
    public const string Prequel = "prequel";
    public const string Sequel = "sequel";
    /// <summary>An alternate version of the same story (MangaUpdates <c>Alternate Version</c>: a remake, a colour edition ...).</summary>
    public const string Alternate = "alternate";

    /// <summary>An alternate story (MangaUpdates <c>Alternate Story</c>: another take on the same characters or world).</summary>
    public const string AlternateStory = "alternate_story";

    /// <summary>An adaptation of the family's main record (the main record is its source).</summary>
    public const string Adaptation = "adaptation";

    /// <summary>The work the family's main record is adapted from.</summary>
    public const string Source = "source";

    /// <summary>Same family, relation unknown (the fallback, or two members linked only through a third).</summary>
    public const string Related = "related";
}

/// <summary>A candidate's place in its series family: <see cref="Group"/> is the 0-based index of the family's first member.</summary>
public sealed record SeriesFamilyMember(int Group, string Role);

/// <summary>
/// Series families among the candidates of one work (1.30.0, owner: the review groups a series with its spin-offs so an admin
/// does not pair the main series with spin-off metadata or vice versa). Pure and deterministic.
/// <list type="bullet">
/// <item>Two records of one provider are one family when either lists the other in its related series with a family relation
/// (main story, spin-off, side story, prequel, sequel, alternate version / story, adapted from, full anthology) - on a record A,
/// a relation <c>{R, B}</c> means "B is A's R" (MangaUpdates: the main series lists a prequel as <c>Prequel</c>, the prequel lists it
/// as <c>Sequel</c>).</item>
/// <item>Fallback when neither lists the other: they share a title head (<c>Title</c> and <c>Title: Subtitle</c>, or two subtitles
/// of one <c>Title</c>) AND both have known authors that overlap. A search hit has no relations or authors of its own; it joins a
/// family through a fetched record's relations.</item>
/// <item>The family's hub (its main story): a member another calls <c>Main Story</c> or that lists a member as <c>Spin-Off</c> /
/// <c>Side Story</c>; else one whose title is another member's head; else the earliest start year; else the first. Each other
/// member's role is its relation seen from the hub (<see cref="SeriesFamilyRole"/>).</item>
/// </list>
/// </summary>
public static class SeriesFamilies
{
    // "B is A's R" when A lists {R, B}: B's role seen from A.
    private static readonly Dictionary<string, string> s_forward = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main story"] = SeriesFamilyRole.MainStory,
        ["spin-off"] = SeriesFamilyRole.SpinOff,
        ["side story"] = SeriesFamilyRole.SideStory,
        ["prequel"] = SeriesFamilyRole.Prequel,
        ["sequel"] = SeriesFamilyRole.Sequel,
        ["alternate version"] = SeriesFamilyRole.Alternate,
        ["alternate story"] = SeriesFamilyRole.AlternateStory,
        ["adapted from"] = SeriesFamilyRole.Source,
        ["full anthology"] = SeriesFamilyRole.Related,
    };

    // A's role seen from B when A lists {R, B} ("B is A's R").
    private static readonly Dictionary<string, string> s_inverse = new(StringComparer.OrdinalIgnoreCase)
    {
        ["main story"] = SeriesFamilyRole.SpinOff,
        ["spin-off"] = SeriesFamilyRole.MainStory,
        ["side story"] = SeriesFamilyRole.MainStory,
        ["prequel"] = SeriesFamilyRole.Sequel,
        ["sequel"] = SeriesFamilyRole.Prequel,
        ["alternate version"] = SeriesFamilyRole.Alternate,
        ["alternate story"] = SeriesFamilyRole.AlternateStory,
        ["adapted from"] = SeriesFamilyRole.Adaptation,
        ["full anthology"] = SeriesFamilyRole.Related,
    };

    /// <summary>True when <paramref name="relation"/> (a provider relation type) ties two records into one series family.</summary>
    public static bool IsFamilyRelation(string? relation) => relation is not null && s_forward.ContainsKey(relation.Trim());

    /// <summary>True when the two records are one series family (a family relation either way, else the head + author fallback).</summary>
    public static bool AreFamily(MatchCandidate a, MatchCandidate b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (!string.Equals(a.Provider, b.Provider, StringComparison.Ordinal)
            || string.Equals(a.ExternalId, b.ExternalId, StringComparison.Ordinal))
            return false;
        var ab = RelationOf(a, b);
        var ba = RelationOf(b, a);
        if (ab is not null || ba is not null)
            return IsFamilyRelation(ab) || IsFamilyRelation(ba);
        return SharesHead(a, b) && AuthorsOverlap(a, b);
    }

    /// <summary>
    /// The family of each candidate (aligned with <paramref name="candidates"/>; null for a candidate with no family member among
    /// them). Families are the connected groups of <see cref="AreFamily"/>.
    /// </summary>
    public static IReadOnlyList<SeriesFamilyMember?> Of(IReadOnlyList<MatchCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var n = candidates.Count;
        var parent = Enumerable.Range(0, n).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);
        for (var i = 0; i < n; i++)
            for (var j = i + 1; j < n; j++)
                if (AreFamily(candidates[i], candidates[j]))
                {
                    var (ri, rj) = (Find(i), Find(j));
                    if (ri != rj)
                        parent[Math.Max(ri, rj)] = Math.Min(ri, rj);
                }

        var result = new SeriesFamilyMember?[n];
        foreach (var group in Enumerable.Range(0, n).GroupBy(Find).Where(g => g.Count() > 1))
        {
            var members = group.OrderBy(i => i).ToList();
            var hub = Hub(members, candidates, out var hubIsMain);
            foreach (var i in members)
            {
                string role;
                if (i == hub)
                    role = hubIsMain ? SeriesFamilyRole.MainStory : RoleSeenFrom(candidates[members.First(m => m != hub)], candidates[hub]);
                else
                    role = RoleSeenFrom(candidates[hub], candidates[i]);
                result[i] = new SeriesFamilyMember(members[0], role);
            }
        }
        return result;
    }

    /// <summary>The role of <paramref name="x"/> seen from <paramref name="from"/>, <see cref="SeriesFamilyRole.Related"/> when neither lists the other.</summary>
    private static string RoleSeenFrom(MatchCandidate from, MatchCandidate x)
    {
        if (RelationOf(from, x) is { } r && s_forward.TryGetValue(r.Trim(), out var forward))
            return forward;
        if (RelationOf(x, from) is { } back && s_inverse.TryGetValue(back.Trim(), out var inverse))
            return inverse;
        return SeriesFamilyRole.Related;
    }

    private static int Hub(List<int> members, IReadOnlyList<MatchCandidate> c, out bool isMain)
    {
        bool CalledMain(int x) => members.Where(y => y != x).Any(y =>
            string.Equals(RelationOf(c[y], c[x])?.Trim(), "main story", StringComparison.OrdinalIgnoreCase)
            || RelationOf(c[x], c[y])?.Trim().ToLowerInvariant() is "spin-off" or "side story");
        bool HeadOfOther(int x)
        {
            var own = TitlesOf(c[x]).Select(TitleNormalizer.ScoringForm).Where(f => f.Length > 0).ToHashSet(StringComparer.Ordinal);
            return members.Where(y => y != x).Any(y => TitlesOf(c[y]).Select(TitleNormalizer.SubtitleHead).OfType<string>()
                .Any(h => own.Contains(TitleNormalizer.ScoringForm(h))));
        }

        var main = members.FirstOrDefault(CalledMain, -1);
        if (main < 0)
            main = members.FirstOrDefault(HeadOfOther, -1);
        isMain = main >= 0;
        if (isMain)
            return main;
        return members.OrderBy(m => c[m].StartYear ?? int.MaxValue).ThenBy(m => m).First();
    }

    /// <summary>The relation type <paramref name="a"/> lists for <paramref name="b"/> ("b is a's R"), or null.</summary>
    private static string? RelationOf(MatchCandidate a, MatchCandidate b) =>
        (a.Relations ?? []).FirstOrDefault(r => string.Equals(r.ExternalId, b.ExternalId, StringComparison.Ordinal))?.Relation;

    private static IEnumerable<string> TitlesOf(MatchCandidate c) =>
        new[] { c.Title }.Concat(c.AltTitles ?? [])
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => AutoMatchText.WithoutDisambiguator(t) ?? t);

    /// <summary>A title of one, up to its subtitle break, equals a title of the other up to its break (at least one has a break).</summary>
    private static bool SharesHead(MatchCandidate a, MatchCandidate b)
    {
        static HashSet<(string Head, bool Broken)> Heads(MatchCandidate c) => TitlesOf(c)
            .Select(t => TitleNormalizer.SubtitleHead(t) is { } h ? (TitleNormalizer.ScoringForm(h), true) : (TitleNormalizer.ScoringForm(t), false))
            .Where(x => x.Item1.Length > 0)
            .ToHashSet();
        var ha = Heads(a);
        return Heads(b).Any(x => ha.Any(y => y.Head == x.Head && (x.Broken || y.Broken)));
    }

    private static bool AuthorsOverlap(MatchCandidate a, MatchCandidate b) =>
        (a.Authors ?? []).Any(x => !string.IsNullOrWhiteSpace(x) && (b.Authors ?? []).Any(y => AutoMatchText.NamesEqual(x, y)));
}
