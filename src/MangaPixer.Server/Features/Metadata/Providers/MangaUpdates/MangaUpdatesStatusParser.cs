namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

using System.Globalization;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// Reads MangaUpdates' free-text <c>status</c> ("43 Volumes (Ongoing)", "200
/// Chapters + Prologue (Complete)  \n15 Volumes (Complete)", often followed by a
/// Markdown note) into the volume count, the chapter total (1.27.0: the first line
/// that STARTS with "N Chapters" - season lines such as "S1: 110 Chapters" are parts
/// of it, never the total) and the publication status in the country of origin.
/// Tolerant: anything it cannot read stays null / Unknown, and the flattened text is
/// kept for display.
/// </summary>
public static partial class MangaUpdatesStatusParser
{
    public const int MaxStatusTextLength = 1024;

    [GeneratedRegex(@"(\d{1,5})\s*(?:Volumes?|Vols?\.?)\b[^()\n]*\(([^()\n]{1,40})\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeLine();

    [GeneratedRegex(@"\(([^()\n]{1,40})\)", RegexOptions.CultureInvariant)]
    private static partial Regex AnyParenthesis();

    [GeneratedRegex(@"^[ \t]*(\d{1,5})[ \t]*Chapters?\b", RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ChapterTotalLine();

    /// <summary>
    /// <paramref name="Chapters"/> (1.27.0): the stated chapter total ("195 Chapters (Hiatus)"). The API's
    /// <c>latest_chapter</c> restarts per season on season-renumbered webtoons, so the matcher's count rule
    /// needs this total as well.
    /// </summary>
    public sealed record Result(int? Volumes, MetadataOriginStatus? Status, string? Text, int? Chapters = null);

    public static Result Parse(string? status)
    {
        var text = MetadataText.Flatten(status, MaxStatusTextLength);
        if (text is null)
            return new Result(null, null, null);

        int? volumes = null;
        MetadataOriginStatus? origin = null;

        var volumeMatch = VolumeLine().Match(text);
        if (volumeMatch.Success)
        {
            if (int.TryParse(volumeMatch.Groups[1].Value, out var v) && v > 0)
                volumes = v;
            origin = MapStatusWord(volumeMatch.Groups[2].Value);
        }

        // No volume line (chapter-only series): the first recognizable "(status)".
        if (origin is null)
        {
            foreach (Match m in AnyParenthesis().Matches(text))
            {
                origin = MapStatusWord(m.Groups[1].Value);
                if (origin is not null)
                    break;
            }
        }

        int? chapters = ChapterTotalLine().Match(text) is { Success: true } c
            && int.TryParse(c.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0
                ? n
                : null;

        return new Result(volumes, origin, text, chapters);
    }

    // "86 Chapters", "13+2 Volumes" (a sum), "18 Physical Volumes" (up to two words between the number and the unit).
    [GeneratedRegex(@"(?<![\p{L}\p{N}.+])(\d{1,5})(?:\s*\+\s*(\d{1,5}))?(?:\.\d+)?\s*(?:[\p{L}-]+\s+){0,2}chapters?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesChapters();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.+])(\d{1,5})(?:\s*\+\s*(\d{1,5}))?(?:\.\d+)?\s*(?:[\p{L}-]+\s+){0,2}(?:volumes?|vols?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesVolumes();

    /// <summary>
    /// The volume and chapter totals of a publisher's <c>notes</c> ("10 Volumes / 60 Chapters; Ongoing",
    /// "86 Chapters; Ongoing", "12 Volumes (Ongoing)", "13+2 Volumes; Complete", "18 Physical Volumes"; 1.27.0). The
    /// largest stated number of each, or null.
    /// </summary>
    public static (int? Volumes, int? Chapters) ParsePublisherNotes(string? notes)
    {
        var parsed = ParsePublisherEdition(notes);
        return (parsed.Volumes, parsed.Chapters);
    }

    /// <summary>
    /// What a publisher's <c>notes</c> say about its regular edition (1.30.0): the volume / chapter totals and the publisher's own
    /// status. A note may list several editions split by <c>|</c> or a line break ("42 Volumes - Ongoing | 14 Omnibus; print,
    /// 3-in-1 - Ongoing"); an omnibus, N-in-1, perfect or deluxe edition numbers its volumes differently from the original, so its
    /// counts never feed <see cref="PublisherEdition.Volumes"/> - <see cref="PublisherEdition.Omnibus"/> is true when such an edition
    /// is the only one that states a volume count. The status comes from the segment that gave the volume total (else the chapter
    /// total, else the first regular segment that states one; never an omnibus segment's); "Defunct" / "Dropped" read as cancelled.
    /// </summary>
    public static PublisherEdition ParsePublisherEdition(string? notes)
    {
        var text = MetadataText.Flatten(notes, MaxStatusTextLength);
        if (text is null)
            return PublisherEdition.Empty;

        int? volumes = null, chapters = null;
        MetadataOriginStatus? volumeStatus = null, chapterStatus = null, anyStatus = null;
        var omnibusVolumes = false;
        foreach (var segment in text.Split(['|', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var segmentVolumes = Largest(NotesVolumes().Matches(segment));
            var segmentChapters = Largest(NotesChapters().Matches(segment));
            if (OmnibusEdition().IsMatch(segment))
            {
                omnibusVolumes |= segmentVolumes is not null || OmnibusCount().IsMatch(segment);
                continue;
            }
            var status = PublisherStatusWord(segment);
            anyStatus ??= status;
            if (segmentVolumes is { } v && (volumes is null || v > volumes))
            {
                volumes = v;
                volumeStatus = status;
            }
            if (segmentChapters is { } c && (chapters is null || c > chapters))
            {
                chapters = c;
                chapterStatus = status;
            }
        }
        var edition = volumeStatus ?? (volumes is null ? chapterStatus : null) ?? (volumes is null && chapters is null ? anyStatus : null);
        return new PublisherEdition(volumes, chapters, edition, omnibusVolumes && volumes is null);
    }

    /// <summary>A publisher's regular edition as its notes state it (<see cref="ParsePublisherEdition"/>).</summary>
    public sealed record PublisherEdition(int? Volumes, int? Chapters, MetadataOriginStatus? Status, bool Omnibus)
    {
        public static PublisherEdition Empty { get; } = new(null, null, null, false);
    }

    // An edition whose volume numbering is not the original's.
    [GeneratedRegex(@"omnibus|\b\d\s*-?\s*in\s*-?\s*1\b|perfect\s+edition|deluxe|big\s+edition", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OmnibusEdition();

    // "14 Omnibus", "9 Physical Perfect Edition Omnibuses": an omnibus count the volume pattern does not read.
    [GeneratedRegex(@"\d{1,5}\s*(?:[\p{L}-]+\s+){0,3}omnibus", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OmnibusCount();

    [GeneratedRegex(@"\p{L}+", RegexOptions.CultureInvariant)]
    private static partial Regex Word();

    // The first status word of a notes segment ("Ongoing", "Complete(d)", "Hiatus", "Cancelled", "Defunct", "Dropped").
    private static MetadataOriginStatus? PublisherStatusWord(string segment)
    {
        foreach (Match m in Word().Matches(segment))
        {
            var w = m.Value.ToLowerInvariant();
            if (w is "defunct" or "dropped")
                return MetadataOriginStatus.Cancelled;
            if (MapStatusWord(w) is { } status)
                return status;
        }
        return null;
    }

    private static int? Largest(MatchCollection matches)
    {
        int? best = null;
        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n))
                continue;
            if (m.Groups[2].Success && int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var extra))
                n += extra;
            if (n > 0 && (best is null || n > best))
                best = n;
        }
        return best;
    }

    private static MetadataOriginStatus? MapStatusWord(string word)
    {
        var w = word.Trim().ToLowerInvariant();
        if (w.Contains("ongoing", StringComparison.Ordinal))
            return MetadataOriginStatus.Ongoing;
        if (w.Contains("hiatus", StringComparison.Ordinal))
            return MetadataOriginStatus.Hiatus;
        if (w.Contains("cancel", StringComparison.Ordinal) || w.Contains("discontinued", StringComparison.Ordinal)
            || w.Contains("axed", StringComparison.Ordinal))
            return MetadataOriginStatus.Cancelled;
        if (w.Contains("complete", StringComparison.Ordinal) || w.Contains("finished", StringComparison.Ordinal))
            return MetadataOriginStatus.Complete;
        return null;
    }
}
