namespace com.lifepixer.mangapixer.Server.Features.WhatsNew;

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Updates;

/// <summary>
/// Reads the server's <c>CHANGELOG.md</c> (Keep a Changelog) into release-note sections for "What's new" (1.53.0). Pure: no I/O, no clock.
///
/// <list type="bullet">
/// <item>A section is <c>## [x.y.z] - yyyy-MM-dd</c> (the date may be left out). <c>## [Unreleased]</c> and everything under it is
/// skipped. Text before the first section (the title and the introduction) is ignored.</item>
/// <item>A group is <c>### Name</c> inside a section; an item is a <c>- </c> bullet at the start of a line, and every following indented,
/// non-empty line continues it (joined with one space). A blank line ends an item. Other paragraph text inside a group is ignored.</item>
/// <item>Inline formatting kept: <c>`code`</c>, <c>**bold**</c> and <c>[text](target)</c> - a link whose target is an app path (one
/// leading <c>/</c>, not <c>//</c>) keeps its target; any other link keeps only its text. An unclosed marker stays literal text. No HTML
/// is interpreted: every part is plain text for the client to render as text.</item>
/// <item>The file is all or nothing: a <c>##</c> heading that is not a version, a version heading twice, a group or bullet outside a
/// section, a bullet before a section's first group, an empty group name or a deeper heading makes <see cref="Parse"/> return no
/// sections with an error code - never an exception - so a broken file shows nothing rather than half the notes.</item>
/// </list>
/// </summary>
public static partial class ChangelogParser
{
    /// <summary>The outcome: the released sections in file order, or none with a short error code (for the log; never content).</summary>
    public sealed record Result(IReadOnlyList<WhatsNewSectionDto> Sections, string? Error)
    {
        public static Result Failed(string error) => new([], error);
    }

    [GeneratedRegex(@"^##\s+\[(?<v>[^\]]+)\](?:\s+-\s+(?<d>\S+))?\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex SectionHeading();

    public static Result Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Result.Failed("empty");

        var sections = new List<WhatsNewSectionDto>();
        var seen = new HashSet<(int, int, int)>();

        // The open section / group / item while reading.
        string? version = null;
        string? date = null;
        List<WhatsNewGroupDto>? groups = null;
        string? groupName = null;
        List<WhatsNewItemDto>? items = null;
        StringBuilder? item = null;
        var inSection = false;   // inside any "## " section (released or Unreleased)
        var skipping = false;    // inside [Unreleased]

        void EndItem()
        {
            if (item is not null && items is not null)
                items.Add(new WhatsNewItemDto { Parts = ParseInline(item.ToString()) });
            item = null;
        }

        void EndGroup()
        {
            EndItem();
            if (groupName is not null && groups is not null && items is not null)
                groups.Add(new WhatsNewGroupDto { Name = groupName, Items = items });
            groupName = null;
            items = null;
        }

        void EndSection()
        {
            EndGroup();
            if (version is not null && groups is not null)
                sections.Add(new WhatsNewSectionDto { Version = version, Date = date, Groups = groups });
            version = null;
            date = null;
            groups = null;
        }

        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();

            if (line.StartsWith("## ", StringComparison.Ordinal) || line == "##")
            {
                EndSection();
                inSection = true;
                var m = SectionHeading().Match(line);
                if (!m.Success)
                    return Result.Failed("section_heading");
                var label = m.Groups["v"].Value.Trim();
                if (string.Equals(label, "Unreleased", StringComparison.OrdinalIgnoreCase))
                {
                    skipping = true;
                    continue;
                }

                skipping = false;
                var parsed = UpdateVersion.TryParse(label);
                if (parsed is null || label.StartsWith('v') || label.StartsWith('V'))
                    return Result.Failed("section_version");
                if (!seen.Add((parsed.Value.Major, parsed.Value.Minor, parsed.Value.Patch)))
                    return Result.Failed("duplicate_version");
                var d = m.Groups["d"];
                if (d.Success && !DateOnly.TryParseExact(d.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
                    return Result.Failed("section_date");

                version = label;
                date = d.Success ? d.Value : null;
                groups = [];
                continue;
            }

            if (skipping)
                continue;

            if (line.StartsWith("### ", StringComparison.Ordinal) || line == "###")
            {
                if (!inSection)
                    return Result.Failed("group_outside_section");
                EndGroup();
                var name = line[3..].Trim();
                if (name.Length == 0)
                    return Result.Failed("group_name");
                groupName = name;
                items = [];
                continue;
            }

            if (line.StartsWith('#'))
            {
                // "# Changelog" (the title) before the first section is fine; any other heading level is not part of the format.
                if (!inSection && line.StartsWith("# ", StringComparison.Ordinal))
                    continue;
                return Result.Failed("heading");
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                if (!inSection)
                    return Result.Failed("item_outside_section");
                if (groupName is null)
                    return Result.Failed("item_outside_group");
                EndItem();
                item = new StringBuilder(line[2..].Trim());
                continue;
            }

            if (line.Length == 0)
            {
                EndItem();
                continue;
            }

            if (item is not null && char.IsWhiteSpace(rawLine[0]))
            {
                item.Append(' ').Append(line.Trim());
                continue;
            }

            // Any other paragraph text: an unbulleted line ends the item and is not shown.
            EndItem();
        }

        EndSection();
        return new Result(sections, null);
    }

    /// <summary>
    /// Splits one item's text into parts: <c>`code`</c> (literal inside), <c>**bold**</c> (toggles; an unpaired one stays literal) and
    /// <c>[text](target)</c> links (an app-path target kept, any other dropped). Adjacent plain runs with the same flags are merged.
    /// </summary>
    public static IReadOnlyList<WhatsNewInlineDto> ParseInline(string text)
    {
        var parts = new List<WhatsNewInlineDto>();
        var plain = new StringBuilder();
        var bold = false;
        // A "**" toggles bold only when a partner follows; count the pairs so a lone one stays literal.
        var boldMarkers = CountBoldMarkers(text);
        var boldUsed = 0;

        void Flush()
        {
            if (plain.Length > 0)
                Add(parts, new WhatsNewInlineDto { Text = plain.ToString(), Bold = bold });
            plain.Clear();
        }

        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                {
                    Flush();
                    Add(parts, new WhatsNewInlineDto { Text = text[(i + 1)..close], Code = true, Bold = bold });
                    i = close + 1;
                    continue;
                }
            }
            else if (c == '*' && i + 1 < text.Length && text[i + 1] == '*' && (bold || boldMarkers - boldUsed >= 2))
            {
                Flush();
                bold = !bold;
                boldUsed++;
                i += 2;
                continue;
            }
            else if (c == '[')
            {
                var endText = text.IndexOf(']', i + 1);
                if (endText > i + 1 && endText + 1 < text.Length && text[endText + 1] == '(')
                {
                    var endTarget = text.IndexOf(')', endText + 2);
                    if (endTarget > endText + 1)
                    {
                        var linkText = StripMarkers(text[(i + 1)..endText]);
                        var target = text[(endText + 2)..endTarget].Trim();
                        Flush();
                        Add(parts, new WhatsNewInlineDto { Text = linkText, Bold = bold, Link = IsAppPath(target) ? target : null });
                        i = endTarget + 1;
                        continue;
                    }
                }
            }

            plain.Append(c);
            i++;
        }

        Flush();
        return parts;
    }

    /// <summary>True for an in-app path: one leading <c>/</c> (never <c>//host</c> or <c>/\host</c>), no whitespace or control characters.</summary>
    public static bool IsAppPath(string target)
    {
        if (target.Length == 0 || target[0] != '/')
            return false;
        if (target.Length > 1 && (target[1] == '/' || target[1] == '\\'))
            return false;
        foreach (var ch in target)
        {
            if (char.IsWhiteSpace(ch) || char.IsControl(ch) || ch == '\\')
                return false;
        }
        return true;
    }

    private static int CountBoldMarkers(string text)
    {
        // Mirrors ParseInline's scan: a closed code span is skipped whole (its asterisks are literal).
        var count = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '`')
            {
                var close = text.IndexOf('`', i + 1);
                if (close > i + 1)
                    i = close;
            }
            else if (text[i] == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                count++;
                i++;
            }
        }
        return count;
    }

    private static string StripMarkers(string text) =>
        text.Replace("**", string.Empty, StringComparison.Ordinal).Replace("`", string.Empty, StringComparison.Ordinal);

    private static void Add(List<WhatsNewInlineDto> parts, WhatsNewInlineDto part)
    {
        if (part.Text.Length == 0)
            return;
        if (parts.Count > 0)
        {
            var last = parts[^1];
            if (!last.Code && !part.Code && last.Link is null && part.Link is null && last.Bold == part.Bold)
            {
                parts[^1] = last with { Text = last.Text + part.Text };
                return;
            }
        }
        parts.Add(part);
    }
}
