namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Text.Json;
using System.Text.Json.Nodes;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>An export answer: the JSON body, or an error code with its HTTP status.</summary>
public sealed record ExportAnswer(int Status, string? Json, string? Error)
{
    public static ExportAnswer Ok(string json) => new(StatusCodes.Status200OK, json, null);

    public static ExportAnswer Fail(int status, string error) => new(status, null, error);
}

/// <summary>
/// Reads the metadata export (1.33.0): the library list (counted live) and the pages of a library's stored snapshot
/// (<see cref="ExportRebuildService"/>). Only the first page of a call (no cursor) may trigger a rebuild. Read-only apart from the
/// rebuild; never contacts a provider.
/// </summary>
public sealed class ExportService(MangaPixerDbContext db, ExportRebuildService rebuilds, TimeProvider time)
{
    public const int DefaultLimit = 200;
    public const int MaxLimit = 500;

    public const string Volumes = "volumes";
    public const string Completion = "completion";
    public const string Refresh = "refresh";

    /// <summary>The optional item blocks <c>include</c> names (all by default).</summary>
    public static readonly IReadOnlyList<string> OptionalBlocks = [Volumes, Completion, Refresh];

    public async Task<ExportLibrariesDto> LibrariesAsync(CancellationToken ct = default)
    {
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var folder = (int)CatalogNodeKind.Folder;
        var libraries = await db.Libraries.AsNoTracking()
            .OrderBy(l => l.DisplayName).ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.PublicId, l.DisplayName, l.LastScanCompleted })
            .ToListAsync(ct);
        var kinds = await KindsAsync(ct);
        var folders = await db.CatalogNodes.AsNoTracking()
            .Where(n => n.Kind == folder && n.Availability != tombstoned)
            .GroupBy(n => n.LibraryId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        var items = await (
                from l in db.NodeSeriesLinks.AsNoTracking()
                join n in db.CatalogNodes.AsNoTracking() on l.NodeId equals n.Id
                where n.Availability != tombstoned
                group n by n.LibraryId into g
                select new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, ct);
        return new ExportLibrariesDto
        {
            SchemaVersion = ExportJson.SchemaVersion,
            ServerTime = ExportJson.Truncate(time.GetUtcNow()),
            Libraries = libraries.Select(l => new ExportLibraryDto
            {
                Id = l.PublicId,
                DisplayName = l.DisplayName,
                Kind = kinds.GetValueOrDefault(l.Id),
                FolderCount = folders.GetValueOrDefault(l.Id),
                ItemCount = items.GetValueOrDefault(l.Id),
                LastScanAt = l.LastScanCompleted is { } scan ? ExportJson.Truncate(scan) : null,
            }).ToList(),
        };
    }

    /// <summary>
    /// One page of a library's export. Errors: 400 <c>libraryRequired</c> / <c>invalidUpdatedSince</c> / <c>invalidCursor</c> /
    /// <c>invalidInclude</c>, 404 <c>libraryNotFound</c>, 409 <c>fullSyncRequired</c> (<paramref name="updatedSince"/> before the removal
    /// pool's watermark).
    /// </summary>
    public async Task<ExportAnswer> PageAsync(
        string? libraryPublicId, string? updatedSince, string? cursor, int? limit, string? include, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(libraryPublicId))
            return ExportAnswer.Fail(StatusCodes.Status400BadRequest, "libraryRequired");
        DateTimeOffset? since = null;
        if (updatedSince is not null)
        {
            if (!ExportJson.TryParseTime(updatedSince, out var parsed))
                return ExportAnswer.Fail(StatusCodes.Status400BadRequest, "invalidUpdatedSince");
            since = parsed;
        }
        ExportCursor? after = null;
        if (cursor is not null)
        {
            if (!ExportCursor.TryDecode(cursor, out var decoded))
                return ExportAnswer.Fail(StatusCodes.Status400BadRequest, "invalidCursor");
            after = decoded;
        }
        if (!TryParseInclude(include, out var blocks))
            return ExportAnswer.Fail(StatusCodes.Status400BadRequest, "invalidInclude");
        var take = Math.Clamp(limit ?? DefaultLimit, 1, MaxLimit);

        var library = await db.Libraries.AsNoTracking().Where(l => l.PublicId == libraryPublicId)
            .Select(l => new { l.Id, l.PublicId, l.DisplayName }).FirstOrDefaultAsync(ct);
        if (library is null)
            return ExportAnswer.Fail(StatusCodes.Status404NotFound, "libraryNotFound");

        // Only a call's first page rebuilds (at most once per minimum interval); later pages read the stored state.
        var state = after is null ? null : await db.ExportLibraryStates.AsNoTracking().FirstOrDefaultAsync(s => s.LibraryId == library.Id, ct);
        state ??= await rebuilds.EnsureFreshAsync(library.Id, ct);
        if (since is { } s && s < state.WatermarkAt)
            return ExportAnswer.Fail(StatusCodes.Status409Conflict, "fullSyncRequired");

        var query = db.ExportItems.AsNoTracking().Where(i => i.LibraryId == library.Id);
        if (since is { } from)
            query = query.Where(i => i.UpdatedAt >= from);
        if (after is { } c)
        {
            var at = new DateTimeOffset(c.UpdatedAtTicks, TimeSpan.Zero);
            query = query.Where(i => i.UpdatedAt > at || (i.UpdatedAt == at && i.Id > c.RowId));
        }
        var rows = await query.OrderBy(i => i.UpdatedAt).ThenBy(i => i.Id)
            .Select(i => new { i.Id, i.UpdatedAt, i.Json }).Take(take + 1).ToListAsync(ct);
        var page = rows.Take(take).ToList();
        var next = rows.Count > take ? new ExportCursor(page[^1].UpdatedAt.UtcTicks, page[^1].Id).Encode() : null;

        var removed = new JsonArray();
        if (since is { } removedSince && after is null)
        {
            var removals = await db.ExportRemovals.AsNoTracking()
                .Where(r => r.LibraryId == library.Id && r.At >= removedSince)
                .OrderBy(r => r.At).ThenBy(r => r.NodePublicId)
                .Select(r => new { r.NodePublicId, r.Reason, r.At })
                .ToListAsync(ct);
            foreach (var r in removals)
                removed.Add(Node(new ExportRemovalDto { NodeId = r.NodePublicId, Reason = r.Reason, At = r.At }));
        }

        var items = new JsonArray();
        foreach (var row in page)
        {
            var item = JsonNode.Parse(row.Json)!.AsObject();
            foreach (var block in OptionalBlocks.Where(b => !blocks.Contains(b)))
                item.Remove(block);
            items.Add(item);
        }
        var kind = (await KindsAsync(ct)).GetValueOrDefault(library.Id);
        var body = new JsonObject
        {
            ["schemaVersion"] = ExportJson.SchemaVersion,
            ["serverTime"] = ExportJson.Format(state.LastRebuildAt),
            ["library"] = Node(new ExportLibraryRefDto { Id = library.PublicId, DisplayName = library.DisplayName, Kind = kind }),
            ["items"] = items,
            ["removed"] = removed,
            ["nextCursor"] = next,
        };
        return ExportAnswer.Ok(body.ToJsonString(ExportJson.Options));
    }

    /// <summary><c>include</c>: absent = every optional block; otherwise a comma-separated subset (empty = none).</summary>
    public static bool TryParseInclude(string? include, out IReadOnlySet<string> blocks)
    {
        if (include is null)
        {
            blocks = OptionalBlocks.ToHashSet(StringComparer.Ordinal);
            return true;
        }
        var parts = include.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        blocks = parts.ToHashSet(StringComparer.Ordinal);
        return parts.Length <= OptionalBlocks.Count && parts.All(p => OptionalBlocks.Contains(p));
    }

    /// <summary>The library-scope declared type of every library that has one (the export's library <c>kind</c>).</summary>
    private async Task<Dictionary<long, string>> KindsAsync(CancellationToken ct) =>
        (await db.DeclaredFacts.AsNoTracking()
            .Where(f => f.NodeId == null && f.Key == DeclaredFactKeys.Type)
            .OrderBy(f => f.Position).ThenBy(f => f.Id)
            .Select(f => new { f.LibraryId, f.Value })
            .ToListAsync(ct))
        .GroupBy(f => f.LibraryId)
        .ToDictionary(g => g.Key, g => g.First().Value);

    private static JsonNode Node<T>(T value) => JsonSerializer.SerializeToNode(value, ExportJson.Options)!;
}
