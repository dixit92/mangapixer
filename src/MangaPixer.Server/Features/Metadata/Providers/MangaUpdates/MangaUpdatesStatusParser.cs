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

    [GeneratedRegex(@"(?<![\p{L}\p{N}.])(\d{1,5})(?:\.\d+)?\s*chapters?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesChapters();

    [GeneratedRegex(@"(?<![\p{L}\p{N}.])(\d{1,5})(?:\.\d+)?\s*(?:volumes?|vols?)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NotesVolumes();

    /// <summary>
    /// The volume and chapter totals of a publisher's <c>notes</c> ("10 Volumes / 60 Chapters; Ongoing",
    /// "86 Chapters; Ongoing", "12 Volumes (Ongoing)"; 1.27.0). The largest stated number of each, or null.
    /// </summary>
    public static (int? Volumes, int? Chapters) ParsePublisherNotes(string? notes)
    {
        var text = MetadataText.Flatten(notes, MaxStatusTextLength);
        if (text is null)
            return (null, null);
        return (Largest(NotesVolumes().Matches(text)), Largest(NotesChapters().Matches(text)));
    }

    private static int? Largest(MatchCollection matches)
    {
        int? best = null;
        foreach (Match m in matches)
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0 && (best is null || n > best))
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
