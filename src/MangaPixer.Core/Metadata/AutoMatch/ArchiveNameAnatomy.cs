namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The doujin naming anatomy of an archive name (metadata stage 2):
/// <c>(event) [circle (artist)] title (parody) [language] ...</c>. Every part is optional
/// except the title. Pure; sees a display name only.
/// </summary>
/// <param name="Event">The leading <c>(event)</c> group (a convention name), or null.</param>
/// <param name="LeadingTag">The leading <c>[...]</c> group as written, or null.</param>
/// <param name="Circle">The circle of a <c>[circle (artist)]</c> tag, or null.</param>
/// <param name="Artist">The artist of a <c>[circle (artist)]</c> tag, or null.</param>
/// <param name="Title">The clean title (<see cref="TitleNormalizer.Normalize"/> primary).</param>
/// <param name="Parody">The first trailing <c>(...)</c> group that is not a year or a release tag, or null.</param>
/// <param name="HasUnitToken">The name carries a volume / chapter token (a series unit, not a one-shot).</param>
public sealed partial record ArchiveNameAnatomy(
    string? Event,
    string? LeadingTag,
    string? Circle,
    string? Artist,
    string Title,
    string? Parody,
    bool HasUnitToken)
{
    [GeneratedRegex(@"^\s*(?:\((?<event>[^()\[\]]{1,80})\)\s*)?\[(?<tag>[^\[\]]{1,160})\]\s*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex LeadingGroups();

    [GeneratedRegex(@"^(?<circle>.+?)\s*\((?<artist>[^()]+)\)\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CircleArtist();

    [GeneratedRegex(@"\(([^()]{1,120})\)", RegexOptions.CultureInvariant)]
    private static partial Regex ParenGroup();

    // Parenthesized release tags that are never a parody: years, languages, quality, edition and
    // unit markers, scan notes; 1.32.0 comics scan tags: (c2c) cover-to-cover, (Zone-Empire) / (<Group>-Empire) scanners,
    // (Webrip), and collected-format words (TPB, HC, GN, OGN).
    [GeneratedRegex(@"^(?:(?:19|20)\d{2}.*|\d+(?:\s*-\s*\d+)?|c2c|[\p{L}\p{N} ]{1,40}-empire|tpb|hc|gn|ogn|digital|english|eng|en|japanese|jp|raw|decensored|uncensored|censored|colou?r(?:ed|ized)?|full\s*colou?r|hd|hq|lq|web|webrip|scan(?:ned)?|translated|complete|ongoing|one-?shot|x\d+|\d{3,4}p|v\d+|(?:ch|vol)\.?\s*\d+.*)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReleaseTag();

    /// <summary>A parenthesized release tag (year, language, quality, edition or unit marker, scan note).</summary>
    public static bool IsReleaseTag(string? text) => !string.IsNullOrWhiteSpace(text) && ReleaseTag().IsMatch(text.Trim());

    [GeneratedRegex(@"(?<![\p{L}\p{N}])(?:v|vol|vols|volume|volumes|ch|chap|chapter|chapters|c)\.?\s*\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UnitToken();

    [GeneratedRegex(@"\s*(?:,|&|、|/)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NameSeparator();

    /// <summary>
    /// Looks like a doujin: an <c>(event)</c> prefix, a <c>[circle (artist)]</c> tag, or a leading
    /// tag plus a parody group on a name without volume / chapter tokens.
    /// </summary>
    public bool IsDoujinShaped =>
        Event is not null
        || (Circle is not null && Artist is not null)
        || (LeadingTag is not null && Parody is not null && !HasUnitToken);

    /// <summary>
    /// Creator names the leading tag carries: circle and artist of <c>[circle (artist)]</c>, or the
    /// whole tag otherwise (a lone tag may be an artist, a circle or a scan group - callers only
    /// ever compare it with a folder name or record authors). Multi-name tags are split on
    /// <c>,</c> <c>&amp;</c> <c>/</c>.
    /// </summary>
    public IReadOnlyList<string> CreatorTags
    {
        get
        {
            var names = new List<string>();
            foreach (var part in new[] { Circle, Artist, Circle is null ? LeadingTag : null })
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                foreach (var n in NameSeparator().Split(part))
                {
                    var t = n.Trim();
                    if (t.Length > 0 && !names.Contains(t, StringComparer.OrdinalIgnoreCase))
                        names.Add(t);
                }
            }
            return names;
        }
    }

    /// <summary>Parses one archive display name. Never throws; an empty name yields an empty title.</summary>
    public static ArchiveNameAnatomy Parse(string? archiveName)
    {
        var title = TitleNormalizer.Normalize(archiveName).Primary;
        if (string.IsNullOrWhiteSpace(archiveName))
            return new ArchiveNameAnatomy(null, null, null, null, title, null, false);

        var name = archiveName.Normalize(NormalizationForm.FormKC).Trim();
        var hasUnit = UnitToken().IsMatch(name);

        string? evt = null, tag = null, circle = null, artist = null, parody = null;
        var rest = name;
        var lead = LeadingGroups().Match(name);
        if (lead.Success)
        {
            evt = lead.Groups["event"].Success ? lead.Groups["event"].Value.Trim() : null;
            tag = lead.Groups["tag"].Value.Trim();
            rest = lead.Groups["rest"].Value;
            var ca = CircleArtist().Match(tag);
            if (ca.Success)
            {
                circle = ca.Groups["circle"].Value.Trim();
                artist = ca.Groups["artist"].Value.Trim();
            }
            if (tag.Length == 0) tag = null;
        }

        // The parody is the first non-tag paren group AFTER some title text.
        foreach (Match m in ParenGroup().Matches(rest))
        {
            if (m.Index == 0 || string.IsNullOrWhiteSpace(rest[..m.Index].Replace("[", " ", StringComparison.Ordinal).Replace("]", " ", StringComparison.Ordinal)))
                continue;
            var inner = m.Groups[1].Value.Trim();
            if (inner.Length == 0 || ReleaseTag().IsMatch(inner) || !inner.Any(char.IsLetter))
                continue;
            parody = inner;
            break;
        }

        return new ArchiveNameAnatomy(evt, tag, circle, artist, title, parody, hasUnit);
    }
}
