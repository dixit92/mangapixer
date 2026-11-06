namespace com.lifepixer.mangapixer.Server.Features.Catalog;

/// <summary>
/// 1.51.0 lane F (owner decision 2): the chips of the Favorites jump rail - a pure function from the favorites list under the name sort,
/// in DISPLAY order (A-Z or Z-A), to its spans. Letters only, from each entry's own <c>SortKey</c> (its first character as it sorts, by
/// browse's <see cref="JumpIndexBuilder.LetterLabelFor"/>; a list whose names mostly open with a tag gets <c>[A</c> ... as a doujin level
/// does); no number ranges and no common-prefix stripping - a favorites list spans series, its names share nothing.
/// <para>
/// Folders (and stacks, which are their folder's card) sort before archives under A-Z (the key's kind prefix) and after them under Z-A, so
/// the kinds are two runs; as in browse's rail the smaller kind gets one <see cref="JumpIndexBuilder.FoldersLabel"/> /
/// <see cref="JumpIndexBuilder.FilesLabel"/> chip, or - from <see cref="JumpIndexBuilder.OwnRailMinRows"/> rows - its own letters, every
/// chip then naming its kind (<c>Group</c>). Invariant: every span is one contiguous run of the list, so a chip's cursor (the entry just
/// before its first entry) lands on its first entry and the next <c>Count</c> entries are exactly its entries.
/// </para>
/// </summary>
internal static class FavoriteJumpIndex
{
    /// <summary>One entry as the rail reads it: its sort key and whether it is a folder (a starred folder or a stack).</summary>
    public readonly record struct Row(string SortKey, bool IsFolder);

    /// <summary>One chip: entries <c>[Start, Start + Count)</c> of the display-ordered list.</summary>
    public readonly record struct Span(int Start, int Count, string Label, string? Group);

    /// <summary>The chips of <paramref name="rows"/> (display order), in display order.</summary>
    public static List<Span> Build(IReadOnlyList<Row> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var spans = new List<Span>();
        if (rows.Count == 0)
            return spans;

        // The kind runs in display order (the key's kind prefix keeps each kind together; defensively, more than two runs = one segment).
        var runs = new List<(int Start, int Count, bool IsFolder)>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (runs.Count > 0 && runs[^1].IsFolder == rows[i].IsFolder)
                runs[^1] = (runs[^1].Start, runs[^1].Count + 1, runs[^1].IsFolder);
            else
                runs.Add((i, 1, rows[i].IsFolder));
        }

        if (runs.Count != 2)
        {
            Letters(rows, 0, rows.Count, null, spans);
            return spans;
        }

        var folders = runs[0].IsFolder ? runs[0] : runs[1];
        var files = runs[0].IsFolder ? runs[1] : runs[0];
        // The bigger kind carries the letters; a tie goes to the archives (browse's rule).
        var mainIsFiles = files.Count >= folders.Count;
        var minor = mainIsFiles ? folders : files;
        foreach (var run in runs)
        {
            if (minor.Count >= JumpIndexBuilder.OwnRailMinRows)
                Letters(rows, run.Start, run.Count, run.IsFolder ? JumpIndexBuilder.FoldersGroup : JumpIndexBuilder.FilesGroup, spans);
            else if (run.Start == minor.Start)
                spans.Add(new Span(run.Start, run.Count, run.IsFolder ? JumpIndexBuilder.FoldersLabel : JumpIndexBuilder.FilesLabel, null));
            else
                Letters(rows, run.Start, run.Count, null, spans);
        }
        return spans;
    }

    /// <summary>The name part of a catalog sort key: after the kind prefix, up to the case tie-break separator (U+0001).</summary>
    internal static string NameOf(string sortKey)
    {
        if (string.IsNullOrEmpty(sortKey))
            return string.Empty;
        var name = sortKey[1..];
        var cut = name.IndexOf('\u0001', StringComparison.Ordinal);
        return cut >= 0 ? name[..cut] : name;
    }

    /// <summary>
    /// The letter chips of rows <c>[start, start + count)</c>: runs of one label; a label found in more than one run keeps its biggest run as
    /// the chip and its other runs join the chip before them (the first chip when none precedes), so every chip stays contiguous - browse's
    /// letter rule.
    /// </summary>
    private static void Letters(IReadOnlyList<Row> rows, int start, int count, string? group, List<Span> spans)
    {
        var names = new string[count];
        for (var i = 0; i < count; i++)
            names[i] = NameOf(rows[start + i].SortKey);
        var labels = JumpIndexBuilder.LetterLabels(names);

        var runs = new List<(string Label, int Start, int Count)>();
        for (var i = 0; i < labels.Length; i++)
        {
            if (runs.Count > 0 && runs[^1].Label == labels[i])
                runs[^1] = (labels[i], runs[^1].Start, runs[^1].Count + 1);
            else
                runs.Add((labels[i], i, 1));
        }
        var keeper = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var r = 0; r < runs.Count; r++)
        {
            if (!keeper.TryGetValue(runs[r].Label, out var k) || runs[r].Count > runs[k].Count)
                keeper[runs[r].Label] = r;
        }
        var first = spans.Count;
        var pending = 0;
        for (var r = 0; r < runs.Count; r++)
        {
            var run = runs[r];
            if (keeper[run.Label] == r)
            {
                spans.Add(new Span(start + run.Start - pending, run.Count + pending, run.Label, group));
                pending = 0;
            }
            else if (spans.Count > first)
            {
                spans[^1] = spans[^1] with { Count = spans[^1].Count + run.Count };
            }
            else
            {
                pending += run.Count;
            }
        }
    }
}
