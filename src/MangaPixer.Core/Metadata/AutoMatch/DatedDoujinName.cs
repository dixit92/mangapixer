namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The dated doujin naming of some downloaders (1.34.2): <c>[Creator] [yyyy-mm] Character (Tag) (Title)</c>, the creator tag
/// often without its opening bracket (<c>Creator] [yyyy-mm] ...</c>, see <see cref="TitleNormalizer.SplitUnmatchedBracketTags"/>).
/// Gated on a bracketed date - <c>[yyyy]</c>, <c>[yyyy-mm]</c> or <c>[yyyy-mm-dd]</c> - RIGHT AFTER the creator tag; the common
/// convention <c>[Creator] Title (Parody)</c> has no such date and is read by <see cref="ArchiveNameAnatomy"/> as before.
/// <para>
/// After the date, the LAST parenthesized group that is not a release tag (<see cref="ArchiveNameAnatomy.IsReleaseTag"/>:
/// <c>(x1600)</c>, <c>(Decensored)</c>, ...) is the title, with any text after it (<c>(Title) - Part 2</c>); every other such
/// group is a tag; the text outside the groups is the character the work is about. A name with no such group (only the
/// character, or only release tags) has no <see cref="Title"/>: it is read as before. Square groups after the date are tags.
/// </para>
/// Pure; sees a display name only.
/// </summary>
/// <param name="Creator">The creator tag as written (circle, artist or both), without its brackets.</param>
/// <param name="Title">The title (the last title group, plus any text after it), or null when the name carries none.</param>
/// <param name="Character">The text outside every group (who the work is about), or null when it has no letters.</param>
/// <param name="Tags">The other parenthesized groups after the date, in name order.</param>
public sealed partial record DatedDoujinName(string Creator, string? Title, string? Character, IReadOnlyList<string> Tags)
{
    // An optional (event), the creator tag with or without its "[", then the date group.
    [GeneratedRegex(@"^\s*(?:\([^()\[\]]{1,80}\)\s*)?\[?(?<creator>[^\[\]]{1,160}?)\s*\]\s*\[(?<date>(?:19|20)\d{2}(?:-(?:0[1-9]|1[0-2])(?:-(?:0[1-9]|[12]\d|3[01]))?)?)\]\s*(?<rest>.*)$",
        RegexOptions.CultureInvariant)]
    private static partial Regex DatedLead();

    [GeneratedRegex(@"\[[^\[\]]*\]", RegexOptions.CultureInvariant)]
    private static partial Regex SquareGroup();

    [GeneratedRegex(@"^\s*[-\u2013\u2014~:]", RegexOptions.CultureInvariant)]
    private static partial Regex TailSeparator();

    [GeneratedRegex(@"[\[\](){}]", RegexOptions.CultureInvariant)]
    private static partial Regex StrayBracket();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    /// <summary>
    /// Parses a display name (an archive file name with or without its extension), or returns null when it does not have the
    /// dated shape. Never throws.
    /// </summary>
    public static DatedDoujinName? TryParse(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return null;
        var name = TitleNormalizer.StripArchiveExtension(displayName.Normalize(NormalizationForm.FormKC).Trim());
        var m = DatedLead().Match(name);
        if (!m.Success)
            return null;
        var creator = m.Groups["creator"].Value.Trim().TrimStart('[').Trim();
        if (!creator.Any(char.IsLetter))
            return null;

        var rest = m.Groups["rest"].Value;
        var groups = TopLevelParenGroups(rest);
        var titled = groups.Where(g => IsTitleText(g.Inner)).ToList();

        string? title = null;
        if (titled.Count > 0)
        {
            var last = titled[^1];
            // Text after the title group belongs to it ("(Title) - Part 2"); release tags and square tags after it do not.
            // A separator in front of it is kept, so the subtitle split still finds the title alone ("Title - Part 2").
            var after = rest[(last.End + 1)..];
            var tail = OutsideGroups(after);
            var join = TailSeparator().IsMatch(after) ? " - " : " ";
            title = Flatten(last.Inner) is { Length: > 0 } t
                ? (tail.Length > 0 ? t + join + tail : t)
                : null;
        }

        var outside = OutsideGroups(titled.Count > 0 ? rest[..titled[^1].Start] : rest);
        var tags = titled.Take(titled.Count - 1).Select(g => Flatten(g.Inner)).Where(t => t.Length > 0).ToList();
        return new DatedDoujinName(creator, title, outside.Any(char.IsLetter) ? outside : null, tags);
    }

    /// <summary>A group that can be a title or a tag: has letters and is no release tag.</summary>
    private static bool IsTitleText(string inner) =>
        inner.Any(char.IsLetter) && !ArchiveNameAnatomy.IsReleaseTag(inner);

    /// <summary>Top-level <c>(...)</c> groups of a text (a nested group stays inside its parent), with their bounds.</summary>
    private static List<(int Start, int End, string Inner)> TopLevelParenGroups(string s)
    {
        var groups = new List<(int, int, string)>();
        var depth = 0;
        var start = -1;
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '(')
            {
                if (depth++ == 0)
                    start = i;
            }
            else if (s[i] == ')' && depth > 0 && --depth == 0)
            {
                groups.Add((start, i, s[(start + 1)..i].Trim()));
            }
        }
        return groups;
    }

    /// <summary>The text outside every group: square and top-level <c>(...)</c> groups removed, then <see cref="Flatten"/>ed.</summary>
    private static string OutsideGroups(string s)
    {
        var sb = new StringBuilder(s.Length);
        var depth = 0;
        foreach (var ch in SquareGroup().Replace(s, " "))
        {
            if (ch == '(')
                depth++;
            else if (ch == ')' && depth > 0)
                depth--;
            else if (depth == 0)
                sb.Append(ch);
        }
        return Flatten(sb.ToString());
    }

    /// <summary>A group's text as words: square groups removed, other brackets (a nested <c>(Part 2)</c>) kept as their words.</summary>
    private static string Flatten(string s)
    {
        s = SquareGroup().Replace(s, " ");
        s = StrayBracket().Replace(s, " ");
        return Whitespace().Replace(s, " ").Trim().Trim(' ', '-', '_', ',', ';', '|', '/', '+', '=', '~').Trim();
    }
}
