namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using Microsoft.EntityFrameworkCore;

/// <summary>One index pass at a time (the builder, an admin creator change): rebuilds never interleave their writes.</summary>
public sealed class FacetIndexLock
{
    internal SemaphoreSlim Gate { get; } = new(1, 1);
}

public enum FacetRebuildOutcome
{
    Rebuilt = 0,

    /// <summary>The inputs key matched the stored one (not forced): nothing was read or written.</summary>
    Unchanged = 1,

    /// <summary>A scan of the library runs: nothing written; the builder tries again later.</summary>
    SkippedScanRunning = 2,

    /// <summary>The library no longer exists.</summary>
    LibraryGone = 3,
}

/// <summary>A rebuild's result: counts only.</summary>
public sealed record FacetRebuildResult(FacetRebuildOutcome Outcome, int Works = 0, int Entries = 0, int Inserted = 0, int Updated = 0,
    int Deleted = 0, long ElapsedMs = 0);

/// <summary>
/// The tag index builder of one library (1.45.0 lane F, D1 2.3). <see cref="RebuildLibraryAsync"/>: creators first (the global creator pass,
/// <see cref="CreatorSync"/> - owner Q5's condition), then the works (<see cref="WorkResolver"/> over the library's tree, links, current
/// ComicInfo and the matcher's own work detector), then each location's values (<see cref="FacetExtractor"/>), and finally a DIFF against
/// the stored entries - deletes, changed rows and inserts in small transactions (no update storm on a refresh day); works and values left
/// without entries are deleted at the end (creators are kept - stable ids; a merged creator's value stays as the redirect). Never in the
/// scan path: nothing is written while a scan of the library runs (checked before the pass and before every batch). No network.
/// </summary>
public sealed class FacetIndexService
{
    /// <summary>
    /// The index rules version: part of every inputs key, so a release that changes the rules rebuilds every library once. 2 = 1.49.0
    /// (lane T): the Tags bucket (record categories + ComicInfo Tags).
    /// </summary>
    public const int RulesVersion = 2;

    /// <summary>Rows per write transaction.</summary>
    public const int BatchSize = 500;

    private readonly MangaPixerDbContext _db;
    private readonly CreatorSync _creators;
    private readonly CreatorStateCache _creatorCache;
    private readonly FacetIndexLock _lock;
    private readonly ScanRunRegistry? _scans;
    private readonly IFacetIndexGeneration? _generation;
    private readonly TimeProvider _time;
    private readonly ILogger<FacetIndexService> _logger;
    private readonly IWorkDetector _detector;

    public FacetIndexService(MangaPixerDbContext db, CreatorSync creators, CreatorStateCache creatorCache, FacetIndexLock gate,
        TimeProvider time, ILogger<FacetIndexService> logger, ScanRunRegistry? scans = null, IWorkDetector? detector = null,
        IFacetIndexGeneration? generation = null)
    {
        _db = db;
        _creators = creators;
        _creatorCache = creatorCache;
        _lock = gate;
        _generation = generation;
        _time = time;
        _logger = logger;
        _scans = scans;
        _detector = detector ?? new WorkDetector();
    }

    private sealed class ScanStarted : Exception;

    /// <summary>
    /// True while a scan of the library runs: a scan run of the library this process registered (<see cref="ScanRunRegistry"/>) is still
    /// running, or the library is in its scan maintenance state.
    /// </summary>
    public async Task<bool> IsScanningAsync(long libraryId, CancellationToken ct)
    {
        if (await _db.Libraries.AsNoTracking().AnyAsync(l => l.Id == libraryId && l.State == "maintenance", ct))
            return true;
        if (_scans is null)
            return false;
        var running = await _db.ScanRuns.AsNoTracking().Where(r => r.LibraryId == libraryId && r.Status == 1).Select(r => r.Id).ToListAsync(ct);
        return running.Any(_scans.IsRunning);
    }

    /// <summary>
    /// The library's inputs key (D1 2.3): the rules version, its catalog revision, its link rows, its parsed ComicInfo and declared facts
    /// (counts and newest stamps), the records its link rows of an indexed state name, and the global creator inputs (the creator tables, author records,
    /// declared creators, admin overrides, merges) - a SHA-256 of scalar reads; no rows are loaded.
    /// <para>
    /// 1.45.1 (B2): the records part is the library's OWN linked records, no longer every stored record - a refresh of a record no link of
    /// this library names left the key of EVERY library changed, so the sweep rebuilt all of them. What another library's record does to
    /// this library goes through the creators: the creator pass of that library's rebuild writes the creator tables, which move this key.
    /// The creator tables are written only by index passes and admin creator changes, both under <see cref="FacetIndexLock"/>, so a rebuild
    /// stores the key with the creator tables as its OWN creator pass left them (else its next sweep would rebuild it again for nothing).
    /// </para>
    /// </summary>
    public async Task<string> InputsKeyAsync(long libraryId, CancellationToken ct) =>
        Hash([.. await InputPartsAsync(libraryId, ct), .. await CreatorTablePartsAsync(ct)]);

    /// <summary>The creator tables part of the inputs key (written only under <see cref="FacetIndexLock"/>).</summary>
    private async Task<object?[]> CreatorTablePartsAsync(CancellationToken ct) =>
    [
        await _db.Creators.CountAsync(ct),
        await _db.Creators.MaxAsync(c => (DateTimeOffset?)c.UpdatedAt, ct),
        await _db.CreatorNames.CountAsync(ct),
        await _db.CreatorProviderIds.CountAsync(ct),
    ];

    /// <summary>Every other part of the inputs key (read before a rebuild reads its inputs).</summary>
    private async Task<object?[]> InputPartsAsync(long libraryId, CancellationToken ct)
    {
        var parsed = FacetIndexData.ComicInfoParsed;
        var libraryNodes = _db.CatalogNodes.Where(n => n.LibraryId == libraryId).Select(n => n.Id);
        var ci = _db.EmbeddedMetadata.Where(e => e.State == parsed && libraryNodes.Contains(e.NodeId));
        var links = _db.NodeSeriesLinks.Where(l => l.LibraryId == libraryId);
        var declared = _db.DeclaredFacts.Where(f => f.LibraryId == libraryId);
        var merged = _db.Creators.Where(c => c.MergedIntoId != null);
        var indexedStates = FacetIndexNudges.IndexedLinkStates;
        var linkedRecords = _db.MetadataRecords.Where(r => links.Any(l => l.RecordId == r.Id && indexedStates.Contains(l.State)));
        var parts = new object?[]
        {
            RulesVersion,
            await _db.Libraries.Where(l => l.Id == libraryId).Select(l => (long?)l.CatalogRevision).FirstOrDefaultAsync(ct),
            await links.CountAsync(ct),
            await links.MaxAsync(l => (DateTimeOffset?)l.UpdatedAt, ct),
            await ci.CountAsync(ct),
            await ci.MaxAsync(e => (DateTimeOffset?)e.ReadAt, ct),
            await declared.CountAsync(ct),
            await declared.MaxAsync(f => (DateTimeOffset?)f.UpdatedAt, ct),
            await linkedRecords.CountAsync(ct),
            await linkedRecords.MaxAsync(r => (DateTimeOffset?)r.FetchedAt, ct),
            await _db.MetadataAuthors.CountAsync(ct),
            await _db.MetadataAuthors.MaxAsync(a => (DateTimeOffset?)a.FetchedAt, ct),
            await _db.DeclaredFacts.CountAsync(f => f.ExternalRef != null, ct),
            await _db.DeclaredFacts.Where(f => f.ExternalRef != null).MaxAsync(f => (DateTimeOffset?)f.UpdatedAt, ct),
            await _db.CreatorOverrides.CountAsync(ct),
            await _db.CreatorOverrides.MaxAsync(o => (long?)o.Id, ct),
            await merged.CountAsync(ct),
            await merged.MaxAsync(c => (DateTimeOffset?)c.UpdatedAt, ct),
        };
        return parts;
    }

    private static string Hash(IEnumerable<object?> parts)
    {
        var text = string.Join('|', parts.Select(p => p switch
        {
            null => "-",
            DateTimeOffset d => d.UtcTicks.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    /// <summary>The inputs key the library's index was last built from, or null.</summary>
    public Task<string?> StoredKeyAsync(long libraryId, CancellationToken ct) =>
        _db.FacetIndexLibraries.AsNoTracking().Where(l => l.LibraryId == libraryId).Select(l => l.InputsKey).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Rebuilds one library's index. <paramref name="force"/> false: only when its inputs key changed. <paramref name="yieldAsync"/>:
    /// awaited before every write batch (the builder waits there while reader work has the media worker pool).
    /// </summary>
    public async Task<FacetRebuildResult> RebuildLibraryAsync(long libraryId, CancellationToken ct, bool force = true,
        Func<CancellationToken, Task>? yieldAsync = null)
    {
        await _lock.Gate.WaitAsync(ct);
        try
        {
            var watch = Stopwatch.StartNew();
            if (!await _db.Libraries.AsNoTracking().AnyAsync(l => l.Id == libraryId, ct))
                return new FacetRebuildResult(FacetRebuildOutcome.LibraryGone);
            if (await IsScanningAsync(libraryId, ct))
            {
                _logger.LogDebug(LogEvents.Metadata.FacetIndexSkippedScan, "Tag index of library {LibraryId} skipped: a scan runs", libraryId);
                return new FacetRebuildResult(FacetRebuildOutcome.SkippedScanRunning);
            }
            var inputs = await InputPartsAsync(libraryId, ct);
            var key = Hash([.. inputs, .. await CreatorTablePartsAsync(ct)]);
            if (!force && string.Equals(await StoredKeyAsync(libraryId, ct), key, StringComparison.Ordinal))
                return new FacetRebuildResult(FacetRebuildOutcome.Unchanged);

            try
            {
                var result = await BuildAsync(libraryId, inputs, yieldAsync, watch, ct);
                _logger.LogInformation(LogEvents.Metadata.FacetIndexRebuilt,
                    "Tag index of library {LibraryId} rebuilt: {Works} work(s), {Entries} entries; {Inserted} inserted, {Updated} updated, {Deleted} deleted in {ElapsedMs} ms",
                    libraryId, result.Works, result.Entries, result.Inserted, result.Updated, result.Deleted, result.ElapsedMs);
                return result;
            }
            catch (ScanStarted)
            {
                _logger.LogDebug(LogEvents.Metadata.FacetIndexSkippedScan, "Tag index of library {LibraryId} stopped: a scan started", libraryId);
                return new FacetRebuildResult(FacetRebuildOutcome.SkippedScanRunning);
            }
        }
        finally
        {
            _lock.Gate.Release();
        }
    }

    private sealed record Desired(long WorkId, FacetSources Sources, FacetRoles Roles, bool Conflict);

    private async Task<FacetRebuildResult> BuildAsync(long libraryId, object?[] inputs, Func<CancellationToken, Task>? yieldAsync, Stopwatch watch,
        CancellationToken ct)
    {
        // 1. Creators first.
        var state = await _creators.EnsureAsync(ct);

        // 2. The library's inputs.
        var snapshot = await LibraryTreeSnapshot.LoadAsync(_db, libraryId, ct, providerAuthors: true);
        var nodes = new List<FacetNode>(snapshot.Count);
        var walk = new Stack<LibraryTreeSnapshot.Node>(snapshot.Roots.Reverse());
        while (walk.Count > 0)
        {
            var node = walk.Pop();
            nodes.Add(new FacetNode(node.Id, node.ParentId, node.IsFolder, node.Name));
            var children = snapshot.ChildrenOf(node.Id);
            for (var i = children.Count - 1; i >= 0; i--)
                walk.Push(children[i]);
        }
        var linkRows = await FacetIndexData.LinksAsync(_db, libraryId, ct);
        var links = new Dictionary<long, FacetLink>();
        foreach (var row in linkRows)
        {
            if (FacetIndexData.ToLink(row) is { } link)
                links[row.NodeId] = link;
        }
        var declared = (await FacetIndexData.DeclaredAsync(_db, libraryId, ct)).Select(FacetIndexData.ToFact).ToList();
        var tree = new FacetTree(nodes, links, declared);
        var comicInfo = (await FacetIndexData.ComicInfoAsync(_db, libraryId, ct)).ToDictionary(r => r.NodeId, FacetIndexData.ToComicInfo);
        var recordIds = links.Values
            .Where(l => l.RecordId is not null && (SeriesLinkStates.IsSeries(l.State) || l.State == SeriesLinkState.CollectionAbout))
            .Select(l => l.RecordId!.Value)
            .ToHashSet();
        var records = (await FacetIndexData.RecordsAsync(_db, recordIds, ct)).ToDictionary(r => r.Id, FacetIndexData.ToRecord);

        // 3. Credits -> creators (creating the ones the creator pass has not seen yet, e.g. written since).
        var resolved = await ResolveAllAsync(state, tree, records, comicInfo, ct);
        state = resolved.State;

        var others = (await _db.FacetWorks.AsNoTracking()
                .Where(w => w.Kind == (int)FacetWorkKind.ComicInfo && _db.FacetEntries.Any(e => e.WorkId == w.Id && e.LibraryId != libraryId))
                .Select(w => w.WorkKey)
                .ToListAsync(ct))
            .Select(k => FacetWorkKey.TryParse(k, out var parts) ? new FacetComicInfoWorkKey(k, parts.SeriesForm!, parts.CreatorIds) : null)
            .OfType<FacetComicInfoWorkKey>()
            .ToList();

        // 4. Works.
        var works = WorkResolver.Resolve(new WorkResolverInput(
            tree,
            id => _detector.Classify(snapshot.ShapeOf(id)),
            comicInfo,
            records.ToDictionary(r => r.Key, r => r.Value.Title),
            archive => resolved.ComicInfo.TryGetValue(archive, out var credits) ? credits.Select(c => c.CreatorId).ToHashSet() : [],
            others));

        // 5. Values per location (a declared creator the creator pass resolved elsewhere is created, then the locations are read again).
        var drafts = new List<(FacetResolvedWork Work, long Location, FacetEntryDraft Entry)>();
        for (var attempt = 0; ; attempt++)
        {
            drafts.Clear();
            var freshDeclared = new Dictionary<CreatorHandle, string>();
            Extract(freshDeclared);
            if (freshDeclared.Count == 0 || attempt > 0)
                break;
            await _creators.MaterializeAsync(freshDeclared.Keys.ToList(), freshDeclared, ct);
            _creatorCache.Invalidate();
            state = await _creators.EnsureAsync(ct);
        }

        void Extract(Dictionary<CreatorHandle, string> freshDeclared)
        {
            foreach (var work in works)
            {
                var record = work.RecordId is { } rid ? records.GetValueOrDefault(rid) : null;
                IReadOnlyList<FacetResolvedCredit> recordCredits = record is null ? [] : resolved.Records.GetValueOrDefault(record.Id) ?? [];
                foreach (var location in work.Locations)
                {
                    var (typeFact, creatorFacts) = tree.EffectiveDeclared(location.NodeId);
                    var context = tree.ContextRefs(location.NodeId, state.RefsOf);
                    var declaredCredits = new List<FacetResolvedCredit>();
                    foreach (var fact in creatorFacts)
                    {
                        var credit = new FacetCredit(fact.Value, fact.Role, FacetText.ParseProviderRef(fact.ExternalRef) is null ? null : fact.ExternalRef);
                        if (state.Resolver.Resolve(credit, context) is not { } handle)
                            continue;
                        if (handle.IsNew)
                        {
                            freshDeclared.TryAdd(handle, fact.Value);
                            continue;
                        }
                        declaredCredits.Add(new FacetResolvedCredit(handle.Id, FacetText.RolesOf(fact.Role, declared: true) ?? FacetRoles.None, fact.Value));
                    }
                    var conflict = record is not null && DeclaredFactsComparer.CreatorsConflict(
                        creatorFacts.Select(f => f.Value).ToList(),
                        record.Creators.Where(c => FacetText.RolesOf(c.Role) is not null).Select(c => c.Name).ToList(),
                        record.Creators.Select(c => c.ProviderRef).OfType<string>()
                            .SelectMany(r => state.Resolver.Registry.ProviderNames.GetValueOrDefault(r) ?? []));
                    var about = location.AboutRecordId is { } aboutId && records.TryGetValue(aboutId, out var aboutRecord)
                        ? (aboutId, aboutRecord.Title)
                        : ((long, string)?)null;
                    var input = new FacetLocationInput(
                        record,
                        recordCredits,
                        location.ArchiveIds.Select(a => comicInfo.GetValueOrDefault(a)).OfType<FacetComicInfo>().ToList(),
                        location.ArchiveIds.SelectMany(a => resolved.ComicInfo.GetValueOrDefault(a) ?? []).ToList(),
                        DeclaredFactKeys.ParseType(typeFact?.Value),
                        declaredCredits,
                        conflict,
                        about);
                    foreach (var entry in FacetExtractor.Extract(input, state.DisplayOf))
                        drafts.Add((work, location.NodeId, entry));
                }
            }
        }

        // The key this build answers: its inputs as read before the build, the creator tables as its own creator pass left them.
        var key = Hash([.. inputs, .. await CreatorTablePartsAsync(ct)]);

        // 6. The diff.
        var now = _time.GetUtcNow();
        var inserted = 0;
        var updated = 0;
        var deleted = 0;

        async Task BatchAsync(Func<Task> write)
        {
            if (yieldAsync is not null)
                await yieldAsync(ct);
            if (await IsScanningAsync(libraryId, ct))
                throw new ScanStarted();
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            await write();
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
            _generation?.Bump(); // Lane Q's count cache: every committed index write is a new generation.
        }

        // 6a. Works (insert new keys; update changed titles / kinds). 1.45.1 (B1): only works with at least one value are stored - a work
        // with nothing to index would be inserted here and deleted again in 6d ("left without entries") on EVERY pass (measured on a real
        // library: 725 of 3201 works, +725 / -725 per rebuild with unchanged inputs).
        var indexed = drafts.Select(d => d.Work.Key).ToHashSet(StringComparer.Ordinal);
        var workIds = new Dictionary<string, long>(StringComparer.Ordinal);
        var existingWorks = new Dictionary<string, FacetWorkEntity>(StringComparer.Ordinal);
        foreach (var chunk in works.Where(w => indexed.Contains(w.Key)).Select(w => w.Key).Chunk(BatchSize))
        {
            foreach (var w in await _db.FacetWorks.AsNoTracking().Where(w => chunk.Contains(w.WorkKey)).ToListAsync(ct))
                existingWorks[w.WorkKey] = w;
        }
        var newWorks = new List<FacetWorkEntity>();
        var changedWorks = new List<FacetWorkEntity>();
        foreach (var work in works.Where(w => indexed.Contains(w.Key)))
        {
            var title = FacetText.Display(work.Title) ?? work.Key;
            if (title.Length > FacetModel.TitleLength)
                title = title[..FacetModel.TitleLength];
            var sortKey = Core.Ordering.SortKey.ForNode(CatalogNodeKind.Folder, title);
            if (existingWorks.TryGetValue(work.Key, out var row))
            {
                workIds[work.Key] = row.Id;
                if (row.Title != title || row.SortKey != sortKey || row.Kind != (int)work.Kind || row.RecordId != work.RecordId)
                {
                    row.Title = title;
                    row.SortKey = sortKey;
                    row.Kind = (int)work.Kind;
                    row.RecordId = work.RecordId;
                    row.UpdatedAt = now;
                    changedWorks.Add(row);
                }
            }
            else
            {
                newWorks.Add(new FacetWorkEntity { WorkKey = work.Key, Kind = (int)work.Kind, RecordId = work.RecordId, Title = title, SortKey = sortKey, UpdatedAt = now });
            }
        }
        foreach (var chunk in changedWorks.Chunk(BatchSize))
        {
            await BatchAsync(() => { _db.FacetWorks.UpdateRange(chunk); return Task.CompletedTask; });
            updated += chunk.Length;
        }
        foreach (var chunk in newWorks.Chunk(BatchSize))
        {
            await BatchAsync(() => { _db.FacetWorks.AddRange(chunk); return Task.CompletedTask; });
            foreach (var w in chunk)
                workIds[w.WorkKey] = w.Id;
            inserted += chunk.Length;
        }

        // 6b. Values (insert new ones; a creator value follows its creator's display name).
        var values = (await _db.FacetValues.AsNoTracking().ToListAsync(ct)).ToDictionary(v => (v.Bucket, v.ValueKey));
        var publicIds = values.Values.Select(v => v.PublicId).ToHashSet(StringComparer.Ordinal);
        var newValues = new Dictionary<(int, string), FacetValueEntity>();
        var changedValues = new List<FacetValueEntity>();
        foreach (var (_, _, entry) in drafts)
        {
            var id = ((int)entry.Bucket, entry.ValueKey);
            if (values.TryGetValue(id, out var row))
            {
                if (entry.CreatorId is not null && row.Display != entry.Display && !changedValues.Contains(row))
                {
                    row.Display = entry.Display;
                    changedValues.Add(row);
                }
                continue;
            }
            if (newValues.ContainsKey(id))
                continue;
            string publicId;
            do
            {
                publicId = FacetPublicIds.NewValueId();
            }
            while (!publicIds.Add(publicId));
            newValues[id] = new FacetValueEntity
            {
                PublicId = publicId,
                Bucket = (int)entry.Bucket,
                ValueKey = entry.ValueKey,
                Display = entry.Display,
                CreatorId = entry.CreatorId,
            };
        }
        foreach (var chunk in changedValues.Chunk(BatchSize))
        {
            await BatchAsync(() => { _db.FacetValues.UpdateRange(chunk); return Task.CompletedTask; });
            updated += chunk.Length;
        }
        foreach (var chunk in newValues.Values.Chunk(BatchSize))
        {
            await BatchAsync(() => { _db.FacetValues.AddRange(chunk); return Task.CompletedTask; });
            foreach (var v in chunk)
                values[(v.Bucket, v.ValueKey)] = v;
            inserted += chunk.Length;
        }

        // 6c. Entries: keyed by (value, location) - the unique key.
        var desired = new Dictionary<(long ValueId, long Location), Desired>();
        foreach (var (work, location, entry) in drafts)
        {
            var valueId = values[((int)entry.Bucket, entry.ValueKey)].Id;
            desired[(valueId, location)] = new Desired(workIds[work.Key], entry.Sources, entry.Roles, entry.Conflict);
        }
        var stored = await _db.FacetEntries.AsNoTracking().Where(e => e.LibraryId == libraryId).ToListAsync(ct);
        var stale = new List<long>();
        var changedEntries = new List<FacetEntryEntity>();
        foreach (var row in stored)
        {
            if (!desired.Remove((row.ValueId, row.LocationNodeId), out var want))
            {
                stale.Add(row.Id);
                continue;
            }
            if (row.WorkId != want.WorkId || row.Sources != (int)want.Sources || row.Roles != (int)want.Roles || row.Conflict != want.Conflict)
            {
                row.WorkId = want.WorkId;
                row.Sources = (int)want.Sources;
                row.Roles = (int)want.Roles;
                row.Conflict = want.Conflict;
                changedEntries.Add(row);
            }
        }
        foreach (var chunk in stale.Chunk(BatchSize))
        {
            await BatchAsync(async () => await _db.FacetEntries.Where(e => chunk.Contains(e.Id)).ExecuteDeleteAsync(ct));
            deleted += chunk.Length;
        }
        foreach (var chunk in changedEntries.Chunk(BatchSize))
        {
            await BatchAsync(() => { _db.FacetEntries.UpdateRange(chunk); return Task.CompletedTask; });
            updated += chunk.Length;
        }
        var fresh = desired.Select(kv => new FacetEntryEntity
        {
            WorkId = kv.Value.WorkId,
            LocationNodeId = kv.Key.Location,
            LibraryId = libraryId,
            ValueId = kv.Key.ValueId,
            Sources = (int)kv.Value.Sources,
            Roles = (int)kv.Value.Roles,
            Conflict = kv.Value.Conflict,
        }).ToList();
        foreach (var chunk in fresh.Chunk(BatchSize))
        {
            await BatchAsync(() => { _db.FacetEntries.AddRange(chunk); return Task.CompletedTask; });
            inserted += chunk.Length;
        }

        // 6d. Works and values left without entries (any library); a merged creator's value stays (the redirect).
        await BatchAsync(async () =>
        {
            deleted += await _db.FacetWorks.Where(w => !_db.FacetEntries.Any(e => e.WorkId == w.Id)).ExecuteDeleteAsync(ct);
            deleted += await _db.FacetValues
                .Where(v => !_db.FacetEntries.Any(e => e.ValueId == v.Id)
                    && !(v.CreatorId != null && _db.Creators.Any(c => c.Id == v.CreatorId && c.MergedIntoId != null)))
                .ExecuteDeleteAsync(ct);
        });

        // 6e. The library's index state.
        var entries = stored.Count - stale.Count + fresh.Count;
        watch.Stop();
        await BatchAsync(async () =>
        {
            var row = await _db.FacetIndexLibraries.FirstOrDefaultAsync(l => l.LibraryId == libraryId, ct);
            if (row is null)
                _db.FacetIndexLibraries.Add(row = new FacetIndexLibraryEntity { LibraryId = libraryId });
            row.InputsKey = key;
            row.BuiltAt = now;
            row.Works = works.Count;
            row.Entries = entries;
            row.ElapsedMs = watch.ElapsedMilliseconds;
        });
        return new FacetRebuildResult(FacetRebuildOutcome.Rebuilt, works.Count, entries, inserted, updated, deleted, watch.ElapsedMilliseconds);
    }

    private sealed record Resolved(
        CreatorState State,
        Dictionary<long, List<FacetResolvedCredit>> Records,
        Dictionary<long, List<FacetResolvedCredit>> ComicInfo);

    /// <summary>The library's record and ComicInfo credits, resolved; creators not seen by the last creator pass are created first.</summary>
    private async Task<Resolved> ResolveAllAsync(CreatorState state, FacetTree tree, Dictionary<long, FacetRecord> records,
        Dictionary<long, FacetComicInfo> comicInfo, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var fresh = new Dictionary<CreatorHandle, string>();
            var recordCredits = new Dictionary<long, List<FacetResolvedCredit>>();
            var ciCredits = new Dictionary<long, List<FacetResolvedCredit>>();
            void Add(Dictionary<long, List<FacetResolvedCredit>> into, long id, FacetCredit credit, IReadOnlyCollection<string> context)
            {
                if (FacetText.RolesOf(credit.Role) is not { } roles || state.Resolver.Resolve(credit, context) is not { } handle)
                    return;
                if (handle.IsNew)
                {
                    fresh.TryAdd(handle, credit.Name);
                    return;
                }
                if (!into.TryGetValue(id, out var list))
                    into[id] = list = [];
                list.Add(new FacetResolvedCredit(handle.Id, roles, credit.Name));
            }
            foreach (var record in records.Values)
            {
                var context = state.RefsOf(record.Id);
                foreach (var credit in record.Creators)
                    Add(recordCredits, record.Id, credit, context);
            }
            foreach (var row in comicInfo.Values)
            {
                var context = tree.ContextRefs(row.ArchiveId, state.RefsOf);
                foreach (var credit in row.Creators)
                    Add(ciCredits, row.ArchiveId, credit, context);
            }
            if (fresh.Count == 0 || attempt > 0)
                return new Resolved(state, recordCredits, ciCredits);
            await _creators.MaterializeAsync(fresh.Keys.ToList(), fresh, ct);
            _creatorCache.Invalidate();
            state = await _creators.EnsureAsync(ct);
        }
    }
}
