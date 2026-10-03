namespace com.lifepixer.mangapixer.Core.Metadata.AutoMatch;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The "Same author" hint of Needs review (1.33.0): the circle / artist names a waiting work's OWN name states, as grouping keys.
/// Display only - nothing here feeds the matcher. Reads the doujin naming anatomy:
/// <list type="bullet">
/// <item>a balanced leading tag, optionally after an <c>(event)</c>: <c>[Circle (Artist)] Title</c> gives both names, a lone
/// <c>[Name] Title</c> gives the name (<see cref="ArchiveNameAnatomy"/>);</item>
/// <item>an unbalanced leading tag (the opening bracket is not in the file name): <c>Circle (Artist)] Title</c>,
/// <c>Name] Title</c>.</item>
/// </list>
/// Multi-name tags split on <c>, &amp; / 、</c>; a release / language / scan tag (<c>English</c>, <c>Digital</c>, ...) is no
/// name. Two names are one author when either of their keys is equal (the equalities of <see cref="AutoMatchText.NamesEqual"/>):
/// <see cref="Key"/> - the scoring form without spaces ("Shin Jinrui" = "ShinJinrui") - or <see cref="OrderKey"/> - its tokens
/// in sorted order ("Family Given" = "Given Family"). One key cannot hold both, so a caller that groups unions on either.
/// Pure; sees display names only.
/// </summary>
public static partial class ReviewAuthorNames
{
    /// <summary>One author name: its two keys (see the class) and the name as written (for the chip).</summary>
    public sealed record Name(string Key, string OrderKey, string Label);

    [GeneratedRegex(@"^\s*(?:\([^()\[\]]{1,80}\)\s*)?(?<tag>[^\[\]]{1,160}?)\s*\]\s*(?<rest>.*)$", RegexOptions.CultureInvariant)]
    private static partial Regex UnbalancedLeading();

    // "Circle (Artist)" - also "Circle (Artist" when the tag lost its closing parenthesis with its bracket.
    [GeneratedRegex(@"^(?<circle>[^()]+?)\s*\((?<artist>[^()]+)\)?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex CircleArtist();

    [GeneratedRegex(@"\s*(?:,|&|、|/|＆)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex NameSeparator();

    /// <summary>The author names a work's own display name (an archive or folder name) states in its leading tag; empty when none.</summary>
    public static IReadOnlyList<Name> FromWorkName(string? displayName)
    {
        if (string.IsNullOrWhiteSpace(displayName))
            return [];
        var name = displayName.Normalize(NormalizationForm.FormKC).Trim();
        var parts = new List<string?>();
        var anatomy = ArchiveNameAnatomy.Parse(name);
        if (anatomy.LeadingTag is not null)
        {
            if (anatomy.Circle is null)
                parts.Add(anatomy.LeadingTag);
            else
                parts.AddRange([anatomy.Circle, anatomy.Artist]);
        }
        else if (UnbalancedLeading().Match(name) is { Success: true } m && m.Groups["rest"].Value.Any(char.IsLetter))
        {
            var tag = m.Groups["tag"].Value.Trim();
            if (CircleArtist().Match(tag) is { Success: true } ca)
                parts.AddRange([ca.Groups["circle"].Value, ca.Groups["artist"].Value]);
            else
                parts.Add(tag);
        }
        return Collect(parts);
    }

    /// <summary>A plain name (an artist folder, a ComicInfo creator) as author names; empty when it is no name.</summary>
    public static IReadOnlyList<Name> FromPlainName(string? name) => Collect([name]);

    /// <summary>The spaceless key of a name: its scoring form without spaces; empty when shorter than 2.</summary>
    public static string Key(string? name)
    {
        var key = TitleNormalizer.ScoringForm(name).Replace(" ", string.Empty, StringComparison.Ordinal);
        return key.Length >= 2 ? key : string.Empty;
    }

    /// <summary>The word-order key of a name: its scoring-form tokens, sorted, joined with one space.</summary>
    public static string OrderKey(string? name) =>
        string.Join(' ', TitleNormalizer.ScoringForm(name).Split(' ', StringSplitOptions.RemoveEmptyEntries).Order(StringComparer.Ordinal));

    private static List<Name> Collect(IEnumerable<string?> parts)
    {
        var names = new List<Name>();
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part))
                continue;
            foreach (var piece in NameSeparator().Split(part))
            {
                var label = piece.Trim();
                if (label.Length == 0 || !label.Any(char.IsLetter) || ArchiveNameAnatomy.IsReleaseTag(label))
                    continue;
                var key = Key(label);
                var orderKey = OrderKey(label);
                if (key.Length > 0 && !names.Any(n => n.Key == key || n.OrderKey == orderKey))
                    names.Add(new Name(key, orderKey, label));
            }
        }
        return names;
    }
}
