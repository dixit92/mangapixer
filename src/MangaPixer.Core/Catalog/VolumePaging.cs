namespace com.lifepixer.mangapixer.Core.Catalog;

using System.Text;
using System.Text.Json;

/// <summary>One page of the Volumes view (entries in display order).</summary>
public sealed record VolumePage(
    IReadOnlyList<VolumeEntry> Entries,
    bool HasMore,
    string? NextCursor,
    bool HasPrevious,
    string? PrevCursor);

/// <summary>
/// Keyset paging over the in-memory entry list of the Volumes view (P2.4). The cursor is the entry's position tuple
/// (Rank, VolumeKey, SortKey, Id) encoded opaquely with a <c>v:</c> prefix (distinct from the name sort's raw SortKey and
/// the <c>a:</c> / <c>r:</c> cursors). An entry is never split: a stack is one entry, so a page boundary never falls inside it.
/// Mirrors the name sort's forward / <c>before</c> semantics. Pure.
/// </summary>
public static class VolumePaging
{
    public const string CursorPrefix = "v:";

    /// <summary>True when the text is a Volumes-view cursor (so a name cursor is never confused with it).</summary>
    public static bool IsVolumeCursor(string? cursor) => cursor is not null && cursor.StartsWith(CursorPrefix, StringComparison.Ordinal);

    /// <summary>The opaque cursor of an entry.</summary>
    public static string Encode(VolumeEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var json = JsonSerializer.SerializeToUtf8Bytes(new object[] { entry.Rank, entry.VolumeKey, entry.SortKey, entry.Id });
        return CursorPrefix + Convert.ToBase64String(json).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The position tuple of a cursor, or null when the text is not a well-formed Volumes-view cursor.</summary>
    public static (int Rank, string VolumeKey, string SortKey, string Id)? Decode(string? cursor)
    {
        if (!IsVolumeCursor(cursor))
            return null;
        try
        {
            var text = cursor![CursorPrefix.Length..].Replace('-', '+').Replace('_', '/');
            text = text.PadRight(text.Length + ((4 - (text.Length % 4)) % 4), '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(text));
            var a = doc.RootElement;
            if (a.ValueKind != JsonValueKind.Array || a.GetArrayLength() != 4)
                return null;
            return (a[0].GetInt32(), a[1].GetString() ?? string.Empty, a[2].GetString() ?? string.Empty, a[3].GetString() ?? string.Empty);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    private static int CompareTo(VolumeEntry e, (int Rank, string VolumeKey, string SortKey, string Id) t)
    {
        var c = e.Rank.CompareTo(t.Rank);
        if (c != 0) return c;
        c = string.CompareOrdinal(e.VolumeKey, t.VolumeKey);
        if (c != 0) return c;
        c = string.CompareOrdinal(e.SortKey, t.SortKey);
        return c != 0 ? c : string.CompareOrdinal(e.Id, t.Id);
    }

    /// <summary>
    /// One page. <paramref name="ascending"/> is the ascending position order of <see cref="VolumeGrouping.Group"/>;
    /// <paramref name="descending"/> reverses the whole list (Name descending). A <paramref name="cursor"/> continues
    /// forward strictly after that entry; <paramref name="before"/> returns the page immediately before it; a cursor that
    /// is not a Volumes-view cursor is ignored (the page starts at the top).
    /// </summary>
    public static VolumePage Page(
        IReadOnlyList<VolumeEntry> ascending, string? cursor, string? before, int pageSize, bool descending)
    {
        ArgumentNullException.ThrowIfNull(ascending);
        pageSize = Math.Max(1, pageSize);
        IReadOnlyList<VolumeEntry> ordered = descending ? ascending.Reverse().ToList() : ascending;
        // Position of a tuple in display order: -1 before, 0 equal, 1 after (descending flips the comparison).
        int Place(VolumeEntry e, (int Rank, string VolumeKey, string SortKey, string Id) t) => descending ? -CompareTo(e, t) : CompareTo(e, t);

        if (Decode(before) is { } b)
        {
            var preceding = ordered.Where(e => Place(e, b) < 0).ToList();
            var hasPrevious = preceding.Count > pageSize;
            var page = preceding.Skip(Math.Max(0, preceding.Count - pageSize)).ToList();
            // The window the client already holds sits just after this page, so forward continuation always exists.
            return new VolumePage(page, page.Count > 0, page.Count > 0 ? Encode(page[^1]) : null, hasPrevious, hasPrevious && page.Count > 0 ? Encode(page[0]) : null);
        }

        var start = 0;
        var from = Decode(cursor);
        if (from is { } f)
        {
            start = ordered.Count;
            for (var i = 0; i < ordered.Count; i++)
            {
                if (Place(ordered[i], f) > 0)
                {
                    start = i;
                    break;
                }
            }
        }
        var slice = ordered.Skip(start).Take(pageSize + 1).ToList();
        var hasMore = slice.Count > pageSize;
        if (hasMore)
            slice = slice.Take(pageSize).ToList();
        // A page started from a mid-list cursor may have entries before it: report a backward cursor.
        var previous = from is not null && start > 0 && slice.Count > 0;
        return new VolumePage(slice, hasMore, hasMore ? Encode(slice[^1]) : null, previous, previous ? Encode(slice[0]) : null);
    }
}
