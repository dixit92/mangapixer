namespace com.lifepixer.mangapixer.Server.Features.Catalog;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

// 1.50.0 lane D - backward (upward) paging under the date sorts. A jump-rail time chip (JumpIndexBuilder.DateCursorOf) lands browse
// mid-list with a date cursor; the page reports hasPrevious / prevCursor from its first row, and a request with `before` returns the page
// just above it. Mirrors the forward keysets of QueryRecentlyAddedAsync (`a:`) and QueryRecentlyUpdatedAsync (`u:`, nulls-last bucket):
// the same total order, the comparison flipped, ordered the opposite way, `pageSize + 1` taken (the extra row says a further page exists),
// then reversed back to display order. recentlyRead stays offset-based (`r:{n}`): it has no rail, so a window always starts at the top.

public sealed partial class CatalogBrowseService
{
    /// <summary>A parsed <c>a:{kind}:{createdAtBinary}:{id}</c> cursor (recentlyAdded).</summary>
    internal readonly record struct AddedCursor(int Kind, DateTimeOffset CreatedAt, long Id);

    /// <summary>
    /// A parsed <c>u:{hasValue}:{effectiveBinary}:{id}:{sortKey}</c> cursor (recentlyUpdated). <see cref="Effective"/> is null for a row
    /// in the trailing bucket of folders without a descendant archive.
    /// </summary>
    internal readonly record struct UpdatedCursor(DateTimeOffset? Effective, long Id, string SortKey);

    /// <summary>The recentlyAdded cursor, or null when <paramref name="cursor"/> is not one (another sort's cursor, or malformed).</summary>
    internal static AddedCursor? ParseAddedCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || !cursor.StartsWith("a:", StringComparison.Ordinal))
            return null;
        var parts = cursor[2..].Split(':', 3);
        if (parts.Length != 3
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var kind)
            || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var binary)
            || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var id))
            return null;
        return TryFromBinary(binary) is { } created ? new AddedCursor(kind, created, id) : null;
    }

    /// <summary>The recentlyUpdated cursor, or null when <paramref name="cursor"/> is not one (another sort's cursor, or malformed).</summary>
    internal static UpdatedCursor? ParseUpdatedCursor(string? cursor)
    {
        if (string.IsNullOrEmpty(cursor) || !cursor.StartsWith("u:", StringComparison.Ordinal))
            return null;
        var parts = cursor[2..].Split(':', 4); // the SortKey is the tail and may contain ':'
        if (parts.Length != 4
            || !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var hasValue)
            || !long.TryParse(parts[1], CultureInfo.InvariantCulture, out var binary)
            || !long.TryParse(parts[2], CultureInfo.InvariantCulture, out var id))
            return null;
        if (hasValue != 1)
            return new UpdatedCursor(null, id, parts[3]);
        return TryFromBinary(binary) is { } effective ? new UpdatedCursor(effective, id, parts[3]) : null;
    }

    private static DateTimeOffset? TryFromBinary(long binary)
    {
        try
        {
            return new DateTimeOffset(DateTime.FromBinary(binary), TimeSpan.Zero);
        }
        catch (ArgumentException)
        {
            return null; // not a valid binary DateTime - the cursor is ignored, like the forward queries do
        }
    }

    /// <summary>Whether <paramref name="cursor"/> is a date cursor of <paramref name="sort"/> that browse can page backward from.</summary>
    internal static bool IsDateCursorOf(string sort, string? cursor) => sort switch
    {
        "recentlyAdded" => ParseAddedCursor(cursor) is not null,
        "recentlyUpdated" => ParseUpdatedCursor(cursor) is not null,
        _ => false,
    };

    /// <summary>
    /// The rows listed strictly BEFORE <paramref name="cursor"/>'s row under a date sort, in the REVERSE of the display order (nearest
    /// first), or null when the cursor is not a date cursor of <paramref name="sort"/>. <paramref name="direction"/> is the display
    /// direction (browse pins the date sorts to Descending; the recentlyAdded helper honours either, like its forward query).
    /// </summary>
    internal static IQueryable<CatalogNodeEntity>? RowsBeforeDateCursor(
        IQueryable<CatalogNodeEntity> baseQuery, string sort, string? cursor, SortDirection direction)
    {
        const int folder = (int)CatalogNodeKind.Folder;
        if (sort == "recentlyAdded" && ParseAddedCursor(cursor) is { } a)
        {
            // Display (Descending): Kind asc, CreatedAt desc, Id desc. Before = a lower kind, or the same kind and newer, or the same
            // kind and time with a higher id. Ascending reverses the whole tuple, so before flips with it.
            var (ck, cc, ci) = (a.Kind, a.CreatedAt, a.Id);
            return direction == SortDirection.Descending
                ? baseQuery
                    .Where(n => n.Kind < ck
                        || (n.Kind == ck && n.CreatedAt > cc)
                        || (n.Kind == ck && n.CreatedAt == cc && n.Id > ci))
                    .OrderByDescending(n => n.Kind).ThenBy(n => n.CreatedAt).ThenBy(n => n.Id)
                : baseQuery
                    .Where(n => n.Kind > ck
                        || (n.Kind == ck && n.CreatedAt < cc)
                        || (n.Kind == ck && n.CreatedAt == cc && n.Id < ci))
                    .OrderBy(n => n.Kind).ThenByDescending(n => n.CreatedAt).ThenByDescending(n => n.Id);
        }

        if (sort == "recentlyUpdated" && ParseUpdatedCursor(cursor) is { } u)
        {
            // Display: effective desc with the null folders last, then SortKey asc, then Id asc. The reverse order is effective ASC,
            // where SQLite puts the NULLs FIRST - exactly the null bucket reversed ahead of the dated rows reversed.
            var (cs, cid) = (u.SortKey, u.Id);
            IQueryable<CatalogNodeEntity> before;
            if (u.Effective is { } cts)
            {
                // A dated cursor: only dated rows are above it (newer, or the same time and earlier by SortKey, then Id).
                before = baseQuery.Where(n =>
                    (n.Kind == folder ? n.LatestDescendantAddedAt : (DateTimeOffset?)n.CreatedAt) > cts
                    || ((n.Kind == folder ? n.LatestDescendantAddedAt : (DateTimeOffset?)n.CreatedAt) == cts
                        && string.Compare(n.SortKey, cs) < 0)
                    || ((n.Kind == folder ? n.LatestDescendantAddedAt : (DateTimeOffset?)n.CreatedAt) == cts
                        && n.SortKey == cs && n.Id < cid));
            }
            else
            {
                // A cursor in the null bucket: every dated row is above it, and the null folders earlier by SortKey, then Id.
                before = baseQuery.Where(n =>
                    n.Kind != folder || n.LatestDescendantAddedAt != null
                    || string.Compare(n.SortKey, cs) < 0
                    || (n.SortKey == cs && n.Id < cid));
            }
            return before
                .OrderBy(n => n.Kind == folder ? n.LatestDescendantAddedAt : (DateTimeOffset?)n.CreatedAt)
                .ThenByDescending(n => n.SortKey)
                .ThenByDescending(n => n.Id);
        }

        return null;
    }

    /// <summary>
    /// Date sort, BACKWARD: the page immediately before <paramref name="before"/> in the display order (the rows nearest to it, returned
    /// in display order) and whether a page exists before them. <paramref name="before"/> must be a date cursor of
    /// <paramref name="sort"/> (<see cref="IsDateCursorOf"/>).
    /// </summary>
    internal async Task<(List<BrowseRow> Rows, bool HasPrevious)> QueryDateBackwardAsync(
        IQueryable<CatalogNodeEntity> baseQuery,
        string sort,
        string before,
        int pageSize,
        SortDirection direction,
        CancellationToken ct)
    {
        var query = RowsBeforeDateCursor(baseQuery, sort, before, direction)
            ?? throw new ArgumentException("Not a date cursor of the sort.", nameof(before));

        var rows = await query
            .Take(pageSize + 1)
            .Select(n => new BrowseRow
            {
                Id = n.PublicId,
                ParentId = n.Parent != null ? n.Parent.PublicId : "",
                LibraryId = n.Library != null ? n.Library.PublicId : "",
                Kind = n.Kind,
                DisplayName = n.DisplayName,
                Availability = n.Availability,
                PageCount = n.ArchiveItem != null ? n.ArchiveItem.PageCount : null,
                InternalId = n.Id,
                CreatedAt = n.CreatedAt,
                LatestDescendantAddedAt = n.LatestDescendantAddedAt,
                SortKey = n.SortKey,
            })
            .ToListAsync(ct);

        var hasPrevious = rows.Count > pageSize;
        if (hasPrevious)
            rows = rows.Take(pageSize).ToList();
        rows.Reverse(); // fetched nearest-first; restore display order
        return (rows, hasPrevious);
    }

    /// <summary>
    /// A forward date page that started from a mid-list cursor (a time-chip landing, or a later page): its first row's cursor and whether
    /// any row is listed above it. A window loaded from the start (no cursor, or another sort's cursor) has nothing above it.
    /// </summary>
    private async Task<(bool HasPrevious, string? PrevCursor)> DatePreviousOfAsync(
        IQueryable<CatalogNodeEntity> baseQuery, string sort, string? cursor, BrowseRow firstRow, SortDirection direction,
        CancellationToken ct)
    {
        if (!IsDateCursorOf(sort, cursor))
            return (false, null);
        var prevCursor = EncodeCursor(sort, firstRow);
        var above = RowsBeforeDateCursor(baseQuery, sort, prevCursor, direction);
        return above is not null && await above.AnyAsync(ct) ? (true, prevCursor) : (false, null);
    }
}
