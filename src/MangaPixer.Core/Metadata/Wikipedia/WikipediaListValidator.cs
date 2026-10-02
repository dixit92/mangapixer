namespace com.lifepixer.mangapixer.Core.Metadata.Wikipedia;

using System.Text.RegularExpressions;

/// <summary>What a parsed list is checked against: MangaDex's stored exact list (when there is one) and MangaUpdates' volume total.</summary>
public sealed record WikipediaReference(IReadOnlyList<VolumeMapEntry> MangaDexVolumes, int? MangaUpdatesVolumes)
{
    public static WikipediaReference None { get; } = new([], null);
}

/// <summary>The verdict on one parsed table. <see cref="Code"/> is a sanitized reason (counts and codes only - it is logged and stored).</summary>
public sealed record WikipediaValidation(bool Accepted, string Code, int SharedChapters, int AgreeingChapters)
{
    public const string Ok = "ok";
    public const string NoVolumes = "no_volumes";
    public const string NotMonotonic = "not_monotonic";
    public const string TooManyVolumes = "too_many_volumes";
    public const string DisagreesWithMangaDex = "disagrees_with_mangadex";
    public const string UnverifiedImplicit = "unverified_implicit";
}

/// <summary>
/// Validation before a Wikipedia list is used (1.32.0, design 6.5) and the choice among several tables of one page. Pure. A list is
/// accepted when: it has volumes with chapters; the chapter numbers increase from volume to volume (a stray row is tolerated, a
/// shuffled list is not); it names no more volumes than MangaUpdates' total plus one (a list of another work - a spin-off, a novel
/// series, a re-numbered edition - has more); at least <see cref="MinAgreement"/> of the chapters both it and MangaDex's list place sit in
/// the SAME volume; and, with no MangaDex list to compare to, no chapter number had to be guessed by counting.
/// </summary>
public static class WikipediaListValidator
{
    public const double MinAgreement = 0.95;

    private static readonly Regex s_otherWork = new(
        @"spin|novel|side stor|kanzenban|anime|guide|databook|art ?book|special|omnibus|box ?set|fanbook|gaiden",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static WikipediaValidation Validate(WikipediaTable table, WikipediaReference reference)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(reference);
        var withChapters = table.Volumes
            .Select(v => (Row: v, Number: VolumeMapJson.Parse(v.Volume)))
            .Where(v => v.Number is not null && v.Row.Chapters.Count > 0)
            .Select(v => (v.Row, Number: v.Number!.Value))
            .OrderBy(v => v.Number)
            .ToList();
        if (withChapters.Count == 0)
            return new WikipediaValidation(false, WikipediaValidation.NoVolumes, 0, 0);

        if (reference.MangaUpdatesVolumes is { } total)
        {
            var numbered = withChapters.Where(v => v.Number >= 1).ToList();
            if (numbered.Count > total + 1 || (numbered.Count > 0 && numbered[^1].Number > total + 1))
                return new WikipediaValidation(false, WikipediaValidation.TooManyVolumes, 0, 0);
        }

        var violations = 0;
        for (var i = 1; i < withChapters.Count; i++)
        {
            var (previous, next) = (Bounds(withChapters[i - 1].Row), Bounds(withChapters[i].Row));
            if (next.Min < previous.Min || next.Max < previous.Max)
                violations++;
        }
        if (violations > Math.Max(1, withChapters.Count / 20))
            return new WikipediaValidation(false, WikipediaValidation.NotMonotonic, 0, 0);

        var placed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var volume in reference.MangaDexVolumes)
        {
            foreach (var chapter in volume.Chapters)
                placed[chapter] = volume.Volume;
        }
        int shared = 0, agreeing = 0;
        foreach (var (row, _) in withChapters)
        {
            foreach (var chapter in row.Chapters)
            {
                if (!placed.TryGetValue(chapter, out var volume))
                    continue;
                shared++;
                if (string.Equals(volume, row.Volume, StringComparison.Ordinal))
                    agreeing++;
            }
        }
        if (shared > 0)
        {
            return (double)agreeing / shared >= MinAgreement
                ? new WikipediaValidation(true, WikipediaValidation.Ok, shared, agreeing)
                : new WikipediaValidation(false, WikipediaValidation.DisagreesWithMangaDex, shared, agreeing);
        }
        // Nothing to compare: counted-on chapter numbers are a guess, and a guess is not enough on its own.
        return table.ImplicitChapters > 0
            ? new WikipediaValidation(false, WikipediaValidation.UnverifiedImplicit, 0, 0)
            : new WikipediaValidation(true, WikipediaValidation.Ok, 0, 0);
    }

    /// <summary>
    /// The table of a page that is this series' own list: the accepted one that agrees with MangaDex on the most chapters, then the one whose
    /// heading does not name another work (spin-off, novels, kanzenban ...), then page order. With nothing accepted: null and the verdict
    /// of the first table.
    /// </summary>
    public static WikipediaTable? Pick(IReadOnlyList<WikipediaTable> tables, WikipediaReference reference, out WikipediaValidation verdict)
    {
        ArgumentNullException.ThrowIfNull(tables);
        verdict = new WikipediaValidation(false, WikipediaValidation.NoVolumes, 0, 0);
        var judged = tables.Select((t, order) => (Table: t, Order: order, Verdict: Validate(t, reference))).ToList();
        if (judged.Count > 0)
            verdict = judged[0].Verdict;
        var best = judged
            .Where(j => j.Verdict.Accepted)
            .OrderByDescending(j => j.Verdict.AgreeingChapters)
            .ThenBy(j => j.Table.Heading is not null && s_otherWork.IsMatch(j.Table.Heading) ? 1 : 0)
            .ThenBy(j => j.Order)
            .Cast<(WikipediaTable Table, int Order, WikipediaValidation Verdict)?>()
            .FirstOrDefault();
        if (best is null)
            return null;
        verdict = best.Value.Verdict;
        return best.Value.Table;
    }

    private static (decimal Min, decimal Max) Bounds(WikipediaVolumeRow row)
    {
        var numbers = row.Chapters.Select(c => VolumeMapJson.Parse(c)).OfType<decimal>().ToList();
        return numbers.Count == 0 ? (0m, 0m) : (numbers.Min(), numbers.Max());
    }
}
