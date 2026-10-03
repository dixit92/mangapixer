namespace com.lifepixer.mangapixer.Server.Features.Export;

using System.Collections.Concurrent;
using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Export settings (configuration section <c>Export</c>).</summary>
public sealed record ExportOptions(TimeSpan MinRebuildInterval)
{
    public const int DefaultMinRebuildMinutes = 5;

    /// <summary>The removal pool covers at least this many days, and at least the effective trash retention.</summary>
    public const int MinPoolDays = 30;

    /// <summary><c>Export:MinRebuildMinutes</c> (default 5; 0 = rebuild on every first-page call).</summary>
    public static ExportOptions FromConfiguration(IConfiguration configuration)
    {
        var minutes = configuration.GetValue("Export:MinRebuildMinutes", DefaultMinRebuildMinutes);
        return new ExportOptions(TimeSpan.FromMinutes(Math.Clamp(minutes, 0, 24 * 60)));
    }
}

/// <summary>One rebuild at a time per library (singleton).</summary>
public sealed class ExportRebuildGate
{
    private readonly ConcurrentDictionary<long, SemaphoreSlim> _locks = new();

    public SemaphoreSlim For(long libraryId) => _locks.GetOrAdd(libraryId, _ => new SemaphoreSlim(1, 1));
}

/// <summary>What one rebuild did (counts only).</summary>
public sealed record ExportRebuildResult(int Items, int Written, int Removed, int Carried, long ElapsedMs);

/// <summary>
/// Rebuilds a library's stored export snapshot (1.33.0): computes the current items (<see cref="ExportItemBuilder"/>), writes the new
/// and changed ones with <c>UpdatedAt</c> = the rebuild time, and turns every item that vanished into a removal with its reason - or,
/// when folder carry-over put its link on a node of this library, into that item's <c>carriedFrom</c>. Prunes the removal pool to its
/// window and moves the watermark with it. The real protection of the export (owner decision 3): at most one rebuild per
/// <see cref="ExportOptions.MinRebuildInterval"/> per library, one at a time. Logs ids, counts and timings only.
/// </summary>
public sealed class ExportRebuildService(
    MangaPixerDbContext db, ExportItemBuilder builder, ExportRebuildGate gate, ExportOptions options, TimeProvider time,
    ILogger<ExportRebuildService> logger)
{
    /// <summary>
    /// The library's export state, rebuilt first when it was never built or its last rebuild is older than the minimum interval. A
    /// caller that waited for another request's rebuild uses that one.
    /// </summary>
    public async Task<ExportLibraryStateEntity> EnsureFreshAsync(long libraryId, CancellationToken ct = default)
    {
        var state = await StateAsync(libraryId, ct);
        if (state is not null && !IsStale(state))
            return state;
        var libraryLock = gate.For(libraryId);
        await libraryLock.WaitAsync(ct);
        try
        {
            state = await StateAsync(libraryId, ct);
            if (state is not null && !IsStale(state))
                return state;
            await RebuildAsync(libraryId, ct);
            return (await StateAsync(libraryId, ct))!;
        }
        finally
        {
            libraryLock.Release();
        }
    }

    private bool IsStale(ExportLibraryStateEntity state) => time.GetUtcNow() - state.LastRebuildAt >= options.MinRebuildInterval;

    private Task<ExportLibraryStateEntity?> StateAsync(long libraryId, CancellationToken ct) =>
        db.ExportLibraryStates.AsNoTracking().FirstOrDefaultAsync(s => s.LibraryId == libraryId, ct);

    private sealed record ExistingRow(long Id, long NodeId, string NodePublicId, string Fingerprint);

    /// <summary>Rebuilds now (callers hold the library's gate; <see cref="EnsureFreshAsync"/> is the normal entry).</summary>
    public async Task<ExportRebuildResult> RebuildAsync(long libraryId, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var now = ExportJson.Truncate(time.GetUtcNow());
        var lastScan = await db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.LastScanCompleted).FirstOrDefaultAsync(ct);
        var built = await builder.BuildAsync(libraryId, now, ct);
        var poolStart = now - await PoolWindowAsync(ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // The carry ledger of the pool window (newest first wins per node).
        await db.ExportCarries.Where(c => c.At < poolStart).ExecuteDeleteAsync(ct);
        var carries = await db.ExportCarries.AsNoTracking().OrderByDescending(c => c.At).ThenByDescending(c => c.Id).ToListAsync(ct);
        var carriedTo = carries.GroupBy(c => c.NewNodeId).ToDictionary(g => g.Key, g => g.First());
        var carriedAway = carries.GroupBy(c => c.OldNodeId).ToDictionary(g => g.Key, g => g.First());

        var existing = await db.ExportItems.AsNoTracking().Where(i => i.LibraryId == libraryId)
            .Select(i => new ExistingRow(i.Id, i.NodeId, i.NodePublicId, i.Fingerprint))
            .ToDictionaryAsync(i => i.NodeId, ct);
        var current = built.ToDictionary(b => b.NodeId);
        var currentPublicIds = built.Select(b => b.NodePublicId).ToHashSet(StringComparer.Ordinal);

        // 1. New and changed items.
        var written = 0;
        var carried = 0;
        var writtenIds = new List<string>();
        foreach (var b in built)
        {
            var carriedFrom = carriedTo.TryGetValue(b.NodeId, out var carry) && carry.OldNodeId != b.NodeId ? carry.OldNodePublicId : null;
            var item = b.Item with
            {
                CarriedFrom = carriedFrom,
                UpdatedAt = now,
                Completion = b.Item.Completion is null ? null : b.Item.Completion with { ComputedAt = now, BasedOnScanAt = TruncateOrNull(lastScan) },
            };
            var fingerprint = ExportJson.Fingerprint(item);
            existing.TryGetValue(b.NodeId, out var row);
            if (row is not null && row.Fingerprint == fingerprint)
                continue;
            var json = ExportJson.Serialize(item);
            if (row is null)
            {
                db.ExportItems.Add(new ExportItemEntity
                {
                    LibraryId = libraryId,
                    NodeId = b.NodeId,
                    NodePublicId = b.NodePublicId,
                    Json = json,
                    Fingerprint = fingerprint,
                    UpdatedAt = now,
                });
            }
            else
            {
                await db.ExportItems.Where(i => i.Id == row.Id).ExecuteUpdateAsync(u => u
                    .SetProperty(i => i.Json, json)
                    .SetProperty(i => i.Fingerprint, fingerprint)
                    .SetProperty(i => i.NodePublicId, b.NodePublicId)
                    .SetProperty(i => i.UpdatedAt, now), ct);
            }
            if (carriedFrom is not null && row is null)
                carried++;
            writtenIds.Add(b.NodePublicId);
            written++;
        }
        await db.SaveChangesAsync(ct);
        // A node that is an item again is no removal (a node is never both).
        foreach (var chunk in writtenIds.Chunk(500))
            await db.ExportRemovals.Where(r => r.LibraryId == libraryId && chunk.Contains(r.NodePublicId)).ExecuteDeleteAsync(ct);

        // 2. Items that vanished: a removal with its reason, or nothing when a carried item of this library replaces them.
        var dropped = existing.Values.Where(r => !current.ContainsKey(r.NodeId)).ToList();
        var removals = new Dictionary<string, string>(StringComparer.Ordinal);
        if (dropped.Count > 0)
        {
            var nodeIds = dropped.Select(r => r.NodeId)
                .Concat(dropped.Select(r => carriedAway.TryGetValue(r.NodeId, out var c) ? c.NewNodeId : 0).Where(id => id != 0))
                .Distinct().ToList();
            var nodes = await db.CatalogNodes.AsNoTracking().Where(n => nodeIds.Contains(n.Id))
                .Select(n => new { n.Id, n.LibraryId, Live = n.Availability != (int)CatalogNodeAvailability.Tombstoned })
                .ToDictionaryAsync(n => n.Id, ct);
            var linkedElsewhere = (await db.NodeSeriesLinks.AsNoTracking().Where(l => nodeIds.Contains(l.NodeId)).Select(l => l.NodeId).ToListAsync(ct))
                .ToHashSet();
            bool ItemOfOtherLibrary(long nodeId) =>
                nodes.TryGetValue(nodeId, out var n) && n.Live && n.LibraryId != libraryId && linkedElsewhere.Contains(nodeId);

            var droppedIds = dropped.Select(r => r.Id).ToList();
            var droppedCarriedFrom = (await db.ExportItems.AsNoTracking().Where(i => droppedIds.Contains(i.Id)).Select(i => new { i.Id, i.Json }).ToListAsync(ct))
                .ToDictionary(i => i.Id, i => ExportJson.ReadItem(i.Json)?.CarriedFrom);
            foreach (var row in dropped)
            {
                string? reason;
                if (ItemOfOtherLibrary(row.NodeId))
                    reason = ExportVocabulary.MovedToOtherLibrary;
                else if (carriedAway.TryGetValue(row.NodeId, out var carry) && current.ContainsKey(carry.NewNodeId))
                    reason = null; // shown as the carried item's carriedFrom
                else if (carry is not null && ItemOfOtherLibrary(carry.NewNodeId))
                    reason = ExportVocabulary.MovedToOtherLibrary;
                else if (!nodes.TryGetValue(row.NodeId, out var node) || !node.Live)
                    reason = ExportVocabulary.NodeGone;
                else
                    reason = ExportVocabulary.LinkCleared;
                if (reason is not null)
                    removals[row.NodePublicId] = reason;
                // The id this row was itself carried from leaves the export with it: a client that missed the carry learns it is gone.
                if (droppedCarriedFrom.GetValueOrDefault(row.Id) is { } older && !currentPublicIds.Contains(older))
                    removals.TryAdd(older, reason ?? ExportVocabulary.NodeGone);
            }
            await db.ExportItems.Where(i => droppedIds.Contains(i.Id)).ExecuteDeleteAsync(ct);
        }
        foreach (var (publicId, reason) in removals.Where(r => !currentPublicIds.Contains(r.Key)))
        {
            var pooled = await db.ExportRemovals.FirstOrDefaultAsync(r => r.LibraryId == libraryId && r.NodePublicId == publicId, ct);
            if (pooled is null)
                db.ExportRemovals.Add(new ExportRemovalEntity { LibraryId = libraryId, NodePublicId = publicId, Reason = reason, At = now });
            else
            {
                pooled.Reason = reason;
                pooled.At = now;
            }
        }

        // 3. The pool window: older removals go, and an incremental call from before the window needs a full sync.
        await db.ExportRemovals.Where(r => r.LibraryId == libraryId && r.At < poolStart).ExecuteDeleteAsync(ct);
        var state = await db.ExportLibraryStates.FirstOrDefaultAsync(s => s.LibraryId == libraryId, ct);
        if (state is null)
        {
            // The first rebuild: nothing before it was tracked, so every client's first incremental call is a full sync.
            state = new ExportLibraryStateEntity { LibraryId = libraryId, WatermarkAt = now };
            db.ExportLibraryStates.Add(state);
        }
        else if (poolStart > state.WatermarkAt)
            state.WatermarkAt = poolStart;
        state.LastRebuildAt = now;
        state.ItemCount = built.Count;
        state.LastRebuildMs = sw.ElapsedMilliseconds;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        var result = new ExportRebuildResult(built.Count, written, removals.Count, carried, sw.ElapsedMilliseconds);
        logger.LogInformation(LogEvents.Metadata.ExportRebuilt,
            "Export rebuild of library {LibraryId}: {Items} items, {Written} written, {Removed} removed, {Carried} carried in {ElapsedMs} ms",
            libraryId, result.Items, result.Written, result.Removed, result.Carried, result.ElapsedMs);
        return result;
    }

    /// <summary>The removal pool's window: the effective trash retention, at least <see cref="ExportOptions.MinPoolDays"/> days.</summary>
    private async Task<TimeSpan> PoolWindowAsync(CancellationToken ct)
    {
        var stored = await db.AppSettings.AsNoTracking().Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.TrashRetentionDays).FirstOrDefaultAsync(ct);
        return TimeSpan.FromDays(Math.Max(TrashRetention.DaysOf(stored), ExportOptions.MinPoolDays));
    }

    private static DateTimeOffset? TruncateOrNull(DateTimeOffset? time) => time is { } t ? ExportJson.Truncate(t) : null;
}
