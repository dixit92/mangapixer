namespace com.lifepixer.mangapixer.Core.Reading;

/// <summary>
/// The rows of the Home page and the user's order of them (1.54.0, owner decision 3: per user, synced; reordered in Settings).
/// The stable ids are what the <c>library-preferences</c> blob carries; the web client maps each id to its row.
///
/// One rule decides what an order means, on write AND on read: unknown and repeated ids are dropped, and the ids the order does not
/// name follow in the default order. So a stored order stays valid when a release adds a row (the new row appears at the end of the
/// user's order) or drops one, and a client can never store a list the Home page cannot draw. The default order is today's Home.
/// </summary>
public static class HomeRows
{
    public const string ContinueReading = "continue";
    public const string Recommended = "recommended";
    public const string Favorites = "favorites";
    public const string NewChapters = "recent";
    public const string Libraries = "libraries";

    /// <summary>The order of a user who never reordered Home (the order Home had before 1.54.0).</summary>
    public static IReadOnlyList<string> DefaultOrder { get; } = [ContinueReading, Recommended, Favorites, NewChapters, Libraries];

    /// <summary>
    /// The full order <paramref name="order"/> stands for: its known ids in its order (each once), then the missing ones in the default
    /// order. Null or empty = the default order.
    /// </summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string?>? order)
    {
        var result = new List<string>(DefaultOrder.Count);
        foreach (var id in order ?? [])
        {
            if (id is not null && DefaultOrder.Contains(id, StringComparer.Ordinal) && !result.Contains(id, StringComparer.Ordinal))
                result.Add(id);
        }
        foreach (var id in DefaultOrder)
        {
            if (!result.Contains(id, StringComparer.Ordinal))
                result.Add(id);
        }
        return result;
    }

    /// <summary>The column value for <paramref name="order"/>: null for the default order, else the normalized ids joined by commas.</summary>
    public static string? ToStored(IEnumerable<string?>? order)
    {
        var normalized = Normalize(order);
        return normalized.SequenceEqual(DefaultOrder, StringComparer.Ordinal) ? null : string.Join(',', normalized);
    }

    /// <summary>The full order a stored column value stands for (null = the default order).</summary>
    public static IReadOnlyList<string> FromStored(string? stored) => Normalize(stored?.Split(','));
}
