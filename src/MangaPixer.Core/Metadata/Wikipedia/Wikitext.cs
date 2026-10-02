namespace com.lifepixer.mangapixer.Core.Metadata.Wikipedia;

using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// The few wikitext primitives the chapter-list parser needs (1.32.0): balanced templates, their top-level parameters, and
/// reference / comment stripping. Pure and bounded (a page is capped by the transport at 2 MB); no regex backtracking hazards
/// (every pattern is anchored or linear and has a match timeout).
/// </summary>
internal static class Wikitext
{
    private static readonly TimeSpan s_timeout = TimeSpan.FromSeconds(2);

    private static Regex Make(string pattern, RegexOptions extra = RegexOptions.None) =>
        new(pattern, RegexOptions.CultureInvariant | extra, s_timeout);

    private static readonly Regex s_selfClosingRef = Make(@"<ref[^>/]*/>", RegexOptions.IgnoreCase);
    private static readonly Regex s_ref = Make(@"<ref[^>]*>.*?</ref>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex s_comment = Make(@"<!--.*?-->", RegexOptions.Singleline);
    private static readonly Regex s_named = Make(@"^\s*([A-Za-z_][A-Za-z0-9_ ]*?)\s*=(.*)\z", RegexOptions.Singleline);

    /// <summary>A template found in the text: its position, extent and the text between the outer braces.</summary>
    public readonly record struct Template(int Start, int End, string Body);

    /// <summary>
    /// Every template whose name matches <paramref name="nameRegex"/> (case-insensitive, ending at <c>|</c>, <c>}</c> or a
    /// line break, so <c>Graphic novel list</c> does not match <c>Graphic novel list/header</c>), balanced over nested braces.
    /// </summary>
    public static List<Template> FindTemplates(string text, string nameRegex)
    {
        var found = new List<Template>();
        var opener = Make(@"\{\{\s*(?:" + nameRegex + @")\s*(?=[|}\r\n])", RegexOptions.IgnoreCase);
        foreach (Match m in opener.Matches(text))
        {
            var i = m.Index;
            if (found.Count > 0 && i < found[^1].End)
                continue; // nested inside the previous match: not a top-level hit
            var depth = 0;
            var j = i;
            while (j < text.Length)
            {
                if (string.CompareOrdinal(text, j, "{{", 0, 2) == 0)
                {
                    depth++;
                    j += 2;
                    continue;
                }
                if (string.CompareOrdinal(text, j, "}}", 0, 2) == 0)
                {
                    depth--;
                    j += 2;
                    if (depth == 0)
                        break;
                    continue;
                }
                j++;
            }
            if (depth != 0)
                continue; // unbalanced: skip rather than swallow the rest of the page
            found.Add(new Template(i, j, text.Substring(i + 2, j - i - 4)));
        }
        return found;
    }

    /// <summary>Splits a template body at its top-level pipes (pipes inside <c>{{ }}</c> and <c>[[ ]]</c> do not split).</summary>
    public static List<string> SplitParameters(string body)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        int templates = 0, links = 0, k = 0;
        while (k < body.Length)
        {
            if (k + 1 < body.Length)
            {
                var two = body.AsSpan(k, 2);
                if (two is "{{") { templates++; current.Append("{{"); k += 2; continue; }
                if (two is "}}") { templates--; current.Append("}}"); k += 2; continue; }
                if (two is "[[") { links++; current.Append("[["); k += 2; continue; }
                if (two is "]]") { links--; current.Append("]]"); k += 2; continue; }
            }
            var c = body[k];
            if (c == '|' && templates == 0 && links == 0)
            {
                parts.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
            k++;
        }
        parts.Add(current.ToString());
        return parts;
    }

    /// <summary>The named parameters (<c>name = value</c>) of a split template; the first part (the template name) is skipped.</summary>
    public static Dictionary<string, string> NamedParameters(IReadOnlyList<string> parts)
    {
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < parts.Count; i++)
        {
            var m = s_named.Match(parts[i]);
            if (m.Success)
                named[m.Groups[1].Value.Trim()] = m.Groups[2].Value.Trim();
        }
        return named;
    }

    /// <summary>The text without <c>&lt;ref&gt;</c> elements and HTML comments, trimmed.</summary>
    public static string StripRefs(string text)
    {
        var s = s_selfClosingRef.Replace(text, string.Empty);
        s = s_ref.Replace(s, string.Empty);
        s = s_comment.Replace(s, string.Empty);
        return s.Trim();
    }
}
