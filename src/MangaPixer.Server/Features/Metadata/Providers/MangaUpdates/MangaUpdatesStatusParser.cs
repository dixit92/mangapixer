namespace com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;

using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Metadata;

/// <summary>
/// Reads MangaUpdates' free-text <c>status</c> ("43 Volumes (Ongoing)", "200
/// Chapters + Prologue (Complete)  \n15 Volumes (Complete)", often followed by a
/// Markdown note) into the volume count and the publication status in the
/// country of origin. Tolerant: anything it cannot read stays null / Unknown, and
/// the flattened text is kept for display.
/// </summary>
public static partial class MangaUpdatesStatusParser
{
    public const int MaxStatusTextLength = 1024;

    [GeneratedRegex(@"(\d{1,5})\s*(?:Volumes?|Vols?\.?)\b[^()\n]*\(([^()\n]{1,40})\)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VolumeLine();

    [GeneratedRegex(@"\(([^()\n]{1,40})\)", RegexOptions.CultureInvariant)]
    private static partial Regex AnyParenthesis();

    public sealed record Result(int? Volumes, MetadataOriginStatus? Status, string? Text);

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

        return new Result(volumes, origin, text);
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
