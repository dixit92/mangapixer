namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using System.Security.Cryptography;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>Why automatic work cannot send requests right now.</summary>
public sealed record AutomaticWait(string Code, DateTimeOffset? Until);

/// <summary>
/// Automatic matching (metadata stage 2): the durable queue, runs, the automatic
/// gate, bulk "Match this library now", re-runs, and the processing of one queued
/// work (lookup through the matcher-core contract, then ONE transaction writing
/// the link / review candidates / retry date and the run counters).
///
/// Gate (owner decisions 3 + 5): config kill, global Fetch + v1 consent, the global
/// Automatic matching switch + automatic consent, the library's own Fetch switch,
/// the persisted backoff and the ONE daily budget. When the budget is spent,
/// automatic work waits for the next UTC day; there is no separate cap or reserve.
///
/// Re-match rules (section 3): Confirmed, Don't match and Auto links are never
/// matched again automatically; Needs review only when an admin asks (re-run);
/// Unmatched after 30 / 90 / 180 days, then never. The matcher-core
/// implementations (<see cref="IWorkDetector"/>, <see cref="IMatchQueryPlanner"/>,
/// <see cref="IMatchScorer"/>) come from DI; while they are not registered the
/// worker reports <c>matcher_unavailable</c> and sends nothing.
/// Logs carry ids, counts and codes only.
/// </summary>
public sealed class MetadataAutoMatchService
{
    private readonly MangaPixerDbContext _db;
    private readonly MetadataGateway _gateway;
    private readonly MetadataBudget _budget;
    private readonly MetadataBackoff _backoff;
    private readonly MetadataSettingsService _settings;
    private readonly MetadataIdentifyService _identify;
    private readonly MetadataAutoMatchState _state;
    private readonly AuditService _audit;
    private readonly TimeProvider _time;
    private readonly ILogger<MetadataAutoMatchService> _logger;
    private readonly IWorkDetector? _detector;
    private readonly IMatchQueryPlanner? _planner;
    private readonly IMatchScorer? _scorer;
    private readonly bool _providerAuthorFolders;
    private readonly bool _declaredTypeFilter;
    private readonly AutoMatchCoverComparer? _covers;
    private readonly Declared.IDeclaredFactsReader? _declared;

    public MetadataAutoMatchService(
        MangaPixerDbContext db,
        MetadataGateway gateway,
        MetadataBudget budget,
        MetadataBackoff backoff,
        MetadataSettingsService settings,
        MetadataIdentifyService identify,
        MetadataAutoMatchState state,
        AuditService audit,
        TimeProvider time,
        ILogger<MetadataAutoMatchService> logger,
        IEnumerable<IWorkDetector> detectors,
        IEnumerable<IMatchQueryPlanner> planners,
        IEnumerable<IMatchScorer> scorers,
        MetadataAutoMatchOptions? options = null,
        AutoMatchCoverComparer? covers = null,
        Declared.IDeclaredFactsReader? declared = null)
    {
        _db = db;
        _gateway = gateway;
        _budget = budget;
        _backoff = backoff;
        _settings = settings;
        _identify = identify;
        _state = state;
        _audit = audit;
        _time = time;
        _logger = logger;
        _detector = detectors.LastOrDefault();
        _planner = planners.LastOrDefault();
        _scorer = scorers.LastOrDefault();
        _providerAuthorFolders = (options ?? new MetadataAutoMatchOptions()).ProviderAuthorFolders;
        _declaredTypeFilter = (options ?? new MetadataAutoMatchOptions()).DeclaredTypeFilter;
        _covers = covers;
        _declared = declared;
    }

    /// <summary>True when the matcher-core implementations are registered.</summary>
    public bool MatcherAvailable => _detector is not null && _planner is not null && _scorer is not null;

    // --- Gate ---

    /// <summary>The global automatic gate (everything but the per-library Fetch switch); null when open.</summary>
    public async Task<AutomaticWait?> CheckGlobalGateAsync(CancellationToken ct = default)
    {
        if (_settings.NetworkDisabledByConfig)
            return new AutomaticWait("metadata_network_disabled", null);
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataEnabled, s.MetadataConsentVersion, s.MetadataAutoMatchEnabled, s.MetadataAutoConsentVersion, s.MetadataProvidersJson })
            .FirstOrDefaultAsync(ct);
        if (row is not { MetadataEnabled: true } || row.MetadataConsentVersion != MetadataConsent.CurrentVersion)
            return new AutomaticWait("metadata_disabled", null);
        if (!row.MetadataAutoMatchEnabled || row.MetadataAutoConsentVersion != MetadataAutoConsent.CurrentVersion)
            return new AutomaticWait("automatic_off", null);
        // Automatic matching and refresh ask MangaUpdates only: with it off the allowlist there is nothing to do.
        if (!MetadataProviderAllowlist.IsAllowed(row.MetadataProvidersJson, MetadataProviderAllowlist.MangaUpdates))
            return new AutomaticWait("provider_not_allowed", null);
        if (!MatcherAvailable)
            return new AutomaticWait("matcher_unavailable", null);
        if (await _backoff.ActiveUntilAsync(ct) is { } until)
            return new AutomaticWait("provider_backoff", until);
        if ((await _budget.GetAsync(ct)).Exhausted)
            return new AutomaticWait("budget_exhausted", _budget.Today().AddDays(1));
        return null;
    }

    /// <summary>The one boolean the post-scan hook reads: the global switch with the current automatic consent.</summary>
    public async Task<bool> IsAutomaticEnabledAsync(CancellationToken ct = default) =>
        await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => s.MetadataAutoMatchEnabled && s.MetadataAutoConsentVersion == MetadataAutoConsent.CurrentVersion)
            .FirstOrDefaultAsync(ct);

    public async Task<MatchThresholds> ThresholdsAsync(CancellationToken ct = default)
    {
        var row = await _db.AppSettings.AsNoTracking()
            .Where(s => s.Id == AppSettingsEntity.SingletonId)
            .Select(s => new { s.MetadataAutoTitleThreshold, s.MetadataMarginThreshold, s.MetadataReviewFloorThreshold })
            .FirstOrDefaultAsync(ct);
        return MetadataThresholds.Resolve(row?.MetadataAutoTitleThreshold, row?.MetadataMarginThreshold, row?.MetadataReviewFloorThreshold);
    }

    // --- Detection and enqueueing ---

    /// <summary>
    /// The library tree for classification, cached per catalog revision; the provider authors it carries
    /// (the artist-folder rule, 1.28.0) are re-read when the library's links or linked records changed.
    /// </summary>
    public async Task<LibraryTreeSnapshot> SnapshotAsync(long libraryId, CancellationToken ct)
    {
        var revision = await _db.Libraries.AsNoTracking().Where(l => l.Id == libraryId).Select(l => l.CatalogRevision).FirstOrDefaultAsync(ct);
        if (_state.CachedSnapshot(libraryId, revision) is { } cached)
        {
            if (!_providerAuthorFolders)
                return cached.Authors.Names.Count == 0 ? cached : cached.WithAuthors(LibraryTreeSnapshot.ProviderAuthorSet.None);
            if (cached.Authors.Stamp == await LibraryTreeSnapshot.LinkStampAsync(_db, libraryId, ct))
                return cached;
            var refreshed = cached.WithAuthors(await LibraryTreeSnapshot.LoadProviderAuthorsAsync(_db, libraryId, ct));
            _state.Cache(refreshed);
            return refreshed;
        }
        var snapshot = await LoadTreeAsync(libraryId, ct);
        _state.Cache(snapshot);
        return snapshot;
    }

    private Task<LibraryTreeSnapshot> LoadTreeAsync(long libraryId, CancellationToken ct) =>
        LibraryTreeSnapshot.LoadAsync(_db, libraryId, ct, providerAuthors: _providerAuthorFolders);

    private async Task<Dictionary<long, SeriesLinkState>> OwnLinksAsync(long libraryId, CancellationToken ct) =>
        await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.LibraryId == libraryId)
            .ToDictionaryAsync(l => l.NodeId, l => (SeriesLinkState)l.State, ct);

    /// <summary>
    /// Post-scan: queues the works among the folders created since
    /// <paramref name="since"/> and their parents (a new child can change a parent's
    /// class), and the folders that gained archives (a new doujin in an artist folder
    /// is its own work). Works that already have a queue row are skipped, so a new
    /// chapter below a linked or decided series costs nothing. Only called when
    /// automatic matching is on. Returns how many works were queued (0 = no run is created).
    /// </summary>
    public async Task<int> EnqueueNewFoldersAsync(long libraryId, DateTimeOffset since, CancellationToken ct = default)
    {
        if (_detector is null)
            return 0;
        var folder = (int)CatalogNodeKind.Folder;
        var tombstoned = (int)CatalogNodeAvailability.Tombstoned;
        var created = await _db.CatalogNodes.AsNoTracking()
            .Where(n => n.LibraryId == libraryId && n.Availability != tombstoned && n.CreatedAt >= since)
            .Select(n => new { n.Id, n.ParentId, n.Kind })
            .ToListAsync(ct);
        if (created.Count == 0)
            return 0;

        var scope = new HashSet<long>(created.Where(c => c.Kind == folder).Select(c => c.Id));
        foreach (var c in created)
            if (c.ParentId is { } parent)
                scope.Add(parent);

        var tree = await LoadTreeAsync(libraryId, ct);
        _state.Cache(tree);
        var works = AutoMatchWorkSelector.Select(tree, _detector, await OwnLinksAsync(libraryId, ct), scope);
        return await EnqueueAsync(libraryId, works, QueueReason.NewFolder, MetadataMatchRunTrigger.Scan, reviewFirst: false, requeueUnmatched: false, ct);
    }

    /// <summary>Everything "Match this library now" would queue (no network).</summary>
    public async Task<(IReadOnlyList<DetectedWork> Works, int AlreadyLinked, int Unmatched)> DetectLibraryAsync(
        long libraryId, bool retryUnmatched, CancellationToken ct)
    {
        if (_detector is null)
            return ([], 0, 0);
        var tree = await SnapshotAsync(libraryId, ct);
        var links = await OwnLinksAsync(libraryId, ct);
        var all = AutoMatchWorkSelector.Select(tree, _detector, links, null);
        var rows = await _db.MetadataMatchQueue.AsNoTracking()
            .Where(q => q.LibraryId == libraryId)
            .Select(q => new { q.NodeId, q.State, q.Outcome })
            .ToDictionaryAsync(q => q.NodeId, ct);

        var fresh = new List<DetectedWork>();
        var unmatched = 0;
        foreach (var w in all)
        {
            if (!rows.TryGetValue(w.AnchorNodeId, out var row))
            {
                fresh.Add(w);
                continue;
            }
            if (row.Outcome == (int)MatchBand.Unmatched && row.State is QueueState.Done or QueueState.Pending)
            {
                unmatched++;
                if (retryUnmatched)
                    fresh.Add(w);
            }
            else if (row.State is QueueState.Failed or QueueState.Cancelled or QueueState.Skipped)
            {
                fresh.Add(w);
            }
        }
        var linked = links.Count(l => l.Value != SeriesLinkState.NeedsReview);
        return (fresh, linked, unmatched);
    }

    public async Task<MetadataMatchEstimateDto?> EstimateAsync(string libraryPublicId, bool retryUnmatched, CancellationToken ct = default)
    {
        var library = await _db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.PublicId == libraryPublicId, ct);
        if (library is null)
            return null;
        var (works, linked, unmatched) = await DetectLibraryAsync(library.Id, retryUnmatched, ct);
        var budget = await _budget.GetAsync(ct);
        var requests = (int)Math.Ceiling(works.Count * AutoMatchPolicy.EstimatedRequestsPerWork);
        var wait = await CheckGlobalGateAsync(ct);
        var code = wait is { Code: "metadata_network_disabled" or "metadata_disabled" or "automatic_off" or "matcher_unavailable" } ? wait.Code
            : !library.MetadataEnabled ? "library_metadata_disabled"
            : null;
        return new MetadataMatchEstimateDto
        {
            LibraryId = library.PublicId,
            Candidates = works.Count,
            EstimatedRequests = requests,
            EstimatedDays = budget.Limit <= 0 ? 0 : Math.Round((double)requests / budget.Limit, 1),
            AlreadyLinked = linked,
            Unmatched = unmatched,
            DailyBudget = budget.Limit,
            BudgetUsedToday = budget.Used,
            FirstRun = !await _db.MetadataMatchRuns.AnyAsync(r => r.LibraryId == library.Id && r.Trigger == (int)MetadataMatchRunTrigger.Bulk, ct),
            AutomaticAvailable = code is null,
            UnavailableCode = code,
        };
    }

    /// <summary>"Match this library now": queues every detected work of the library in one Bulk run. Null when the library does not exist.</summary>
    public async Task<(string? Error, MetadataMatchRunDto? Run)> StartBulkAsync(
        string libraryPublicId, MetadataMatchLibraryRequest request, string? actor, CancellationToken ct = default)
    {
        var library = await _db.Libraries.AsNoTracking().FirstOrDefaultAsync(l => l.PublicId == libraryPublicId, ct);
        if (library is null)
            return ("not_found", null);
        if (!MatcherAvailable)
            return ("matcher_unavailable", null);
        var (works, _, _) = await DetectLibraryAsync(library.Id, request.RetryUnmatched, ct);
        if (works.Count == 0)
            return ("nothing_to_match", null);

        var runId = await CreateRunAsync(library.Id, MetadataMatchRunTrigger.Bulk, request.ReviewFirst, works.Count, ct);
        await EnqueueIntoRunAsync(library.Id, runId, works, QueueReason.Bulk, request.ReviewFirst, requeue: true, ct);
        await _audit.RecordAsync(AuditActions.MetadataMatchLibrary, request.ReviewFirst ? "review_first" : AuditResults.Success, actor, ct: ct,
            targetLibraryId: library.Id);
        _logger.LogInformation(LogEvents.Metadata.AutoMatchQueued, "Automatic matching: {Count} works queued for library {LibraryId} (bulk run {RunId})",
            works.Count, library.Id, runId);
        _state.Signal();
        return (null, await RunDtoAsync(runId, ct));
    }

    private async Task<int> EnqueueAsync(long libraryId, IReadOnlyList<DetectedWork> works, int reason, MetadataMatchRunTrigger trigger,
        bool reviewFirst, bool requeueUnmatched, CancellationToken ct)
    {
        var existing = await _db.MetadataMatchQueue.AsNoTracking()
            .Where(q => q.LibraryId == libraryId)
            .Select(q => q.NodeId)
            .ToListAsync(ct);
        var known = existing.ToHashSet();
        var fresh = works.Where(w => !known.Contains(w.AnchorNodeId)).ToList();
        if (fresh.Count == 0)
            return 0;
        var runId = await CreateRunAsync(libraryId, trigger, reviewFirst, fresh.Count, ct);
        await EnqueueIntoRunAsync(libraryId, runId, fresh, reason, reviewFirst, requeueUnmatched, ct);
        _logger.LogInformation(LogEvents.Metadata.AutoMatchQueued, "Automatic matching: {Count} works queued for library {LibraryId} (run {RunId})",
            fresh.Count, libraryId, runId);
        _state.Signal();
        return fresh.Count;
    }

    /// <summary>Inserts (or, with <paramref name="requeue"/>, resets) one queue row per work.</summary>
    private async Task EnqueueIntoRunAsync(long libraryId, long runId, IReadOnlyList<DetectedWork> works, int reason,
        bool reviewFirst, bool requeue, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        var anchors = works.Select(w => w.AnchorNodeId).ToList();
        var rows = new Dictionary<long, MetadataMatchQueueEntity>();
        foreach (var chunk in anchors.Chunk(500))
        {
            var ids = chunk.ToList();
            foreach (var row in await _db.MetadataMatchQueue.Where(q => ids.Contains(q.NodeId)).ToListAsync(ct))
                rows[row.NodeId] = row;
        }

        var queued = 0;
        foreach (var work in works)
        {
            if (rows.TryGetValue(work.AnchorNodeId, out var row))
            {
                if (!requeue || row.State is QueueState.Pending or QueueState.Leased && row.Outcome is null)
                    continue;
            }
            else
            {
                row = new MetadataMatchQueueEntity { NodeId = work.AnchorNodeId, LibraryId = libraryId };
                _db.MetadataMatchQueue.Add(row);
                rows[work.AnchorNodeId] = row;
            }
            row.Reason = reason;
            row.State = QueueState.Pending;
            row.Level = (int)work.Level;
            row.WorkClass = (int)work.Class;
            row.MemberNodeIdsJson = work.MemberNodeIds.Count == 0 ? null : JsonSerializer.Serialize(work.MemberNodeIds);
            row.Attempts = 0;
            row.NotBefore = null;
            row.LeaseUntil = null;
            row.LeaseOwner = null;
            row.LastErrorCode = null;
            row.RunId = runId;
            row.ReviewFirst = reviewFirst;
            row.EnqueuedAt = now;
            row.CompletedAt = null;
            queued++;
        }
        await _db.SaveChangesAsync(ct);
        await _db.MetadataMatchRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Queued, r => r.Queued + queued), ct);
    }

    /// <summary>"Re-run matching" on review rows: re-queues each node (one Rerun run per library). Returns per-node codes.</summary>
    public async Task<Dictionary<string, string>> RerunAsync(IReadOnlyList<string> nodePublicIds, string? actor, CancellationToken ct = default)
    {
        var codes = new Dictionary<string, string>(StringComparer.Ordinal);
        var nodes = await _db.CatalogNodes.AsNoTracking()
            .Where(n => nodePublicIds.Contains(n.PublicId))
            .Select(n => new { n.Id, n.PublicId, n.LibraryId, n.Kind, n.Availability })
            .ToListAsync(ct);
        foreach (var id in nodePublicIds)
            codes[id] = "not_found";

        foreach (var group in nodes.Where(n => n.Availability != (int)CatalogNodeAvailability.Tombstoned).GroupBy(n => n.LibraryId))
        {
            var ids = group.Select(n => n.Id).ToList();
            var locked = await _db.NodeSeriesLinks.AsNoTracking()
                .Where(l => ids.Contains(l.NodeId) && l.State != (int)SeriesLinkState.NeedsReview)
                .Select(l => l.NodeId)
                .ToListAsync(ct);
            var existing = await _db.MetadataMatchQueue.AsNoTracking()
                .Where(q => ids.Contains(q.NodeId))
                .ToDictionaryAsync(q => q.NodeId, ct);
            var works = new List<DetectedWork>();
            foreach (var n in group)
            {
                if (locked.Contains(n.Id))
                {
                    // Confirmed / Auto / Don't match are admin decisions: unlink first.
                    codes[n.PublicId] = "linked";
                    continue;
                }
                var level = existing.TryGetValue(n.Id, out var row) ? (MatchLevel)row.Level
                    : n.Kind == (int)CatalogNodeKind.Folder ? MatchLevel.Folder : MatchLevel.Archive;
                var cls = row?.WorkClass is { } c ? (WorkClass)c : WorkClass.Excluded;
                var members = row?.MemberNodeIdsJson is { } json ? JsonSerializer.Deserialize<List<long>>(json) ?? [] : [];
                works.Add(new DetectedWork(n.Id, n.Id, level, cls, members));
                codes[n.PublicId] = "ok";
            }
            if (works.Count == 0)
                continue;
            var runId = await CreateRunAsync(group.Key, MetadataMatchRunTrigger.Rerun, reviewFirst: false, works.Count, ct);
            await EnqueueIntoRunAsync(group.Key, runId, works, QueueReason.Rerun, reviewFirst: false, requeue: true, ct);
            await _audit.RecordAsync(AuditActions.MetadataMatchRerun, AuditResults.Success, actor, ct: ct, targetLibraryId: group.Key);
        }
        _state.Signal();
        return codes;
    }

    /// <summary>Above this many works a Content change asks before it re-queues them.</summary>
    public const int ContentRematchConfirmAbove = 200;

    /// <summary>
    /// The Needs-review and Unmatched works at or below <paramref name="folderId"/> whose effective
    /// Content comes from it (subtrees of folders with their own Content row are skipped).
    /// </summary>
    public async Task<IReadOnlyList<long>> ContentRematchCandidatesAsync(long folderId, CancellationToken ct = default)
    {
        var libraryId = await _db.CatalogNodes.AsNoTracking().Where(n => n.Id == folderId).Select(n => n.LibraryId).FirstAsync(ct);
        var tree = await LibraryTreeSnapshot.LoadAsync(_db, libraryId, ct);
        var overrides = (await _db.FolderMetadataContents.AsNoTracking().Select(c => c.NodeId).ToListAsync(ct)).ToHashSet();
        var scope = new List<long>();
        var stack = new Stack<long>([folderId]);
        while (stack.Count > 0)
        {
            var id = stack.Pop();
            scope.Add(id);
            foreach (var child in tree.ChildrenOf(id))
                if (!(child.IsFolder && overrides.Contains(child.Id)))
                    stack.Push(child.Id);
        }

        var needsReview = (int)SeriesLinkState.NeedsReview;
        var unmatched = (int)MatchBand.Unmatched;
        var found = new HashSet<long>();
        foreach (var chunk in scope.Chunk(500))
        {
            var ids = chunk.ToList();
            found.UnionWith(await _db.NodeSeriesLinks.AsNoTracking()
                .Where(l => ids.Contains(l.NodeId) && l.State == needsReview)
                .Select(l => l.NodeId)
                .ToListAsync(ct));
            found.UnionWith(await _db.MetadataMatchQueue.AsNoTracking()
                .Where(q => ids.Contains(q.NodeId)
                    && ((q.Outcome == unmatched && q.State == QueueState.Done) || q.State == QueueState.Failed)
                    && !_db.NodeSeriesLinks.Any(l => l.NodeId == q.NodeId))
                .Select(q => q.NodeId)
                .ToListAsync(ct));
        }
        return found.Order().ToList();
    }

    /// <summary>
    /// Re-queues the works <see cref="ContentRematchCandidatesAsync"/> finds, when automatic matching is
    /// on and (unless <paramref name="confirmed"/>) there are at most <see cref="ContentRematchConfirmAbove"/>.
    /// </summary>
    public async Task<MetadataContentRematchDto> RematchBelowAsync(long folderId, bool confirmed, string? actor, CancellationToken ct = default)
    {
        var nodes = await ContentRematchCandidatesAsync(folderId, ct);
        if (nodes.Count == 0)
            return new MetadataContentRematchDto { Affected = 0, Queued = 0 };
        if (!await IsAutomaticEnabledAsync(ct))
            return new MetadataContentRematchDto { Affected = nodes.Count, Queued = 0, AutomaticOff = true };
        if (!confirmed && nodes.Count > ContentRematchConfirmAbove)
            return new MetadataContentRematchDto { Affected = nodes.Count, Queued = 0, NeedsConfirmation = true };

        var publicIds = new List<string>();
        foreach (var chunk in nodes.Chunk(500))
        {
            var ids = chunk.ToList();
            publicIds.AddRange(await _db.CatalogNodes.AsNoTracking().Where(n => ids.Contains(n.Id)).Select(n => n.PublicId).ToListAsync(ct));
        }
        var codes = await RerunAsync(publicIds, actor, ct);
        return new MetadataContentRematchDto { Affected = nodes.Count, Queued = codes.Values.Count(c => c == "ok") };
    }

    /// <summary>Unmatched works whose retry date came go back to the queue (one Retry run per library).</summary>
    public async Task<int> PromoteDueRetriesAsync(CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        var due = await _db.MetadataMatchQueue
            .Where(q => q.State == QueueState.Done && q.Outcome == (int)MatchBand.Unmatched && q.NotBefore != null && q.NotBefore <= now)
            .ToListAsync(ct);
        if (due.Count == 0)
            return 0;
        foreach (var group in due.GroupBy(q => q.LibraryId))
        {
            var runId = await CreateRunAsync(group.Key, MetadataMatchRunTrigger.Retry, reviewFirst: false, group.Count(), ct);
            foreach (var row in group)
            {
                row.State = QueueState.Pending;
                row.Reason = QueueReason.Retry;
                row.NotBefore = null;
                row.Attempts = 0;
                row.RunId = runId;
                row.EnqueuedAt = now;
            }
            await _db.MetadataMatchRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Queued, group.Count()), ct);
        }
        await _db.SaveChangesAsync(ct);
        return due.Count;
    }

    private async Task<long> CreateRunAsync(long libraryId, MetadataMatchRunTrigger trigger, bool reviewFirst, int candidates, CancellationToken ct)
    {
        var run = new MetadataMatchRunEntity
        {
            PublicId = "mm" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
            LibraryId = libraryId,
            Trigger = (int)trigger,
            Status = (int)MetadataMatchRunStatus.Running,
            ReviewFirst = reviewFirst,
            StartedAt = _time.GetUtcNow(),
            Candidates = candidates,
        };
        _db.MetadataMatchRuns.Add(run);
        await _db.SaveChangesAsync(ct);
        return run.Id;
    }

    // --- Leasing and processing ---

    /// <summary>
    /// Leases the next due row of a library whose Fetch switch is on (re-runs first,
    /// then new folders, bulk, retries; oldest first). An expired lease counts as
    /// pending (crash / restart resume). Null when nothing is due.
    /// </summary>
    public async Task<MetadataMatchQueueEntity?> LeaseNextAsync(string owner, CancellationToken ct = default)
    {
        var now = _time.GetUtcNow();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var next = await _db.MetadataMatchQueue.AsNoTracking()
                .Where(q => (q.State == QueueState.Pending || (q.State == QueueState.Leased && q.LeaseUntil < now))
                    && (q.NotBefore == null || q.NotBefore <= now)
                    && _db.Libraries.Any(l => l.Id == q.LibraryId && l.MetadataEnabled))
                .OrderBy(QueueReason.Priority)
                .ThenBy(q => q.EnqueuedAt)
                .ThenBy(q => q.Id)
                .Select(q => new { q.Id })
                .FirstOrDefaultAsync(ct);
            if (next is null)
                return null;

            var until = now + AutoMatchPolicy.LeaseDuration;
            var taken = await _db.MetadataMatchQueue
                .Where(q => q.Id == next.Id && (q.State == QueueState.Pending || (q.State == QueueState.Leased && q.LeaseUntil < now)))
                .ExecuteUpdateAsync(s => s
                    .SetProperty(q => q.State, QueueState.Leased)
                    .SetProperty(q => q.LeaseUntil, until)
                    .SetProperty(q => q.LeaseOwner, owner), ct);
            if (taken == 1)
                return await _db.MetadataMatchQueue.AsNoTracking().FirstAsync(q => q.Id == next.Id, ct);
        }
        return null;
    }

    /// <summary>Hands a leased row back unchanged (a refusal: the gate closed while it was being processed).</summary>
    public async Task ReleaseAsync(long queueId, CancellationToken ct = default) =>
        await _db.MetadataMatchQueue.Where(q => q.Id == queueId && q.State == QueueState.Leased)
            .ExecuteUpdateAsync(s => s
                .SetProperty(q => q.State, QueueState.Pending)
                .SetProperty(q => q.LeaseUntil, (DateTimeOffset?)null)
                .SetProperty(q => q.LeaseOwner, (string?)null), ct);

    /// <summary>
    /// Processes one leased row: re-checks it, looks the work up, and writes the
    /// outcome. A gateway REFUSAL (switch off, budget, backoff) releases the row and
    /// is rethrown so the worker waits; a provider FAILURE counts an attempt.
    /// </summary>
    public async Task ProcessAsync(MetadataMatchQueueEntity row, CancellationToken ct = default)
    {
        if (_detector is null || _planner is null || _scorer is null)
        {
            await ReleaseAsync(row.Id, ct);
            return;
        }

        var tree = await SnapshotAsync(row.LibraryId, ct);
        var work = await RecheckAsync(row, tree, ct);
        if (work is null)
        {
            var handedOver = await HandOverToArchivesAsync(row, tree, ct);
            await FinishAsync(row.Id, row.RunId, QueueState.Skipped, null, 0, null, 0, ct);
            _logger.LogDebug(LogEvents.Metadata.AutoMatchSkipped, "Automatic matching skipped node {NodeId} ({Works} archive works queued instead)",
                row.NodeId, handedOver);
            return;
        }

        var call = MetadataCallContext.Automatic();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        WorkLookupResult lookup;
        try
        {
            var allowDoujinshi = await EffectiveContentAsync(work.Work.FolderId, ct) == MetadataFolderContent.DoujinshiAndAdultOneShots;
            var declared = _declared is null ? null
                : (await _declared.EffectiveForLibraryAsync(row.LibraryId, ct)).GetValueOrDefault(work.Work.FolderId);
            var lookupEngine = new AutoMatchLookup(_db, _gateway, _planner, _scorer, _covers, _declaredTypeFilter);
            lookup = await lookupEngine.LookupAsync(tree, work.Work, work.Classification, await ThresholdsAsync(ct), allowDoujinshi, call, ct,
                declared);
        }
        catch (MetadataGatewayException ex) when (IsRefusal(ex))
        {
            await AddRequestsAsync(row.RunId, call.RequestsSent, ct);
            await ReleaseAsync(row.Id, ct);
            throw;
        }
        catch (MetadataGatewayException ex)
        {
            await AddRequestsAsync(row.RunId, call.RequestsSent, ct);
            await FailAttemptAsync(row, ex.Code, ct);
            return;
        }

        await WriteOutcomeAsync(row, work, lookup, call, ct);
        // A decided folder-level work speaks for its subtree: archive results inside it are retired.
        if (work.Work.Level is MatchLevel.Folder or MatchLevel.ReviewOnly)
            await CoveredWorkRetirement.RetireBelowAsync(_db, tree, work.Work.AnchorNodeId, ct);
        _logger.LogInformation(LogEvents.Metadata.AutoMatchDecided,
            "Automatic matching decided node {NodeId}: {Band} ({Requests} requests, {Covers} covers compared: {CoverCheck}, {ElapsedMs} ms)",
            row.NodeId, lookup.Outcome.Band, call.RequestsSent, lookup.CoversCompared, lookup.CoverCheck, watch.ElapsedMilliseconds);
    }

    /// <summary>
    /// A folder queued at folder or review level that is matched archive by archive NOW (1.28.0: an artist folder
    /// whose author was linked in the library after it was queued, e.g. earlier in the same run) hands over to its
    /// archive works in the same run instead of being dropped. Returns how many works were queued.
    /// </summary>
    private async Task<int> HandOverToArchivesAsync(MetadataMatchQueueEntity row, LibraryTreeSnapshot tree, CancellationToken ct)
    {
        if (row.RunId is not { } runId || _detector is null || tree.Find(row.NodeId) is not { IsFolder: true } node)
            return 0;
        var classification = _detector.Classify(tree.ShapeOf(node.Id));
        if (classification.Level != MatchLevel.Archive)
            return 0;
        var links = await OwnLinksAsync(row.LibraryId, ct);
        if (tree.Ancestors(node.Id).Prepend(node).Any(n => links.ContainsKey(n.Id)))
            return 0; // The folder or an ancestor has a link row: it speaks for the archives.
        var works = AutoMatchWorkSelector.ArchiveWorks(tree, node.Id, classification, links).ToList();
        var anchors = works.Select(w => w.AnchorNodeId).ToList();
        var known = (await _db.MetadataMatchQueue.AsNoTracking().Where(q => anchors.Contains(q.NodeId)).Select(q => q.NodeId).ToListAsync(ct)).ToHashSet();
        var fresh = works.Where(w => !known.Contains(w.AnchorNodeId)).ToList();
        if (fresh.Count == 0)
            return 0;
        await EnqueueIntoRunAsync(row.LibraryId, runId, fresh, row.Reason, row.ReviewFirst, requeue: false, ct);
        await _db.MetadataMatchRuns.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Candidates, r => r.Candidates + fresh.Count), ct);
        return fresh.Count;
    }

    /// <summary>
    /// Local refusals (nothing was wrong with the provider): the row waits, no attempt is counted. A site removed from
    /// the provider allowlist is one too - before 1.29.0 it counted as a failure, so removing MangaUpdates failed the
    /// queued works (and marked refreshed records failed) instead of pausing them.
    /// </summary>
    public static bool IsRefusal(MetadataGatewayException ex) => ex.Code is
        "metadata_network_disabled" or "metadata_disabled" or "library_metadata_disabled" or "automatic_off"
        or "budget_exhausted" or "provider_backoff" or "provider_busy" or "provider_not_allowed";

    private sealed record CheckedWork(DetectedWork Work, WorkClassification Classification);

    /// <summary>The row's work as it is NOW, or null when it is no longer something to match.</summary>
    private async Task<CheckedWork?> RecheckAsync(MetadataMatchQueueEntity row, LibraryTreeSnapshot tree, CancellationToken ct)
    {
        var node = tree.Find(row.NodeId);
        if (node is null)
            return null; // Removed (tombstoned) since it was queued.

        // Own link: only a re-run of a Needs review row may proceed.
        var chain = tree.Ancestors(row.NodeId).Select(a => a.Id).Prepend(row.NodeId).ToList();
        var links = await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => chain.Contains(l.NodeId))
            .Select(l => new { l.NodeId, l.State })
            .ToListAsync(ct);
        if (links.FirstOrDefault(l => l.NodeId == row.NodeId) is { } own
            && !(row.Reason == QueueReason.Rerun && own.State == (int)SeriesLinkState.NeedsReview))
            return null;
        if (links.Any(l => l.NodeId != row.NodeId))
            return null; // Inherits from a linked / Don't match / in-review ancestor.

        var rerun = row.Reason == QueueReason.Rerun;
        if (node.IsFolder)
        {
            var classification = _detector!.Classify(tree.ShapeOf(node.Id));
            var level = classification.Level switch
            {
                MatchLevel.Folder or MatchLevel.ReviewOnly => classification.Level,
                _ when rerun => MatchLevel.ReviewOnly, // An admin asked: match it, but never auto.
                _ => MatchLevel.None,
            };
            return level == MatchLevel.None
                ? null
                : new CheckedWork(new DetectedWork(node.Id, node.Id, level, classification.Class, []), classification);
        }

        if (node.ParentId is not { } parentId || tree.Find(parentId) is null)
            return rerun ? LooseArchive(node) : null;
        var parentClass = _detector!.Classify(tree.ShapeOf(parentId));
        var ownLinks = await _db.NodeSeriesLinks.AsNoTracking()
            .Where(l => l.LibraryId == row.LibraryId && l.NodeId != row.NodeId)
            .Select(l => new { l.NodeId, l.State })
            .ToDictionaryAsync(l => l.NodeId, l => (SeriesLinkState)l.State, ct);
        if (parentClass.Level == MatchLevel.Archive)
        {
            var work = AutoMatchWorkSelector.ArchiveWorks(tree, parentId, parentClass, ownLinks)
                .FirstOrDefault(w => w.AnchorNodeId == node.Id || w.MemberNodeIds.Contains(node.Id));
            if (work is not null)
            {
                // Keep this row's node as the anchor even when the group's first archive changed.
                var members = work.MemberNodeIds.Prepend(work.AnchorNodeId).Where(id => id != node.Id).ToList();
                return new CheckedWork(work with { AnchorNodeId = node.Id, MemberNodeIds = members }, parentClass);
            }
        }
        return rerun ? LooseArchive(node) : null;

        CheckedWork LooseArchive(LibraryTreeSnapshot.Node archive)
        {
            var single = new WorkClassification(WorkClass.Ambiguous, MatchLevel.ReviewOnly, ["rerun"],
                [new ArchiveGroup(archive.Name, [0])]);
            return new CheckedWork(new DetectedWork(archive.Id, archive.ParentId ?? archive.Id, MatchLevel.Archive, WorkClass.Ambiguous, []), single);
        }
    }

    /// <summary>The effective folder Content (nearest folder with a row wins; default Auto).</summary>
    public async Task<MetadataFolderContent> EffectiveContentAsync(long folderId, CancellationToken ct)
    {
        var (content, _) = await MetadataFolderContentService.ResolveAsync(_db, folderId, ct);
        return content;
    }

    /// <summary>ONE transaction: link rows / candidates / retry date, the queue row and the run counters. Idempotent.</summary>
    private async Task WriteOutcomeAsync(MetadataMatchQueueEntity row, CheckedWork work, WorkLookupResult lookup,
        MetadataCallContext call, CancellationToken ct)
    {
        var outcome = lookup.Outcome;
        var band = outcome.Band;
        var reasons = outcome.Ranked.Count > 0 ? outcome.Ranked[0].Reasons : MatchReason.None;
        if (band == MatchBand.Auto && work.Work.Level == MatchLevel.ReviewOnly)
        {
            band = MatchBand.NeedsReview;
            reasons |= MatchReason.ReviewOnlyClass;
        }
        else if (band == MatchBand.Auto && row.ReviewFirst)
        {
            band = MatchBand.NeedsReview;
        }
        var top = outcome.Ranked.FirstOrDefault();
        if (band == MatchBand.Auto && (top is null || !lookup.Fetched.ContainsKey(top.Candidate.ExternalId)))
            band = MatchBand.NeedsReview; // Never link to a record we could not read.

        var now = _time.GetUtcNow();
        MetadataRecordEntity? record = null;
        var anchorIds = work.Work.MemberNodeIds.Prepend(work.Work.AnchorNodeId).ToList();

        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            // Idempotency: a link made meanwhile (an admin, or a previous attempt of this row) wins.
            var own = await _db.NodeSeriesLinks.Where(l => anchorIds.Contains(l.NodeId)).ToListAsync(ct);
            var anchorLink = own.FirstOrDefault(l => l.NodeId == work.Work.AnchorNodeId);
            if (anchorLink is not null && anchorLink.State != (int)SeriesLinkState.NeedsReview)
            {
                await tx.RollbackAsync(ct);
                await FinishAsync(row.Id, row.RunId, QueueState.Skipped, null, 0, null, call.RequestsSent, ct);
                return;
            }

            await _db.MetadataMatchCandidates.Where(c => c.NodeId == work.Work.AnchorNodeId).ExecuteDeleteAsync(ct);
            switch (band)
            {
                case MatchBand.Auto:
                    record = await UpsertRecordAsync(lookup.Fetched[top!.Candidate.ExternalId], now, ct);
                    foreach (var nodeId in anchorIds)
                    {
                        var link = own.FirstOrDefault(l => l.NodeId == nodeId);
                        if (link is not null && link.State != (int)SeriesLinkState.NeedsReview)
                            continue; // A member linked on its own keeps its link.
                        if (link is null)
                        {
                            link = new NodeSeriesLinkEntity { NodeId = nodeId, LibraryId = row.LibraryId, CreatedAt = now };
                            _db.NodeSeriesLinks.Add(link);
                        }
                        link.State = (int)SeriesLinkState.Auto;
                        link.RecordId = record.Id;
                        link.MatchMethod = (int)MetadataMatchMethod.Auto;
                        link.MatchScore = Math.Round(Math.Clamp(top.TitleScore, 0, 1), 4);
                        link.UpdatedAt = now;
                    }
                    break;

                case MatchBand.NeedsReview:
                    if (anchorLink is null)
                    {
                        anchorLink = new NodeSeriesLinkEntity { NodeId = work.Work.AnchorNodeId, LibraryId = row.LibraryId, CreatedAt = now };
                        _db.NodeSeriesLinks.Add(anchorLink);
                    }
                    anchorLink.State = (int)SeriesLinkState.NeedsReview;
                    anchorLink.RecordId = null;
                    anchorLink.MatchMethod = (int)MetadataMatchMethod.Auto;
                    anchorLink.MatchScore = top is null ? null : Math.Round(Math.Clamp(top.TitleScore, 0, 1), 4);
                    anchorLink.UpdatedAt = now;
                    AddCandidates(work.Work.AnchorNodeId, outcome.ToPersist.Count > 0 ? outcome.ToPersist : outcome.Ranked.Take(1).ToList(), lookup, now);
                    break;

                default:
                    if (anchorLink is not null)
                        _db.NodeSeriesLinks.Remove(anchorLink); // A re-run that found nothing any more.
                    AddCandidates(work.Work.AnchorNodeId, outcome.ToPersist, lookup, now);
                    break;
            }

            var queue = await _db.MetadataMatchQueue.FirstAsync(q => q.Id == row.Id, ct);
            queue.State = QueueState.Done;
            queue.Outcome = (int)band;
            queue.OutcomeReasons = (int)reasons;
            queue.Level = (int)work.Work.Level;
            queue.WorkClass = (int)work.Classification.Class;
            queue.MemberNodeIdsJson = work.Work.MemberNodeIds.Count == 0 ? null : JsonSerializer.Serialize(work.Work.MemberNodeIds);
            queue.CompletedAt = now;
            queue.LeaseUntil = null;
            queue.LeaseOwner = null;
            queue.LastErrorCode = null;
            if (band == MatchBand.Unmatched)
            {
                queue.NotBefore = queue.RetryStep < AutoMatchPolicy.UnmatchedRetry.Length ? now + AutoMatchPolicy.UnmatchedRetry[queue.RetryStep] : null;
                queue.RetryStep = Math.Min(queue.RetryStep + 1, AutoMatchPolicy.UnmatchedRetry.Length);
            }
            else
            {
                queue.NotBefore = null;
            }
            await _db.SaveChangesAsync(ct);

            if (row.RunId is { } runId)
            {
                var requests = call.RequestsSent;
                await _db.MetadataMatchRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Processed, r => r.Processed + 1)
                    .SetProperty(r => r.AutoLinked, r => r.AutoLinked + (band == MatchBand.Auto ? 1 : 0))
                    .SetProperty(r => r.NeedsReview, r => r.NeedsReview + (band == MatchBand.NeedsReview ? 1 : 0))
                    .SetProperty(r => r.Unmatched, r => r.Unmatched + (band == MatchBand.Unmatched ? 1 : 0))
                    .SetProperty(r => r.RequestsUsed, r => r.RequestsUsed + requests), ct);
            }
            await tx.CommitAsync(ct);
        }

        // The poster (gated, automatic) after the commit: a failed image never undoes the link.
        if (record is { ImageState: not 1, ImageRemoteUrl: not null })
        {
            var before = call.RequestsSent;
            await _identify.TryStoreImageAsync(record, row.LibraryId, ct, call);
            await AddRequestsAsync(row.RunId, call.RequestsSent - before, ct);
        }
        await CompleteRunIfDoneAsync(row.RunId, ct);
    }

    private void AddCandidates(long nodeId, IReadOnlyList<ScoredCandidate> candidates, WorkLookupResult lookup, DateTimeOffset now)
    {
        var rank = 1;
        foreach (var c in candidates.Take(5))
        {
            lookup.Fetched.TryGetValue(c.Candidate.ExternalId, out var full);
            lookup.HitImages.TryGetValue(c.Candidate.ExternalId, out var hitImage);
            _db.MetadataMatchCandidates.Add(new MetadataMatchCandidateEntity
            {
                NodeId = nodeId,
                Rank = rank++,
                Provider = c.Candidate.Provider,
                ExternalId = c.Candidate.ExternalId,
                Title = Truncate(c.Candidate.Title, 512),
                ProviderType = full?.ProviderType ?? (c.Candidate.Origin is { } o ? Truncate(o, 32) : null),
                Format = (int?)c.Candidate.Format,
                Origin = (int?)(full?.Origin ?? Providers.MangaUpdates.MangaUpdatesMapping.OriginOf(c.Candidate.Origin)),
                Year = c.Candidate.StartYear,
                Volumes = c.Candidate.Volumes,
                TitleScore = Math.Round(Math.Clamp(c.TitleScore, 0, 1), 4),
                AdjustedScore = Math.Round(Math.Clamp(c.AdjustedScore, 0, 1), 4),
                Reasons = (int)c.Reasons,
                ImageRemoteUrl = full?.ImageRemoteUrl ?? hitImage,
                CreatedAt = now,
            });
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    private async Task<MetadataRecordEntity> UpsertRecordAsync(Providers.ProviderSeriesRecord fetched, DateTimeOffset now, CancellationToken ct)
    {
        var record = await _db.MetadataRecords.FirstOrDefaultAsync(r => r.Provider == fetched.Provider && r.ExternalId == fetched.ExternalId, ct);
        if (record is null)
        {
            record = new MetadataRecordEntity
            {
                PublicId = await _identify.NewPublicIdAsync(ct),
                Provider = fetched.Provider,
                ExternalId = fetched.ExternalId,
            };
            _db.MetadataRecords.Add(record);
        }
        MetadataIdentifyService.Apply(record, fetched, now);
        await _db.SaveChangesAsync(ct);
        return record;
    }

    private async Task FailAttemptAsync(MetadataMatchQueueEntity row, string code, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        var giveUp = attempts >= AutoMatchPolicy.MaxAttempts;
        var retryAt = _time.GetUtcNow() + AutoMatchPolicy.FailureRetry;
        await _db.MetadataMatchQueue.Where(q => q.Id == row.Id).ExecuteUpdateAsync(s => s
            .SetProperty(q => q.Attempts, attempts)
            .SetProperty(q => q.State, giveUp ? QueueState.Failed : QueueState.Pending)
            .SetProperty(q => q.NotBefore, giveUp ? null : retryAt)
            .SetProperty(q => q.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(q => q.LeaseOwner, (string?)null)
            .SetProperty(q => q.LastErrorCode, code.Length <= 32 ? code : code[..32]), ct);
        if (giveUp && row.RunId is { } runId)
            await _db.MetadataMatchRuns.Where(r => r.Id == runId).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Processed, r => r.Processed + 1)
                .SetProperty(r => r.Failed, r => r.Failed + 1), ct);
        _logger.LogWarning(LogEvents.Metadata.AutoMatchFailed, "Automatic matching of node {NodeId} failed: {Code} (attempt {Attempt})", row.NodeId, code, attempts);
        await CompleteRunIfDoneAsync(row.RunId, ct);
    }

    private async Task FinishAsync(long queueId, long? runId, int state, int? outcome, int reasons, DateTimeOffset? notBefore,
        int requests, CancellationToken ct)
    {
        var now = _time.GetUtcNow();
        await _db.MetadataMatchQueue.Where(q => q.Id == queueId).ExecuteUpdateAsync(s => s
            .SetProperty(q => q.State, state)
            .SetProperty(q => q.Outcome, outcome)
            .SetProperty(q => q.OutcomeReasons, reasons)
            .SetProperty(q => q.NotBefore, notBefore)
            .SetProperty(q => q.CompletedAt, now)
            .SetProperty(q => q.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(q => q.LeaseOwner, (string?)null), ct);
        if (runId is { } id)
            await _db.MetadataMatchRuns.Where(r => r.Id == id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Processed, r => r.Processed + 1)
                .SetProperty(r => r.Skipped, r => r.Skipped + (state == QueueState.Skipped ? 1 : 0))
                .SetProperty(r => r.RequestsUsed, r => r.RequestsUsed + requests), ct);
        await CompleteRunIfDoneAsync(runId, ct);
    }

    private async Task AddRequestsAsync(long? runId, int requests, CancellationToken ct)
    {
        if (runId is { } id && requests > 0)
            await _db.MetadataMatchRuns.Where(r => r.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.RequestsUsed, r => r.RequestsUsed + requests), ct);
    }

    /// <summary>Marks a running run completed once no row of it is pending or leased.</summary>
    public async Task CompleteRunIfDoneAsync(long? runId, CancellationToken ct = default)
    {
        if (runId is not { } id)
            return;
        var open = await _db.MetadataMatchQueue.AnyAsync(q => q.RunId == id && (q.State == QueueState.Pending || q.State == QueueState.Leased), ct);
        if (open)
            return;
        var now = _time.GetUtcNow();
        var done = await _db.MetadataMatchRuns.Where(r => r.Id == id && r.Status == (int)MetadataMatchRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, (int)MetadataMatchRunStatus.Completed)
                .SetProperty(r => r.CompletedAt, now), ct);
        if (done > 0)
            _logger.LogInformation(LogEvents.Metadata.AutoMatchRunCompleted, "Automatic matching run {RunId} completed", id);
    }

    // --- Runs ---

    public async Task<(string? Error, MetadataMatchRunDto? Run)> CancelRunAsync(string runPublicId, string? actor, CancellationToken ct = default)
    {
        var run = await _db.MetadataMatchRuns.FirstOrDefaultAsync(r => r.PublicId == runPublicId, ct);
        if (run is null)
            return ("not_found", null);
        if (run.Status == (int)MetadataMatchRunStatus.Running)
        {
            var cancelled = await _db.MetadataMatchQueue
                .Where(q => q.RunId == run.Id && q.State == QueueState.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(q => q.State, QueueState.Cancelled), ct);
            run.Status = (int)MetadataMatchRunStatus.Cancelled;
            run.CompletedAt = _time.GetUtcNow();
            run.Skipped += cancelled;
            await _db.SaveChangesAsync(ct);
            await _audit.RecordAsync(AuditActions.MetadataRunCancel, AuditResults.Success, actor, ct: ct, targetLibraryId: run.LibraryId);
        }
        return (null, await RunDtoAsync(run.Id, ct));
    }

    public async Task<MetadataMatchRunsDto> ListRunsAsync(string? libraryPublicId, string? cursor, int limit, CancellationToken ct = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = _db.MetadataMatchRuns.AsNoTracking();
        if (!string.IsNullOrEmpty(libraryPublicId))
        {
            var libraryId = await _db.Libraries.Where(l => l.PublicId == libraryPublicId).Select(l => (long?)l.Id).FirstOrDefaultAsync(ct) ?? -1;
            query = query.Where(r => r.LibraryId == libraryId);
        }
        if (long.TryParse(cursor, out var before))
            query = query.Where(r => r.Id < before);
        var runs = await query.OrderByDescending(r => r.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = runs.Count > limit;
        runs = runs.Take(limit).ToList();
        var names = await LibraryNamesAsync(runs.Select(r => r.LibraryId), ct);
        return new MetadataMatchRunsDto
        {
            Status = await StatusAsync(ct),
            Items = runs.Select(r => ToDto(r, names)).ToList(),
            HasMore = hasMore,
            NextCursor = hasMore ? runs[^1].Id.ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
        };
    }

    public async Task<MetadataAutoMatchStatusDto> StatusAsync(CancellationToken ct = default)
    {
        var wait = await CheckGlobalGateAsync(ct);
        var pending = await _db.MetadataMatchQueue.CountAsync(q => q.State == QueueState.Pending || q.State == QueueState.Leased, ct);
        return new MetadataAutoMatchStatusDto
        {
            Enabled = await IsAutomaticEnabledAsync(ct),
            Active = wait is null && pending > 0,
            WaitingCode = wait?.Code,
            WaitingUntil = wait?.Until,
            Pending = pending,
        };
    }

    private async Task<MetadataMatchRunDto?> RunDtoAsync(long runId, CancellationToken ct)
    {
        var run = await _db.MetadataMatchRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, ct);
        return run is null ? null : ToDto(run, await LibraryNamesAsync([run.LibraryId], ct));
    }

    private async Task<Dictionary<long, (string PublicId, string Name)>> LibraryNamesAsync(IEnumerable<long> ids, CancellationToken ct)
    {
        var list = ids.Distinct().ToList();
        return await _db.Libraries.AsNoTracking().Where(l => list.Contains(l.Id))
            .ToDictionaryAsync(l => l.Id, l => (l.PublicId, l.DisplayName), ct);
    }

    private static MetadataMatchRunDto ToDto(MetadataMatchRunEntity r, IReadOnlyDictionary<long, (string PublicId, string Name)> names) => new()
    {
        RunId = r.PublicId,
        LibraryId = names.TryGetValue(r.LibraryId, out var n) ? n.PublicId : string.Empty,
        LibraryName = names.TryGetValue(r.LibraryId, out var m) ? m.Name : string.Empty,
        Trigger = (MetadataMatchRunTrigger)r.Trigger,
        Status = (MetadataMatchRunStatus)r.Status,
        ReviewFirst = r.ReviewFirst,
        StartedAt = r.StartedAt,
        CompletedAt = r.CompletedAt,
        Candidates = r.Candidates,
        Queued = r.Queued,
        Processed = r.Processed,
        AutoLinked = r.AutoLinked,
        NeedsReview = r.NeedsReview,
        Unmatched = r.Unmatched,
        Skipped = r.Skipped,
        Failed = r.Failed,
        RequestsUsed = r.RequestsUsed,
        AutoChangedByAdmin = r.AutoChangedByAdmin,
        ReviewAcceptedTop = r.ReviewAcceptedTop,
        ReviewAcceptedOther = r.ReviewAcceptedOther,
        ReviewDontMatch = r.ReviewDontMatch,
    };
}
