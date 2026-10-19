namespace com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;

using System.Diagnostics;
using System.Globalization;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The resolved creator state the library passes use: the resolver over the stored creators (every credit the sync saw has an id), the
/// provider references each record credits (rule R2's context) and each live creator's display name.
/// </summary>
public sealed class CreatorState
{
    internal CreatorState(string stamp, CreatorResolver resolver, IReadOnlyDictionary<long, IReadOnlyCollection<string>> recordRefs,
        Dictionary<long, string> display)
    {
        Stamp = stamp;
        Resolver = resolver;
        RecordRefs = recordRefs;
        Display = display;
    }

    public string Stamp { get; }
    public CreatorResolver Resolver { get; internal set; }
    public IReadOnlyDictionary<long, IReadOnlyCollection<string>> RecordRefs { get; }
    internal Dictionary<long, string> Display { get; }

    public IReadOnlyCollection<string> RefsOf(long recordId) => RecordRefs.TryGetValue(recordId, out var refs) ? refs : [];

    public string DisplayOf(long creatorId) => Display.TryGetValue(creatorId, out var name) ? name : string.Empty;
}

/// <summary>The last creator state (singleton): reused while the global creator inputs are unchanged.</summary>
public sealed class CreatorStateCache
{
    private CreatorState? _state;

    public CreatorState? Current => Volatile.Read(ref _state);

    internal void Set(CreatorState? state) => Volatile.Write(ref _state, state);

    /// <summary>Forget the state (an admin merge / split / rename): the next pass syncs again.</summary>
    public void Invalidate() => Set(null);
}

/// <summary>
/// The global creator pass (1.45.0 lane F, D1 2.2; creators resolve BEFORE works - owner Q5's condition). Reads every credit of every
/// source - the stored records' credits, the current ComicInfo of live archives and the declared creators - with its work context (the
/// nearest linked series' record credits and the declared creators' picked authors), resolves each through <see cref="CreatorResolver"/>
/// (admin pins, R1-R4, merges), creates the creators seen for the first time (stable surrogate ids, never deleted by a rebuild), adds every
/// spelling seen to <c>creator_names</c>, and applies the display-name rule (admin &gt; the author record's main name &gt; the most used
/// spelling), pushing a changed name to the creator's tag value. Skipped while the global inputs stamp is unchanged. No network.
/// </summary>
public sealed class CreatorSync
{
    private readonly MangaPixerDbContext _db;
    private readonly CreatorStateCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<CreatorSync> _logger;

    public CreatorSync(MangaPixerDbContext db, CreatorStateCache cache, TimeProvider time, ILogger<CreatorSync> logger)
    {
        _db = db;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>The state for the current inputs: the cached one while the stamp is unchanged, else a fresh sync.</summary>
    public async Task<CreatorState> EnsureAsync(CancellationToken ct)
    {
        var stamp = await StampAsync(ct);
        if (_cache.Current is { } cached && string.Equals(cached.Stamp, stamp, StringComparison.Ordinal))
            return cached;
        var state = await SyncAsync(stamp, ct);
        _cache.Set(state);
        return state;
    }

    /// <summary>
    /// Everything the creator pass reads, as counts and newest stamps (scalar reads only): records, author records, ComicInfo, declared
    /// facts, link rows, live nodes and the creator tables themselves.
    /// </summary>
    internal async Task<string> StampAsync(CancellationToken ct)
    {
        var parsed = FacetIndexData.ComicInfoParsed;
        var parts = new object?[]
        {
            await _db.MetadataRecords.CountAsync(ct),
            await _db.MetadataRecords.MaxAsync(r => (DateTimeOffset?)r.FetchedAt, ct),
            await _db.MetadataAuthors.CountAsync(ct),
            await _db.MetadataAuthors.MaxAsync(a => (DateTimeOffset?)a.FetchedAt, ct),
            await _db.EmbeddedMetadata.CountAsync(e => e.State == parsed, ct),
            await _db.EmbeddedMetadata.MaxAsync(e => (DateTimeOffset?)e.ReadAt, ct),
            await _db.DeclaredFacts.CountAsync(ct),
            await _db.DeclaredFacts.MaxAsync(f => (DateTimeOffset?)f.UpdatedAt, ct),
            await _db.NodeSeriesLinks.CountAsync(ct),
            await _db.NodeSeriesLinks.MaxAsync(l => (DateTimeOffset?)l.UpdatedAt, ct),
            await _db.Libraries.SumAsync(l => (long?)l.CatalogRevision, ct),
            await _db.Libraries.CountAsync(ct),
            await _db.Creators.CountAsync(ct),
            await _db.Creators.MaxAsync(c => (DateTimeOffset?)c.UpdatedAt, ct),
            await _db.CreatorNames.CountAsync(ct),
            await _db.CreatorProviderIds.CountAsync(ct),
            await _db.CreatorOverrides.CountAsync(ct),
            await _db.CreatorOverrides.MaxAsync(o => (long?)o.Id, ct),
        };
        return string.Join('|', parts.Select(p => p switch
        {
            null => "-",
            DateTimeOffset d => d.UtcTicks.ToString(CultureInfo.InvariantCulture),
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => p.ToString(),
        }));
    }

    private sealed record Occurrence(FacetCredit Credit, IReadOnlyCollection<string> Context, CreatorNameSource Source);

    private sealed class Use
    {
        public int Count;
        public CreatorNameSource Source = CreatorNameSource.Admin;
    }

    internal async Task<CreatorState> SyncAsync(string stamp, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        var records = await FacetIndexData.RecordsAsync(_db, null, ct);
        var recordCredits = records.ToDictionary(r => r.Id, r => FacetIndexData.Credits(r.Provider, r.CreatorsJson));
        var recordRefs = recordCredits.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyCollection<string>)kv.Value.Select(c => c.ProviderRef).OfType<string>().ToHashSet(StringComparer.Ordinal));
        var authors = await _db.MetadataAuthors.AsNoTracking()
            .Where(a => a.Status == 0)
            .Select(a => new { a.Provider, a.ExternalId, a.Name, a.OtherNamesJson })
            .ToListAsync(ct);
        var declared = await FacetIndexData.DeclaredAsync(_db, null, ct);

        // Every spelling each provider-backed person is known by (R2 / R3).
        var providerNames = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        void Name(string? reference, string? name)
        {
            if (reference is null || DeclaredFactKeys.CleanName(name) is not { } clean)
                return;
            if (!providerNames.TryGetValue(reference, out var set))
                providerNames[reference] = set = new HashSet<string>(StringComparer.Ordinal);
            set.Add(clean);
        }
        foreach (var credit in recordCredits.Values.SelectMany(c => c))
            Name(credit.ProviderRef, credit.Name);
        var authorNames = new Dictionary<string, (string? Main, IReadOnlyList<string> Other)>(StringComparer.Ordinal);
        foreach (var author in authors)
        {
            if (FacetText.ProviderRef(author.Provider, author.ExternalId) is not { } reference)
                continue;
            var other = MetadataJson.ReadList<string>(author.OtherNamesJson);
            authorNames[reference] = (author.Name, other);
            Name(reference, author.Name);
            foreach (var o in other)
                Name(reference, o);
        }
        foreach (var fact in declared.Where(f => f.Key == DeclaredFactKeys.Creator))
            Name(FacetText.ParseProviderRef(fact.ExternalRef) is null ? null : fact.ExternalRef, fact.Value);

        var registry = await RegistryAsync(providerNames.ToDictionary(kv => kv.Key, kv => (IReadOnlyCollection<string>)kv.Value), ct);
        var resolver = new CreatorResolver(registry);

        // Every credit occurrence with its work context.
        var occurrences = new List<Occurrence>();
        foreach (var (recordId, credits) in recordCredits)
        {
            foreach (var credit in credits.Where(c => FacetText.RolesOf(c.Role) is not null))
                occurrences.Add(new Occurrence(credit, recordRefs[recordId], CreatorNameSource.Record));
        }
        var trees = await TreesAsync(declared, ct);
        foreach (var row in await FacetIndexData.ComicInfoAsync(_db, null, ct))
        {
            if (!trees.TryGetValue(row.LibraryId, out var tree))
                continue;
            var context = tree.ContextRefs(row.NodeId, RefsOf);
            foreach (var credit in FacetIndexData.ComicInfoCredits(row.CreatorsJson).Where(c => FacetText.RolesOf(c.Role) is not null))
                occurrences.Add(new Occurrence(credit, context, CreatorNameSource.ComicInfo));
        }
        foreach (var fact in declared.Where(f => f.Key == DeclaredFactKeys.Creator))
        {
            IReadOnlyCollection<string> context = fact.NodeId is { } node && trees.TryGetValue(fact.LibraryId, out var tree)
                ? tree.ContextRefs(node, RefsOf)
                : declared.Where(f => f.LibraryId == fact.LibraryId && f.NodeId == null && f.Key == DeclaredFactKeys.Creator)
                    .Select(f => f.ExternalRef).Where(r => FacetText.ParseProviderRef(r) is not null).OfType<string>().ToHashSet(StringComparer.Ordinal);
            occurrences.Add(new Occurrence(new FacetCredit(fact.Value, fact.Role, ProviderRefOrNull(fact.ExternalRef)), context, CreatorNameSource.Declared));
        }

        IReadOnlyCollection<string> RefsOf(long recordId) => recordRefs.TryGetValue(recordId, out var refs) ? refs : [];

        // Resolve, then create the creators seen for the first time.
        var resolved = occurrences.Select(o => (o, Handle: resolver.Resolve(o.Credit, o.Context))).Where(x => x.Handle is not null).ToList();
        var firstNames = resolved.Where(x => x.Handle!.Value.IsNew)
            .GroupBy(x => x.Handle!.Value)
            .ToDictionary(g => g.Key, g => g.First().o.Credit.Name);
        var created = await MaterializeAsync(firstNames.Keys.ToList(), firstNames, ct);
        if (created > 0)
        {
            registry = await RegistryAsync(registry.ProviderNames, ct);
            resolver = new CreatorResolver(registry);
            resolved = occurrences.Select(o => (o, Handle: resolver.Resolve(o.Credit, o.Context))).Where(x => x.Handle is not null).ToList();
        }

        // Spellings in use per live creator.
        var uses = new Dictionary<long, Dictionary<string, Use>>();
        void Count(long creatorId, string name, CreatorNameSource source)
        {
            if (DeclaredFactKeys.CleanName(name) is not { } clean)
                return;
            if (!uses.TryGetValue(creatorId, out var byName))
                uses[creatorId] = byName = new Dictionary<string, Use>(StringComparer.Ordinal);
            if (!byName.TryGetValue(clean, out var use))
                byName[clean] = use = new Use();
            use.Count++;
            if (source < use.Source)
                use.Source = source;
        }
        foreach (var (occurrence, handle) in resolved)
        {
            if (handle is { IsNew: false } h)
                Count(h.Id, occurrence.Credit.Name, occurrence.Source);
        }

        var namesAdded = await WriteNamesAsync(uses, registry, authorNames, resolver, ct);
        var display = await WriteDisplayNamesAsync(uses, registry, authorNames, ct);

        watch.Stop();
        _logger.LogInformation(LogEvents.Metadata.FacetCreatorsSynced,
            "Facet creators synced: {Credits} credit(s), {Created} creator(s) created, {Names} spelling(s) added, {Renamed} display name(s) changed in {ElapsedMs} ms",
            occurrences.Count, created, namesAdded, display.Changed, watch.ElapsedMilliseconds);

        // The stamp moved with this sync's own writes: take the new one so the next pass does not sync again for them.
        var after = created + namesAdded + display.Changed > 0 ? await StampAsync(ct) : stamp;
        return new CreatorState(after, resolver, recordRefs, display.Names);
    }

    private static string? ProviderRefOrNull(string? reference) => FacetText.ParseProviderRef(reference) is null ? null : reference;

    /// <summary>The stored creators as the resolver reads them.</summary>
    internal async Task<CreatorRegistry> RegistryAsync(IReadOnlyDictionary<string, IReadOnlyCollection<string>> providerNames, CancellationToken ct)
    {
        var providerIds = await _db.CreatorProviderIds.AsNoTracking().Select(p => new { p.Provider, p.ExternalId, p.CreatorId }).ToListAsync(ct);
        var withProvider = providerIds.Select(p => p.CreatorId).ToHashSet();
        var merged = await _db.Creators.AsNoTracking().Where(c => c.MergedIntoId != null)
            .Select(c => new { c.Id, Into = c.MergedIntoId!.Value }).ToListAsync(ct);
        var names = await _db.CreatorNames.AsNoTracking().Select(n => new { n.CreatorId, n.NameKey }).ToListAsync(ct);
        var nameOnly = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names.Where(n => !withProvider.Contains(n.CreatorId)).OrderBy(n => n.CreatorId))
            nameOnly.TryAdd(name.NameKey, name.CreatorId);
        var overrides = await _db.CreatorOverrides.AsNoTracking()
            .Select(o => new CreatorOverride(o.Id, (CreatorOverrideKind)o.Kind, o.Subject, o.TargetCreatorId, o.Value))
            .ToListAsync(ct);
        var byRef = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var p in providerIds)
        {
            if (FacetText.ProviderRef(p.Provider, p.ExternalId) is { } reference)
                byRef[reference] = p.CreatorId;
        }
        return new CreatorRegistry(byRef, nameOnly, merged.ToDictionary(m => m.Id, m => m.Into), overrides, providerNames);
    }

    /// <summary>One <see cref="FacetTree"/> per library (ids, shape, links, declared facts; no names - nothing here reads them).</summary>
    private async Task<Dictionary<long, FacetTree>> TreesAsync(List<FacetIndexData.DeclaredRow> declared, CancellationToken ct)
    {
        var nodes = await FacetIndexData.AllNodesAsync(_db, ct);
        var links = await FacetIndexData.LinksAsync(_db, null, ct);
        var linksByLibrary = links.GroupBy(l => l.LibraryId).ToDictionary(g => g.Key, g => g.ToList());
        var declaredByLibrary = declared.GroupBy(f => f.LibraryId).ToDictionary(g => g.Key, g => g.ToList());
        var trees = new Dictionary<long, FacetTree>();
        foreach (var library in nodes.GroupBy(n => n.LibraryId))
        {
            var ownLinks = new Dictionary<long, FacetLink>();
            foreach (var link in linksByLibrary.GetValueOrDefault(library.Key) ?? [])
            {
                if (FacetIndexData.ToLink(link) is { } l)
                    ownLinks[link.NodeId] = l;
            }
            trees[library.Key] = new FacetTree(
                library.Select(n => new FacetNode(n.Id, n.ParentId, n.IsFolder, string.Empty)),
                ownLinks,
                (declaredByLibrary.GetValueOrDefault(library.Key) ?? []).Select(FacetIndexData.ToFact));
        }
        return trees;
    }

    /// <summary>Creates the creators of new handles (provider-backed with their reference; name-only by key). Returns how many.</summary>
    internal async Task<int> MaterializeAsync(IReadOnlyList<CreatorHandle> handles, IReadOnlyDictionary<CreatorHandle, string> firstNames, CancellationToken ct)
    {
        if (handles.Count == 0)
            return 0;
        var now = _time.GetUtcNow();
        var publicIds = (await _db.Creators.AsNoTracking().Select(c => c.PublicId).ToListAsync(ct)).ToHashSet(StringComparer.Ordinal);
        var made = 0;
        foreach (var chunk in handles.Chunk(500))
        {
            await using var tx = await _db.Database.BeginTransactionAsync(ct);
            var pending = new List<(CreatorEntity Creator, CreatorHandle Handle, string Name)>();
            foreach (var handle in chunk)
            {
                var name = FacetText.Display(firstNames.GetValueOrDefault(handle)) ?? handle.NewNameKey ?? handle.NewProviderRef ?? "?";
                string publicId;
                do
                {
                    publicId = FacetPublicIds.NewCreatorId();
                }
                while (!publicIds.Add(publicId));
                var creator = new CreatorEntity
                {
                    PublicId = publicId,
                    DisplayName = name,
                    DisplayNameSource = (int)CreatorDisplayNameSource.MostUsed,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                _db.Creators.Add(creator);
                pending.Add((creator, handle, name));
            }
            await _db.SaveChangesAsync(ct);
            foreach (var (creator, handle, name) in pending)
            {
                if (handle.NewProviderRef is { } reference && FacetText.ParseProviderRef(reference) is { } parts)
                    _db.CreatorProviderIds.Add(new CreatorProviderIdEntity { CreatorId = creator.Id, Provider = parts.Provider, ExternalId = parts.ExternalId });
                // The name-only creator's key row: what R4 finds it by.
                var key = handle.NewNameKey ?? DeclaredFactsComparer.NameKey(name);
                if (key.Length > 0)
                {
                    _db.CreatorNames.Add(new CreatorNameEntity
                    {
                        CreatorId = creator.Id,
                        Name = Cap(name, FacetModel.NameLength),
                        NameKey = Cap(handle.NewNameKey ?? DeclaredFactsComparer.NameKey(name), FacetModel.NameKeyLength),
                        Script = (int)FacetText.Script(name),
                        Source = (int)(handle.NewProviderRef is null ? CreatorNameSource.ComicInfo : CreatorNameSource.Record),
                    });
                }
            }
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
            made += pending.Count;
        }
        return made;
    }

    private async Task<int> WriteNamesAsync(Dictionary<long, Dictionary<string, Use>> uses, CreatorRegistry registry,
        Dictionary<string, (string? Main, IReadOnlyList<string> Other)> authorNames, CreatorResolver resolver, CancellationToken ct)
    {
        var existing = (await _db.CreatorNames.AsNoTracking().Select(n => new { n.CreatorId, n.Name }).ToListAsync(ct))
            .Select(n => (n.CreatorId, n.Name)).ToHashSet();
        var wanted = new List<CreatorNameEntity>();
        void Want(long creatorId, string name, CreatorNameSource source)
        {
            var capped = Cap(name, FacetModel.NameLength);
            var key = Cap(DeclaredFactsComparer.NameKey(capped), FacetModel.NameKeyLength);
            if (key.Length == 0 || !existing.Add((creatorId, capped)))
                return;
            wanted.Add(new CreatorNameEntity
            {
                CreatorId = creatorId,
                Name = capped,
                NameKey = key,
                Script = (int)FacetText.Script(capped),
                Source = (int)source,
            });
        }
        foreach (var (creatorId, byName) in uses)
        {
            foreach (var (name, use) in byName.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Want(creatorId, name, use.Source);
        }
        // The stored author record's names belong to the creator holding its reference.
        foreach (var (reference, (main, other)) in authorNames.OrderBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (!registry.ProviderCreators.TryGetValue(reference, out var id))
                continue;
            var creatorId = resolver.FinalId(id);
            foreach (var name in new[] { main }.Concat(other).Select(DeclaredFactKeys.CleanName).OfType<string>())
                Want(creatorId, name, CreatorNameSource.ProviderAuthor);
        }
        foreach (var chunk in wanted.Chunk(1000))
        {
            _db.CreatorNames.AddRange(chunk);
            await _db.SaveChangesAsync(ct);
            _db.ChangeTracker.Clear();
        }
        return wanted.Count;
    }

    private async Task<(int Changed, Dictionary<long, string> Names)> WriteDisplayNamesAsync(Dictionary<long, Dictionary<string, Use>> uses,
        CreatorRegistry registry, Dictionary<string, (string? Main, IReadOnlyList<string> Other)> authorNames, CancellationToken ct)
    {
        var creators = await _db.Creators.AsNoTracking()
            .Select(c => new { c.Id, c.DisplayName, c.DisplayNameSource, c.MergedIntoId })
            .ToListAsync(ct);
        var refsByCreator = registry.ProviderCreators.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.Select(kv => kv.Key).Order(StringComparer.Ordinal).ToList());
        var admin = registry.Overrides.Where(o => o.Kind == CreatorOverrideKind.DisplayName)
            .GroupBy(o => o.TargetCreatorId)
            .ToDictionary(g => g.Key, g => g.MaxBy(o => o.Id)!.Value);
        var firstSeen = (await _db.CreatorNames.AsNoTracking().Select(n => new { n.Id, n.CreatorId, n.Name }).ToListAsync(ct))
            .GroupBy(n => (n.CreatorId, n.Name))
            .ToDictionary(g => g.Key, g => g.Min(n => n.Id));
        var names = new Dictionary<long, string>(creators.Count);
        var changed = 0;
        var now = _time.GetUtcNow();
        foreach (var creator in creators)
        {
            names[creator.Id] = creator.DisplayName;
            if (creator.MergedIntoId is not null)
                continue;
            var spellings = (uses.GetValueOrDefault(creator.Id) ?? [])
                .Select(kv => new CreatorSpellingUse(kv.Key, kv.Value.Count,
                    firstSeen.TryGetValue((creator.Id, Cap(kv.Key, FacetModel.NameLength)), out var seen) ? seen : long.MaxValue))
                .ToList();
            var providerMain = (refsByCreator.GetValueOrDefault(creator.Id) ?? []).Select(r => authorNames.TryGetValue(r, out var a) ? a.Main : null);
            var adminName = admin.GetValueOrDefault(creator.Id);
            if (spellings.Count == 0 && adminName is null && !providerMain.Any(n => n is not null))
                continue; // Nothing credits it now: keep its name.
            if (CreatorDisplayName.Choose(adminName, providerMain, spellings) is not { } choice)
                continue;
            names[creator.Id] = choice.Name;
            if (string.Equals(choice.Name, creator.DisplayName, StringComparison.Ordinal) && (int)choice.Source == creator.DisplayNameSource)
                continue;
            changed++;
            await _db.Creators.Where(c => c.Id == creator.Id).ExecuteUpdateAsync(s => s
                .SetProperty(c => c.DisplayName, choice.Name)
                .SetProperty(c => c.DisplayNameSource, (int)choice.Source)
                .SetProperty(c => c.UpdatedAt, now), ct);
            await _db.FacetValues.Where(v => v.CreatorId == creator.Id && v.Display != choice.Name)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.Display, choice.Name), ct);
        }
        return (changed, names);
    }

    internal static string Cap(string text, int max) => text.Length <= max ? text : text[..max];
}
