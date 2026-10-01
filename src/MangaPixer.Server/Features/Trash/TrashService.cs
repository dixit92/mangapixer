namespace com.lifepixer.mangapixer.Server.Features.Trash;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Empty trash (1.31.0): purges tombstoned nodes past the retention window (<see cref="TrashRetention"/>) together with
/// everything they own - archive item, pages, reading state, read marks, bookmarks, reader overrides, favorites, links, cover
/// decisions, thumbnail and cover crops - but never a metadata record, poster or web cover (they belong to the record).
/// Tombstones held by move recognition (<see cref="ITombstoneHolds"/>) always stay; a library is held as a whole while a scan
/// of it runs, when its root was unreachable at the last scan, or when more than half of it would go (an offline disk) -
/// the last two only until the admin empties that one library (<see cref="TrashRules"/>).
/// One transaction per library; files go after the commit. Logs ids and counts only.
/// </summary>
public sealed class TrashService
{
    private readonly MangaPixerDbContext _db;
    private readonly ITombstoneHolds _holds;
    private readonly NodePurger _purger;
    private readonly BundleCleaner _bundles;
    private readonly AuditService _audit;
    private readonly TrashRunGate _gate;
    private readonly TimeProvider _time;
    private readonly ILogger<TrashService> _logger;

    public TrashService(
        MangaPixerDbContext db,
        ITombstoneHolds holds,
        NodePurger purger,
        BundleCleaner bundles,
        AuditService audit,
        TrashRunGate gate,
        TimeProvider time,
        ILogger<TrashService> logger)
    {
        _db = db;
        _holds = holds;
        _purger = purger;
        _bundles = bundles;
        _audit = audit;
        _gate = gate;
        _time = time;
        _logger = logger;
    }

    // --- Overview / settings ---

    public async Task<TrashOverviewDto> GetOverviewAsync(CancellationToken ct)
    {
        var settings = await SettingsRowAsync(ct);
        var now = _time.GetUtcNow();
        var windowStart = TrashRetention.WindowStart(now, settings?.TrashRetentionDays);
        var libraries = await LoadLibrariesAsync(windowStart, ct);

        var rows = new List<TrashLibraryDto>();
        var total = Zero;
        foreach (var library in libraries.Where(l => l.Eligible.Count > 0 || l.Tombstoned > 0))
        {
            var plan = TrashRules.Plan(library.Eligible, await ChildrenOfFoldersAsync(library.Eligible, ct));
            var files = _purger.MeasureFiles(await _purger.CollectFilesAsync(plan.Archives, ct));
            var state = await _purger.CountUserStateAsync(plan.AllNodeIds.ToList(), ct);
            var counts = CountsOf(plan, state, files);
            rows.Add(new TrashLibraryDto
            {
                LibraryId = library.PublicId,
                Name = library.Name,
                Eligible = counts,
                Waiting = library.Tombstoned - plan.NodeCount,
                LibraryNodes = library.Nodes,
                Hold = TrashRules.CodeOf(library.Hold),
                HoldReleasable = TrashRules.IsReleasable(library.Hold),
            });
            if (library.Hold == TrashHold.None)
                total = Add(total, counts);
        }

        var bundles = await _bundles.PreviewAsync(ct);
        return new TrashOverviewDto
        {
            Settings = SettingsOf(settings),
            WindowStart = windowStart,
            Libraries = rows,
            Total = total,
            Bundles = new TrashFilesDto { Files = bundles.Files, Bytes = bundles.Bytes },
            LastEmpty = settings?.TrashLastEmptiedAt is { } emptied
                ? new TrashRunDto
                {
                    At = emptied,
                    Automatic = settings.TrashLastEmptiedAutomatic,
                    Count = settings.TrashLastEmptiedNodes,
                    Bytes = settings.TrashLastEmptiedBytes,
                    HeldLibraries = settings.TrashLastEmptiedHeldLibraries,
                }
                : null,
            LastBundleClean = settings?.BundlesLastCleanedAt is { } cleaned
                ? new TrashRunDto
                {
                    At = cleaned,
                    Automatic = settings.BundlesLastCleanedAutomatic,
                    Count = settings.BundlesLastCleanedFiles,
                    Bytes = settings.BundlesLastCleanedBytes,
                    HeldLibraries = 0,
                }
                : null,
        };
    }

    public async Task<(TrashSettingsDto? Settings, string? Error)> UpdateSettingsAsync(UpdateTrashSettingsRequest request, string? actor, CancellationToken ct)
    {
        if (request.RetentionDays is { } days && !TrashRetention.IsAllowed(days))
            return (null, "invalid_retention");

        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            _db.AppSettings.Add(row);
        }

        var audits = new List<(string Action, string Result)>();
        if (request.RetentionDays is { } retention && retention != TrashRetention.DaysOf(row.TrashRetentionDays))
        {
            row.TrashRetentionDays = retention;
            audits.Add((AuditActions.TrashRetentionChange, string.Create(CultureInfo.InvariantCulture, $"days_{retention}")));
        }
        if (request.AutomaticCleaning is { } automatic && automatic != row.TrashAutoCleanEnabled)
        {
            row.TrashAutoCleanEnabled = automatic;
            if (automatic)
                row.TrashAutoCleanEnabledAt = _time.GetUtcNow();
            audits.Add((automatic ? AuditActions.TrashAutoEnable : AuditActions.TrashAutoDisable, AuditResults.Success));
        }

        await _db.SaveChangesAsync(ct);
        foreach (var (action, result) in audits)
            await _audit.RecordAsync(action, result, actor, ct: ct);
        if (audits.Count > 0)
            _logger.LogInformation(LogEvents.Administration.TrashSettingsChanged, "Trash settings changed: automatic {Automatic}, retention {Days} days",
                row.TrashAutoCleanEnabled, TrashRetention.DaysOf(row.TrashRetentionDays));
        return (SettingsOf(row), null);
    }

    // --- Empty trash ---

    /// <summary>
    /// Empties the trash of every library without a hold (<paramref name="libraryPublicId"/> null), or of one library -
    /// which, with <paramref name="releaseHold"/>, also goes when held for a burst or an unreachable root. A running scan
    /// always holds. Records the run, one audit event and one log line.
    /// </summary>
    public async Task<TrashEmptyOutcome> EmptyAsync(string? libraryPublicId, bool releaseHold, bool automatic, string? actor, CancellationToken ct)
    {
        await _gate.Semaphore.WaitAsync(ct);
        try
        {
            return await EmptyCoreAsync(libraryPublicId, releaseHold, automatic, actor, ct);
        }
        finally
        {
            _gate.Semaphore.Release();
        }
    }

    private async Task<TrashEmptyOutcome> EmptyCoreAsync(string? libraryPublicId, bool releaseHold, bool automatic, string? actor, CancellationToken ct)
    {
        long? onlyLibrary = null;
        if (libraryPublicId is not null)
        {
            var id = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct);
            if (id is null)
                return TrashEmptyOutcome.Fail("not_found");
            onlyLibrary = id;
        }

        var settings = await SettingsRowAsync(ct);
        var now = _time.GetUtcNow();
        var windowStart = TrashRetention.WindowStart(now, settings?.TrashRetentionDays);
        var libraries = (await LoadLibrariesAsync(windowStart, ct))
            .Where(l => onlyLibrary is null || l.Id == onlyLibrary)
            .ToList();

        if (onlyLibrary is not null && libraries.FirstOrDefault() is { } single && single.Hold != TrashHold.None)
        {
            if (single.Hold == TrashHold.ScanRunning)
                return TrashEmptyOutcome.Fail("scan_in_progress");
            if (!releaseHold)
                return TrashEmptyOutcome.Fail("trash_held", TrashRules.CodeOf(single.Hold));
        }

        var removed = Zero;
        var held = new List<TrashHeldLibraryDto>();
        foreach (var library in libraries)
        {
            var releasing = onlyLibrary is not null && releaseHold && TrashRules.IsReleasable(library.Hold);
            if (library.Hold != TrashHold.None && !releasing)
            {
                if (library.Eligible.Count > 0)
                    held.Add(new TrashHeldLibraryDto { LibraryId = library.PublicId, Hold = TrashRules.CodeOf(library.Hold)! });
                continue;
            }
            if (library.Eligible.Count == 0)
                continue;
            var counts = await PurgeLibraryAsync(library.Id, windowStart, ct);
            removed = Add(removed, counts);
            if (releasing)
                _logger.LogInformation(LogEvents.Administration.TrashHoldReleased, "Trash hold {Hold} released by an admin for library {LibraryId}",
                    TrashRules.CodeOf(library.Hold), library.Id);
        }

        var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
        if (row is null)
        {
            row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            _db.AppSettings.Add(row);
        }
        row.TrashLastEmptiedAt = now;
        row.TrashLastEmptiedAutomatic = automatic;
        row.TrashLastEmptiedNodes = removed.Nodes;
        row.TrashLastEmptiedBytes = removed.Bytes;
        row.TrashLastEmptiedHeldLibraries = held.Count;
        await _db.SaveChangesAsync(ct);

        await _audit.RecordAsync(automatic ? AuditActions.TrashEmptyAuto : AuditActions.TrashEmpty,
            string.Create(CultureInfo.InvariantCulture, $"nodes_{removed.Nodes}_held_{held.Count}"),
            actor, ct: ct, targetLibraryId: onlyLibrary);
        _logger.LogInformation(LogEvents.Administration.TrashEmptied,
            "Trash emptied ({Trigger}, library {LibraryId}): {Nodes} nodes ({Archives} archives, {Folders} folders), {StateRows} user state rows, {Files} files, {Bytes} bytes; {Held} libraries held",
            automatic ? "automatic" : "admin", onlyLibrary, removed.Nodes, removed.Archives, removed.Folders, removed.UserStateRows,
            removed.Files, removed.Bytes, held.Count);
        if (held.Count > 0)
            _logger.LogInformation(LogEvents.Administration.TrashEmptyHeld, "Trash held for libraries {LibraryIds}",
                string.Join(",", libraries.Where(l => held.Any(h => h.LibraryId == l.PublicId)).Select(l => l.Id.ToString(CultureInfo.InvariantCulture))));

        return TrashEmptyOutcome.Ok(new EmptyTrashResultDto { Removed = removed, Held = held });
    }

    /// <summary>
    /// Purges one library's eligible tombstones in one transaction: the plan is made inside it (a node a scan brought back
    /// just before is no longer eligible), the per-user rows by item id and the item's jobs go first, then the nodes
    /// leaf-first (their foreign keys cascade to everything else they own). Files go after the commit.
    /// </summary>
    private async Task<TrashCountsDto> PurgeLibraryAsync(long libraryId, DateTimeOffset windowStart, CancellationToken ct)
    {
        TrashPurgePlan plan;
        NodeFileKeys files;
        UserStateCounts state;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            var eligible = await EligibleQuery(windowStart)
                .Where(n => n.LibraryId == libraryId)
                .Select(n => new TrashNode(n.Id, n.ParentId, n.Kind))
                .ToListAsync(ct);
            plan = TrashRules.Plan(eligible, await ChildrenOfFoldersAsync(eligible, ct));
            if (plan.NodeCount == 0)
                return Zero;

            var all = plan.AllNodeIds.ToList();
            files = await _purger.CollectFilesAsync(plan.Archives, ct);
            var favorites = await _db.Favorites.CountAsync(f => all.Contains(f.CatalogNodeId), ct);
            state = (await _purger.DeleteUserStateAsync(plan.Archives, ct)) with { Favorites = favorites };
            var archiveIds = plan.Archives;
            await _db.Jobs.Where(j => j.ItemId != null && archiveIds.Contains(j.ItemId.Value) && j.Status != 1).ExecuteDeleteAsync(ct);

            if (archiveIds.Count > 0)
                await _db.CatalogNodes.Where(n => archiveIds.Contains(n.Id)).ExecuteDeleteAsync(ct);
            foreach (var round in plan.FolderRounds)
                await _db.CatalogNodes.Where(n => round.Contains(n.Id)).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        }

        var removedFiles = _purger.DeleteFiles(files);
        return CountsOf(plan, state, removedFiles);
    }

    // --- Clean bundles ---

    public async Task<TrashFilesDto> CleanBundlesAsync(bool automatic, string? actor, CancellationToken ct)
    {
        await _gate.Semaphore.WaitAsync(ct);
        try
        {
            var removed = await _bundles.CleanAsync(ct);
            var row = await _db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);
            if (row is null)
            {
                row = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
                _db.AppSettings.Add(row);
            }
            row.BundlesLastCleanedAt = _time.GetUtcNow();
            row.BundlesLastCleanedAutomatic = automatic;
            row.BundlesLastCleanedFiles = removed.Files;
            row.BundlesLastCleanedBytes = removed.Bytes;
            await _db.SaveChangesAsync(ct);

            await _audit.RecordAsync(automatic ? AuditActions.TrashBundlesCleanAuto : AuditActions.TrashBundlesClean,
                string.Create(CultureInfo.InvariantCulture, $"files_{removed.Files}"), actor, ct: ct);
            _logger.LogInformation(LogEvents.Administration.BundlesCleaned, "Bundles cleaned ({Trigger}): {Files} files, {Bytes} bytes",
                automatic ? "automatic" : "admin", removed.Files, removed.Bytes);
            return new TrashFilesDto { Files = removed.Files, Bytes = removed.Bytes };
        }
        finally
        {
            _gate.Semaphore.Release();
        }
    }

    // --- Queries ---

    /// <summary>Tombstones past the window that move recognition does not hold.</summary>
    private IQueryable<CatalogNodeEntity> EligibleQuery(DateTimeOffset windowStart)
    {
        var held = _holds.HeldNodeIds();
        return _db.CatalogNodes.AsNoTracking()
            .Where(n => n.Availability == 5 && n.TombstonedAt != null && n.TombstonedAt < windowStart && !held.Contains(n.Id));
    }

    private async Task<List<TrashChild>> ChildrenOfFoldersAsync(IReadOnlyCollection<TrashNode> eligible, CancellationToken ct)
    {
        var folders = eligible.Where(n => n.Kind != 1).Select(n => n.Id).ToList();
        if (folders.Count == 0)
            return [];
        return await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.ParentId != null && folders.Contains(n.ParentId.Value))
            .Select(n => new TrashChild(n.Id, n.ParentId!.Value))
            .ToListAsync(ct);
    }

    private sealed record LibraryTrash(long Id, string PublicId, string Name, int Nodes, int Tombstoned, List<TrashNode> Eligible, TrashHold Hold);

    private async Task<List<LibraryTrash>> LoadLibrariesAsync(DateTimeOffset windowStart, CancellationToken ct)
    {
        var libraries = await _db.Libraries.AsNoTracking()
            .OrderBy(l => l.DisplayName).ThenBy(l => l.Id)
            .Select(l => new { l.Id, l.PublicId, l.DisplayName })
            .ToListAsync(ct);
        var sizes = await _db.CatalogNodes.AsNoTracking()
            .GroupBy(n => n.LibraryId)
            .Select(g => new { LibraryId = g.Key, Nodes = g.Count(), Tombstoned = g.Count(n => n.Availability == 5) })
            .ToDictionaryAsync(g => g.LibraryId, ct);
        var eligible = (await EligibleQuery(windowStart)
                .Select(n => new { n.LibraryId, n.Id, n.ParentId, n.Kind })
                .ToListAsync(ct))
            .GroupBy(n => n.LibraryId)
            .ToDictionary(g => g.Key, g => g.Select(n => new TrashNode(n.Id, n.ParentId, n.Kind)).ToList());
        var scanning = (await _db.ScanRuns.AsNoTracking()
                .Where(s => s.Status == 0 || s.Status == 1)
                .Select(s => s.LibraryId)
                .ToListAsync(ct))
            .ToHashSet();
        // The latest FINISHED scan (completed or failed) of each library; a cancelled or interrupted scan says nothing.
        var latestRunIds = await _db.ScanRuns.AsNoTracking()
            .Where(s => s.Status == 2 || s.Status == 3)
            .GroupBy(s => s.LibraryId)
            .Select(g => g.Max(s => s.Id))
            .ToListAsync(ct);
        var latestErrors = await _db.ScanRuns.AsNoTracking()
            .Where(s => latestRunIds.Contains(s.Id))
            .Select(s => new { s.LibraryId, s.SanitizedError })
            .ToDictionaryAsync(s => s.LibraryId, s => s.SanitizedError, ct);

        return libraries.Select(l =>
        {
            var size = sizes.GetValueOrDefault(l.Id);
            var nodes = eligible.GetValueOrDefault(l.Id) ?? [];
            var hold = TrashRules.HoldOf(scanning.Contains(l.Id), latestErrors.GetValueOrDefault(l.Id), nodes.Count, size?.Nodes ?? 0);
            return new LibraryTrash(l.Id, l.PublicId, l.DisplayName, size?.Nodes ?? 0, size?.Tombstoned ?? 0, nodes, hold);
        }).ToList();
    }

    private async Task<AppSettingsEntity?> SettingsRowAsync(CancellationToken ct) =>
        await _db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId, ct);

    internal static TrashSettingsDto SettingsOf(AppSettingsEntity? row) => new()
    {
        AutomaticCleaning = row?.TrashAutoCleanEnabled ?? false,
        RetentionDays = TrashRetention.DaysOf(row?.TrashRetentionDays),
        AllowedRetentionDays = TrashRetention.AllowedDays,
        AutomaticHour = TrashSchedule.RunHour,
    };

    private static readonly TrashCountsDto Zero = new() { Nodes = 0, Archives = 0, Folders = 0, UserStateRows = 0, Files = 0, Bytes = 0 };

    private static TrashCountsDto CountsOf(TrashPurgePlan plan, UserStateCounts state, FileTotals files) => new()
    {
        Nodes = plan.NodeCount,
        Archives = plan.Archives.Count,
        Folders = plan.FolderCount,
        UserStateRows = state.Total,
        Files = files.Files,
        Bytes = files.Bytes,
    };

    private static TrashCountsDto Add(TrashCountsDto a, TrashCountsDto b) => new()
    {
        Nodes = a.Nodes + b.Nodes,
        Archives = a.Archives + b.Archives,
        Folders = a.Folders + b.Folders,
        UserStateRows = a.UserStateRows + b.UserStateRows,
        Files = a.Files + b.Files,
        Bytes = a.Bytes + b.Bytes,
    };
}

/// <summary>Serialises trash runs (an admin's "now" and the daily run never purge at the same time).</summary>
public sealed class TrashRunGate
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}

/// <summary>The result of "Empty trash now": the removed counts, or an error code (and the hold that refused it).</summary>
public sealed record TrashEmptyOutcome(EmptyTrashResultDto? Result, string? Error, string? Hold)
{
    public static TrashEmptyOutcome Ok(EmptyTrashResultDto result) => new(result, null, null);

    public static TrashEmptyOutcome Fail(string error, string? hold = null) => new(null, error, hold);
}
