namespace com.lifepixer.mangapixer.Server.Features.Reading;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Shared per-archive double-page pairing (1.23.0). The layout is a property of the
/// FILE, not of a user: one row per archive, readable through the item manifest and
/// writable by anyone who can read the archive
/// (<see cref="LibraryAuthorizationService.CanAccessItemAsync"/>). No audit events and
/// no admin reset by owner decision (minor, non-destructive; an admin fixes it by
/// reading like anyone else).
///
/// A layout is stamped with the archive's <see cref="ArchiveItemEntity.ContentVersion"/>;
/// once the scanner bumps that version (the file changed), the stored row is ignored
/// and lazily deleted, so a changed file resets the pairing.
/// </summary>
public sealed class SpreadLayoutService
{
    private readonly MangaPixerDbContext _db;
    private readonly LibraryAuthorizationService _auth;
    private readonly ILogger<SpreadLayoutService>? _logger;

    public SpreadLayoutService(MangaPixerDbContext db, LibraryAuthorizationService auth, ILogger<SpreadLayoutService>? logger = null)
    {
        _db = db;
        _auth = auth;
        _logger = logger;
    }

    public enum SetStatus
    {
        Ok,

        /// <summary>Unknown item, or the user cannot access it (no existence leak).</summary>
        NotFound,

        /// <summary>The node is a folder, not an archive.</summary>
        NotReadable,

        /// <summary>The client's content version is not the archive's current, analyzed one.</summary>
        StaleContent,

        /// <summary>The forced starts failed validation.</summary>
        Invalid,
    }

    public sealed record SetResult(SetStatus Status, SpreadLayoutDto? Layout = null, string? Error = null);

    /// <summary>
    /// The saved forced spread starts for an archive at its current content version, or
    /// null when none is saved (or the saved one is stale, which is deleted here).
    /// Caller is responsible for authorization (the manifest endpoint checks it first).
    /// </summary>
    public async Task<IReadOnlyList<int>?> GetCurrentAsync(long nodeId, long currentContentVersion, int pageCount, CancellationToken ct = default)
    {
        var row = await _db.ArchiveSpreadLayouts.AsNoTracking()
            .FirstOrDefaultAsync(x => x.NodeId == nodeId, ct);
        if (row is null)
            return null;

        if (row.ContentVersion != currentContentVersion)
        {
            // The file changed since the layout was saved: drop it. A set-based delete
            // is race-free against a concurrent reader doing the same, and re-checks the
            // stamp so a layout just saved against the new version survives.
            await _db.ArchiveSpreadLayouts
                .Where(x => x.NodeId == nodeId && x.ContentVersion != currentContentVersion)
                .ExecuteDeleteAsync(ct);
            _logger?.LogDebug("Stale spread layout dropped for item {ItemId}", nodeId);
            return null;
        }

        // Defensive: the row was validated on write, but never hand the reader an
        // out-of-range or unsorted set.
        return Parse(row.SpreadStartsJson)
            .Where(i => i >= 1 && i < pageCount)
            .Distinct()
            .Order()
            .ToList();
    }

    /// <summary>
    /// Replaces the shared layout for <paramref name="itemId"/> (internal node id).
    /// </summary>
    public async Task<SetResult> SetAsync(
        long userId,
        long itemId,
        string itemPublicId,
        long expectedContentVersion,
        IReadOnlyList<int>? spreadStarts,
        CancellationToken ct = default)
    {
        if (!await _auth.CanAccessItemAsync(userId, itemId, ct))
            return new SetResult(SetStatus.NotFound);

        var node = await _db.CatalogNodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == itemId, ct);
        if (node is null)
            return new SetResult(SetStatus.NotFound);
        if (node.Kind != 1)
            return new SetResult(SetStatus.NotReadable, Error: "Item is not a readable archive.");

        var item = await _db.ArchiveItems.AsNoTracking().FirstOrDefaultAsync(a => a.NodeId == itemId, ct);
        if (item is null)
            return new SetResult(SetStatus.NotFound);

        // Only a ready, analyzed archive has pages a client can have paired; anything
        // else (or a different version) means the client's view is out of date.
        if (item.AnalysisState != 0 || item.PageCount is not int pageCount || pageCount <= 0
            || item.ContentVersion != expectedContentVersion)
        {
            return new SetResult(SetStatus.StaleContent,
                Error: "Content version mismatch: the item has changed since the client last loaded it.");
        }

        var invalid = Validate(spreadStarts, pageCount);
        if (invalid is not null)
            return new SetResult(SetStatus.Invalid, Error: invalid);

        var starts = spreadStarts!;
        var json = JsonSerializer.Serialize(starts);
        var now = DateTimeOffset.UtcNow;

        var saved = await UpsertAsync(itemId, item.ContentVersion, json, now, ct);
        if (!saved)
        {
            // Lost an insert race against a concurrent first save: retry once as an update.
            _db.ChangeTracker.Clear();
            await UpsertAsync(itemId, item.ContentVersion, json, now, ct);
        }

        _logger?.LogDebug("Spread layout saved for item {ItemId}: {Count} forced starts", itemId, starts.Count);

        return new SetResult(SetStatus.Ok, new SpreadLayoutDto
        {
            ItemId = itemPublicId,
            ContentVersion = item.ContentVersion,
            SpreadStarts = starts,
            UpdatedAt = now,
        });
    }

    private async Task<bool> UpsertAsync(long nodeId, long contentVersion, string json, DateTimeOffset now, CancellationToken ct)
    {
        var row = await _db.ArchiveSpreadLayouts.FirstOrDefaultAsync(x => x.NodeId == nodeId, ct);
        if (row is null)
        {
            _db.ArchiveSpreadLayouts.Add(new ArchiveSpreadLayoutEntity
            {
                NodeId = nodeId,
                ContentVersion = contentVersion,
                SpreadStartsJson = json,
                UpdatedAt = now,
            });
        }
        else
        {
            row.ContentVersion = contentVersion;
            row.SpreadStartsJson = json;
            row.UpdatedAt = now;
        }

        try
        {
            await _db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException) when (row is null)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates a forced-starts set against a page count. Returns null when valid,
    /// else a client-facing message (no paths, no titles).
    /// </summary>
    internal static string? Validate(IReadOnlyList<int>? spreadStarts, int pageCount)
    {
        if (spreadStarts is null)
            return "spreadStarts is required (use an empty list for no shifts).";
        if (spreadStarts.Count > pageCount)
            return $"Too many spread starts ({spreadStarts.Count}) for {pageCount} pages.";

        var previous = 0;
        foreach (var index in spreadStarts)
        {
            if (index < 1 || index > pageCount - 1)
                return $"Spread start {index} is out of range (1..{pageCount - 1}).";
            if (index <= previous)
                return "Spread starts must be unique and sorted ascending.";
            previous = index;
        }

        return null;
    }

    private static List<int> Parse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<List<int>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
