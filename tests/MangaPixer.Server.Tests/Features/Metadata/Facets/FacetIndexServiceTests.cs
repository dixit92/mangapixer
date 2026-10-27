namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Facets;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Creators;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets.Index;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the tag index builder (1.45.0 lane F, D1 2.3 / 2.5): a rebuild indexes works, creators and values; it is
/// idempotent and writes only the diff; an admin merge / split survives every rebuild and re-keys ComicInfo works (owner Q5); nothing is
/// written while a scan of the library runs; the inputs key moves with the library's inputs; the index observers hear every write; and
/// a synthetic 30k-node library builds within a time bound. Migrated SQLite per test; synthetic names only.
/// </summary>
public sealed class FacetIndexServiceTests(Xunit.Abstractions.ITestOutputHelper output)
{
    /// <summary>Lane Q's seam as the tests see it: counts the bumps.</summary>
    private sealed class Generation : IFacetIndexGeneration
    {
        private long _current;
        public long Current => Interlocked.Read(ref _current);
        public void Bump() => Interlocked.Increment(ref _current);
    }

    private sealed class Kit : IAsyncDisposable
    {
        private Kit(MetadataTestDb db) => Meta = db;

        public MetadataTestDb Meta { get; }
        public ScanRunRegistry Scans { get; } = new();
        public CreatorStateCache Cache { get; } = new();
        public FacetIndexLock Lock { get; } = new();
        public Generation Generation { get; } = new();
        public RecordingFacetIndexNudge Nudges { get; } = new();

        public static async Task<Kit> CreateAsync() => new(await MetadataTestDb.CreateAsync());

        public FacetIndexService Index() => new(Meta.Db,
            new CreatorSync(Meta.Db, Cache, TimeProvider.System, NullLogger<CreatorSync>.Instance, Generation),
            Cache, Lock, TimeProvider.System, NullLogger<FacetIndexService>.Instance, Scans, generation: Generation);

        public CreatorAdminService Admin() => new(Meta.Db, new AuditService(Meta.Db), Nudges, Cache, Lock, TimeProvider.System,
            NullLogger<CreatorAdminService>.Instance, Generation);

        public Task<FacetRebuildResult> RebuildAsync(bool force = true) => Index().RebuildLibraryAsync(Meta.LibraryId, default, force);

        public ValueTask DisposeAsync() => Meta.DisposeAsync();
    }

    private static async Task<(CatalogNodeEntity Folder, MetadataRecordEntity Record)> LinkedSeriesAsync(Kit kit, string name = "Series Alpha",
        string externalId = "500")
    {
        var folder = await kit.Meta.AddFolderAsync(null, name);
        await kit.Meta.AddArchiveAsync(folder, name + " v01.cbz");
        await kit.Meta.AddArchiveAsync(folder, name + " v02.cbz");
        var record = await kit.Meta.AddRecordAsync(externalId, name);
        record.CreatorsJson = "[{\"name\":\"Tanaka Hiroshi\",\"role\":\"author\",\"providerId\":\"7\"},{\"name\":\"Helper\",\"role\":\"other\"}]";
        await kit.Meta.Db.SaveChangesAsync();
        await kit.Meta.AddLinkAsync(folder, record);
        return (folder, record);
    }

    private static async Task<CatalogNodeEntity> ComicInfoFolderAsync(Kit kit, string name, string series, string writer)
    {
        var folder = await kit.Meta.AddFolderAsync(null, name);
        foreach (var i in new[] { 1, 2 })
        {
            var archive = await kit.Meta.AddArchiveAsync(folder, $"{name} {i:00}.cbz");
            await kit.Meta.AddComicInfoAsync(archive, series, creators: [new ComicInfoCreator { Name = writer, Role = "writer" }]);
        }
        return folder;
    }

    private static async Task<List<string>> WorkKeysAsync(Kit kit) =>
        (await kit.Meta.Db.FacetWorks.AsNoTracking().Select(w => w.WorkKey).ToListAsync()).Order(StringComparer.Ordinal).ToList();

    private static async Task<long> CreatorIdAsync(Kit kit, string name) =>
        await kit.Meta.Db.CreatorNames.AsNoTracking().Where(n => n.Name == name).Select(n => n.CreatorId).FirstAsync();

    [Fact]
    public async Task ARebuild_IndexesWorksCreatorsAndValues_AndIsIdempotent()
    {
        await using var kit = await Kit.CreateAsync();
        var (folder, record) = await LinkedSeriesAsync(kit);
        var loose = await ComicInfoFolderAsync(kit, "Loose Saga", "Loose Saga", "Pen Name");

        var first = await kit.RebuildAsync();

        Assert.Equal(FacetRebuildOutcome.Rebuilt, first.Outcome);
        var db = kit.Meta.Db;
        var provider = await db.CreatorProviderIds.AsNoTracking().SingleAsync();
        Assert.Equal(("mangaupdates", "7"), (provider.Provider, provider.ExternalId));
        var penName = await CreatorIdAsync(kit, "Pen Name");
        Assert.Equal(new[] { "ci:loose saga#" + penName, "rec:" + record.Id }, await WorkKeysAsync(kit));
        // The record's "other" credit is not indexed (owner Q11).
        Assert.False(await db.CreatorNames.AnyAsync(n => n.Name == "Helper"));
        var recordWork = await db.FacetWorks.AsNoTracking().SingleAsync(w => w.RecordId == record.Id);
        var keys = await (from e in db.FacetEntries.AsNoTracking()
                          join v in db.FacetValues.AsNoTracking() on e.ValueId equals v.Id
                          where e.WorkId == recordWork.Id
                          select v.ValueKey).ToListAsync();
        Assert.Equal(new[] { "c:" + provider.CreatorId, "g:action", "g:drama", "t:manga", "y:1999" }, keys.Order(StringComparer.Ordinal).ToArray());
        Assert.True(await db.FacetEntries.AllAsync(e => e.LibraryId == kit.Meta.LibraryId));
        Assert.Equal(new[] { folder.Id }, await db.FacetEntries.Where(e => e.WorkId == recordWork.Id).Select(e => e.LocationNodeId).Distinct().ToArrayAsync());
        var state = await db.FacetIndexLibraries.AsNoTracking().SingleAsync();
        Assert.Equal((2, first.Entries), (state.Works, state.Entries));
        Assert.True(kit.Generation.Current > 0);

        var entryIds = await db.FacetEntries.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync();
        var second = await kit.RebuildAsync();
        Assert.Equal((FacetRebuildOutcome.Rebuilt, 0, 0, 0), (second.Outcome, second.Inserted, second.Updated, second.Deleted));
        Assert.Equal(entryIds, await db.FacetEntries.AsNoTracking().OrderBy(e => e.Id).Select(e => e.Id).ToListAsync());
        Assert.Equal(FacetRebuildOutcome.Unchanged, (await kit.RebuildAsync(force: false)).Outcome);
        _ = loose;
    }

    /// <summary>
    /// 1.45.1 (B1, measured on a copy of a real library: 725 of 3201 works had no value): a work with nothing to index - here a plain unlinked
    /// series folder without ComicInfo or declared facts - is not stored, so a rebuild with unchanged inputs writes NOTHING (the old builder
    /// inserted its row and deleted it again as "left without entries" on every pass).
    /// </summary>
    [Fact]
    public async Task AWorkWithNoValues_IsNotStored_SoARebuildWithUnchangedInputsWritesNothing()
    {
        await using var kit = await Kit.CreateAsync();
        await LinkedSeriesAsync(kit);
        var plain = await kit.Meta.AddFolderAsync(null, "Plain Saga");
        await kit.Meta.AddArchiveAsync(plain, "Plain Saga v01.cbz");
        await kit.Meta.AddArchiveAsync(plain, "Plain Saga v02.cbz");

        var first = await kit.RebuildAsync();
        Assert.Equal(FacetRebuildOutcome.Rebuilt, first.Outcome);
        Assert.False(await kit.Meta.Db.FacetWorks.AnyAsync(w => w.WorkKey == FacetWorkKey.Node(plain.Id)));
        var workIds = await kit.Meta.Db.FacetWorks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.Id).ToListAsync();

        var second = await kit.RebuildAsync();

        Assert.Equal((FacetRebuildOutcome.Rebuilt, 0, 0, 0), (second.Outcome, second.Inserted, second.Updated, second.Deleted));
        Assert.Equal(workIds, await kit.Meta.Db.FacetWorks.AsNoTracking().OrderBy(w => w.Id).Select(w => w.Id).ToListAsync());
    }

    [Fact]
    public async Task EveryCommittedIndexWrite_BumpsLaneQsGeneration_ASkippedPassDoesNot()
    {
        await using var kit = await Kit.CreateAsync();
        await LinkedSeriesAsync(kit);

        Assert.Equal(FacetRebuildOutcome.Rebuilt, (await kit.RebuildAsync()).Outcome);
        var afterRebuild = kit.Generation.Current;
        Assert.True(afterRebuild > 0, "a rebuild that wrote entries must bump the generation");

        Assert.Equal(FacetRebuildOutcome.Unchanged, (await kit.RebuildAsync(force: false)).Outcome);
        Assert.Equal(afterRebuild, kit.Generation.Current);

        var creator = await kit.Meta.Db.Creators.AsNoTracking().SingleAsync();
        await kit.Admin().SetDisplayNameAsync(creator.PublicId, "Renamed", null, default);
        Assert.True(kit.Generation.Current > afterRebuild, "a creator rename must bump the generation");
    }

    [Fact]
    public async Task ARefreshedRecord_RewritesOnlyWhatChanged()
    {
        await using var kit = await Kit.CreateAsync();
        var (_, record) = await LinkedSeriesAsync(kit);
        await kit.RebuildAsync();
        var db = kit.Meta.Db;
        var actionEntry = await (from e in db.FacetEntries join v in db.FacetValues on e.ValueId equals v.Id where v.ValueKey == "g:action" select e.Id).SingleAsync();

        var tracked = await db.MetadataRecords.SingleAsync(r => r.Id == record.Id);
        tracked.GenresJson = "[\"Action\",\"Comedy\"]";
        tracked.FetchedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await kit.RebuildAsync(force: false);

        Assert.Equal(FacetRebuildOutcome.Rebuilt, result.Outcome);
        // One new value + its entry; the Drama entry and its now unused value deleted; nothing else touched.
        Assert.Equal((2, 0, 2), (result.Inserted, result.Updated, result.Deleted));
        Assert.Equal(actionEntry, await (from e in db.FacetEntries join v in db.FacetValues on e.ValueId equals v.Id where v.ValueKey == "g:action" select e.Id).SingleAsync());
        Assert.False(await db.FacetValues.AnyAsync(v => v.ValueKey == "g:drama"));
        Assert.True(await db.FacetValues.AnyAsync(v => v.ValueKey == "g:comedy"));
    }

    [Fact]
    public async Task AMergeAndASplit_SurviveEveryRebuild_AndReKeyTheComicInfoWorks()
    {
        await using var kit = await Kit.CreateAsync();
        var one = await ComicInfoFolderAsync(kit, "Saga One", "Synthetic Saga", "Pen Name");
        var two = await ComicInfoFolderAsync(kit, "Saga Two", "Synthetic Saga", "Other Pen");
        await kit.RebuildAsync();
        Assert.Equal(2, (await WorkKeysAsync(kit)).Count);
        var pen = await kit.Meta.Db.Creators.AsNoTracking().SingleAsync(c => c.Id == kit.Meta.Db.CreatorNames.Where(n => n.Name == "Pen Name").Select(n => n.CreatorId).First());
        var other = await kit.Meta.Db.Creators.AsNoTracking().SingleAsync(c => c.Id == kit.Meta.Db.CreatorNames.Where(n => n.Name == "Other Pen").Select(n => n.CreatorId).First());

        var merged = await kit.Admin().MergeAsync(other.PublicId, pen.PublicId, null, default);
        Assert.Equal(pen.PublicId, merged.Creator!.Id);
        Assert.Contains(((long?)null, FacetNudgeReason.CreatorOverrideChanged), kit.Nudges.Nudges);

        for (var pass = 0; pass < 2; pass++)
        {
            await kit.RebuildAsync();
            var work = Assert.Single(await WorkKeysAsync(kit));
            Assert.Equal("ci:synthetic saga#" + pen.Id, work);
            Assert.Equal(new[] { one.Id, two.Id }, await kit.Meta.Db.FacetEntries.Select(e => e.LocationNodeId).Distinct().OrderBy(id => id).ToArrayAsync());
        }
        // The merged creator keeps its row (the redirect) and its tag value (lane Q redirects a bookmarked value through it).
        Assert.Equal(pen.Id, (await kit.Meta.Db.Creators.AsNoTracking().SingleAsync(c => c.Id == other.Id)).MergedIntoId);
        Assert.True(await kit.Meta.Db.FacetValues.AnyAsync(v => v.CreatorId == other.Id));
        Assert.Equal(pen.PublicId, (await kit.Admin().GetAsync(other.PublicId, default))!.Id);
        Assert.Equal(other.PublicId, (await kit.Admin().GetAsync(other.PublicId, default))!.RedirectedFrom);

        // Split "Other Pen" off again: a pin keeps the rules from undoing it, rebuild after rebuild.
        var split = await kit.Admin().SplitAsync(pen.PublicId, null, ["Other Pen"], null, default);
        Assert.Null(split.Error);
        var splitId = await kit.Meta.Db.Creators.AsNoTracking().Where(c => c.PublicId == split.Creator!.Id).Select(c => c.Id).SingleAsync();
        for (var pass = 0; pass < 2; pass++)
        {
            await kit.RebuildAsync();
            Assert.Equal(new[] { "ci:synthetic saga#" + pen.Id, "ci:synthetic saga#" + splitId }.Order(StringComparer.Ordinal).ToArray(),
                (await WorkKeysAsync(kit)).ToArray());
        }
        Assert.True(await kit.Meta.Db.CreatorOverrides.AnyAsync(o => o.Kind == (int)CreatorOverrideKind.SplitPin && o.Subject == "other pen" && o.TargetCreatorId == splitId));
    }

    [Fact]
    public async Task AnAdminDisplayName_Wins_AndSurvivesARebuild()
    {
        await using var kit = await Kit.CreateAsync();
        await LinkedSeriesAsync(kit);
        await kit.RebuildAsync();
        var creator = await kit.Meta.Db.Creators.AsNoTracking().SingleAsync();
        Assert.Equal(("Tanaka Hiroshi", (int)CreatorDisplayNameSource.MostUsed), (creator.DisplayName, creator.DisplayNameSource));

        Assert.Equal("Admin Choice", (await kit.Admin().SetDisplayNameAsync(creator.PublicId, " Admin  Choice ", null, default)).Creator!.DisplayName);
        await kit.RebuildAsync();
        await kit.RebuildAsync();
        Assert.Equal("Admin Choice", (await kit.Meta.Db.Creators.AsNoTracking().SingleAsync()).DisplayName);
        Assert.Equal("Admin Choice", (await kit.Meta.Db.FacetValues.AsNoTracking().SingleAsync(v => v.CreatorId == creator.Id)).Display);

        await kit.Admin().SetDisplayNameAsync(creator.PublicId, null, null, default);
        await kit.RebuildAsync();
        Assert.Equal("Tanaka Hiroshi", (await kit.Meta.Db.Creators.AsNoTracking().SingleAsync()).DisplayName);
    }

    [Fact]
    public async Task ProviderAuthorNames_MakeAliasSpellingsOneCreator_AndNameTheCreator()
    {
        await using var kit = await Kit.CreateAsync();
        await LinkedSeriesAsync(kit);
        kit.Meta.Db.MetadataAuthors.Add(new MetadataAuthorEntity
        {
            Provider = "mangaupdates",
            ExternalId = "7",
            Name = "TANAKA Hiroshi",
            OtherNamesJson = "[\"Kenta Mori\"]",
            FetchedAt = DateTimeOffset.UtcNow,
        });
        await kit.Meta.Db.SaveChangesAsync();
        var a = await ComicInfoFolderAsync(kit, "Saga One", "Synthetic Saga", "Hiroshi Tanaka");
        var b = await ComicInfoFolderAsync(kit, "Saga Two", "Synthetic Saga", "Kenta Mori");

        await kit.RebuildAsync();

        var creator = await kit.Meta.Db.Creators.AsNoTracking().SingleAsync();
        Assert.Equal(("TANAKA Hiroshi", (int)CreatorDisplayNameSource.Provider), (creator.DisplayName, creator.DisplayNameSource));
        Assert.Contains("ci:synthetic saga#" + creator.Id, await WorkKeysAsync(kit));
        var ciWork = await kit.Meta.Db.FacetWorks.AsNoTracking().SingleAsync(w => w.Kind == (int)FacetWorkKind.ComicInfo);
        Assert.Equal(new[] { a.Id, b.Id }, await kit.Meta.Db.FacetEntries.Where(e => e.WorkId == ciWork.Id).Select(e => e.LocationNodeId).Distinct().OrderBy(i => i).ToArrayAsync());
    }

    [Fact]
    public async Task NothingIsWritten_WhileAScanOfTheLibraryRuns_AndTheNextPassWrites()
    {
        await using var kit = await Kit.CreateAsync();
        await LinkedSeriesAsync(kit);
        var run = new ScanRunEntity { LibraryId = kit.Meta.LibraryId, ScanRevision = 1, Status = 1, StartedAt = DateTimeOffset.UtcNow };
        kit.Meta.Db.ScanRuns.Add(run);
        await kit.Meta.Db.SaveChangesAsync();
        kit.Scans.Register(run.Id);

        Assert.Equal(FacetRebuildOutcome.SkippedScanRunning, (await kit.RebuildAsync()).Outcome);
        Assert.False(await kit.Meta.Db.FacetEntries.AnyAsync());
        Assert.False(await kit.Meta.Db.FacetWorks.AnyAsync());
        Assert.False(await kit.Meta.Db.Creators.AnyAsync());
        Assert.False(await kit.Meta.Db.FacetIndexLibraries.AnyAsync());
        Assert.Equal(0, kit.Generation.Current);

        // Another library's scan does not hold this one back.
        var other = await kit.Meta.AddLibraryAsync("otherlib", "Other");
        var otherRun = new ScanRunEntity { LibraryId = other.Id, ScanRevision = 1, Status = 1, StartedAt = DateTimeOffset.UtcNow };
        kit.Meta.Db.ScanRuns.Add(otherRun);
        await kit.Meta.Db.SaveChangesAsync();
        kit.Scans.Register(otherRun.Id);
        kit.Scans.Complete(run.Id);

        Assert.Equal(FacetRebuildOutcome.Rebuilt, (await kit.RebuildAsync()).Outcome);
        Assert.True(await kit.Meta.Db.FacetEntries.AnyAsync());
    }

    /// <summary>
    /// 1.45.1 (B2): a record that no link of the library names - another library's series refreshed, a record stored by an Identify elsewhere -
    /// leaves the library's inputs key alone (it used to move every library's key, so the sweep rebuilt them all); a refresh of the library's
    /// own linked record and a change of the creator tables still move it.
    /// </summary>
    [Fact]
    public async Task TheInputsKey_IgnoresRecordsNoLinkOfTheLibraryNames_AndMovesWithItsOwnRecordAndTheCreators()
    {
        await using var kit = await Kit.CreateAsync();
        var (_, own) = await LinkedSeriesAsync(kit);
        var other = await kit.Meta.AddLibraryAsync("otherlib", "Other");
        var elsewhere = await kit.Meta.AddFolderAsync(null, "Elsewhere", other.Id);
        var theirs = await kit.Meta.AddRecordAsync("600", "Elsewhere Series");
        await kit.Meta.AddLinkAsync(elsewhere, theirs);
        var index = kit.Index();
        var key = await index.InputsKeyAsync(kit.Meta.LibraryId, default);

        var db = kit.Meta.Db;
        var tracked = await db.MetadataRecords.SingleAsync(r => r.Id == theirs.Id);
        tracked.FetchedAt = DateTimeOffset.UtcNow.AddMinutes(5);
        await db.SaveChangesAsync();
        await kit.Meta.AddRecordAsync("700", "Unlinked Candidate");
        Assert.Equal(key, await index.InputsKeyAsync(kit.Meta.LibraryId, default));

        var mine = await db.MetadataRecords.SingleAsync(r => r.Id == own.Id);
        mine.FetchedAt = DateTimeOffset.UtcNow.AddMinutes(6);
        await db.SaveChangesAsync();
        var afterOwn = await index.InputsKeyAsync(kit.Meta.LibraryId, default);
        Assert.NotEqual(key, afterOwn);

        // The creator pass of another library's rebuild (a new creator) moves it: what a record elsewhere does here goes through the creators.
        await kit.Index().RebuildLibraryAsync(other.Id, default);
        Assert.NotEqual(afterOwn, await index.InputsKeyAsync(kit.Meta.LibraryId, default));
    }

    [Fact]
    public async Task TheInputsKey_MovesWithTheLibrarysInputs_Only()
    {
        await using var kit = await Kit.CreateAsync();
        var (folder, _) = await LinkedSeriesAsync(kit);
        var other = await kit.Meta.AddLibraryAsync("otherlib", "Other");
        var index = kit.Index();
        var key = await index.InputsKeyAsync(kit.Meta.LibraryId, default);
        Assert.Equal(key, await index.InputsKeyAsync(kit.Meta.LibraryId, default));

        // Another library's folder changes nothing here.
        var elsewhere = await kit.Meta.AddFolderAsync(null, "Elsewhere", other.Id);
        kit.Meta.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = other.Id,
            NodeId = elsewhere.Id,
            Key = DeclaredFactKeys.Type,
            Value = "manhwa",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await kit.Meta.Db.SaveChangesAsync();
        Assert.Equal(key, await index.InputsKeyAsync(kit.Meta.LibraryId, default));

        // A declared fact, a ComicInfo row and a link of this library each move it.
        kit.Meta.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = kit.Meta.LibraryId,
            NodeId = folder.Id,
            Key = DeclaredFactKeys.Type,
            Value = "manhwa",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await kit.Meta.Db.SaveChangesAsync();
        var afterDeclared = await index.InputsKeyAsync(kit.Meta.LibraryId, default);
        Assert.NotEqual(key, afterDeclared);
        var archive = await kit.Meta.AddArchiveAsync(folder, "Series Alpha v03.cbz");
        await kit.Meta.AddComicInfoAsync(archive, "Series Alpha");
        var afterComicInfo = await index.InputsKeyAsync(kit.Meta.LibraryId, default);
        Assert.NotEqual(afterDeclared, afterComicInfo);
        await kit.Meta.AddLinkAsync(archive, null, SeriesLinkState.DontMatch);
        Assert.NotEqual(afterComicInfo, await index.InputsKeyAsync(kit.Meta.LibraryId, default));
    }

    [Fact]
    public async Task ADeclaredTypeTheRecordContradicts_IsIndexedBesideTheRecordsType_AndFlagged()
    {
        await using var kit = await Kit.CreateAsync();
        var (folder, _) = await LinkedSeriesAsync(kit);
        kit.Meta.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = kit.Meta.LibraryId,
            NodeId = folder.Id,
            Key = DeclaredFactKeys.Type,
            Value = "manhwa",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await kit.Meta.Db.SaveChangesAsync();

        await kit.RebuildAsync();

        var types = await (from e in kit.Meta.Db.FacetEntries
                           join v in kit.Meta.Db.FacetValues on e.ValueId equals v.Id
                           where v.Bucket == (int)FacetBucket.Type
                           orderby v.ValueKey
                           select new { v.ValueKey, e.Conflict, e.Sources }).ToListAsync();
        Assert.Equal(new[] { ("t:manga", false, (int)FacetSources.Web), ("t:manhwa", true, (int)FacetSources.Declared) },
            types.Select(t => (t.ValueKey, t.Conflict, t.Sources)).ToArray());
    }

    [Fact]
    public async Task ASynthetic30kNodeLibrary_BuildsWithinTheTimeBound_AndASecondPassWritesNothing()
    {
        await using var kit = await Kit.CreateAsync();
        var db = kit.Meta.Db;
        const int folders = 1000;
        const int archivesPerFolder = 29;
        var now = DateTimeOffset.UtcNow;
        var records = Enumerable.Range(0, 600).Select(i => new MetadataRecordEntity
        {
            PublicId = "syn" + i,
            Provider = "mangaupdates",
            ExternalId = (100000 + i).ToString(System.Globalization.CultureInfo.InvariantCulture),
            Title = "Synthetic Series " + i,
            CreatorsJson = $"[{{\"name\":\"Writer {i % 300} Name\",\"role\":\"author\",\"providerId\":\"{i % 300}\"}},{{\"name\":\"Artist {i % 200} Name\",\"role\":\"artist\",\"providerId\":\"{1000 + (i % 200)}\"}}]",
            GenresJson = $"[\"Genre {i % 30}\",\"Genre {(i + 7) % 30}\",\"Action\"]",
            PublishersJson = $"[{{\"name\":\"Publisher {i % 50}\",\"kind\":\"original\"}}]",
            PublicationsJson = $"[{{\"name\":\"Magazine {i % 40}\"}}]",
            StartYear = 1980 + (i % 40),
            Origin = (int)MetadataOrigin.Japan,
            FetchedAt = now,
            // 1.49.0 (lane T): the stored top 20 categories, net votes 20 .. 1, from a vocabulary of 2756 (the live distinct count).
            CategoriesJson = "[" + string.Join(",", Enumerable.Range(0, 20).Select(k => $"{{\"name\":\"Tag {(i * 7 + k * 31) % 2756}\",\"votes\":{20 - k}}}")) + "]",
        }).ToList();
        db.MetadataRecords.AddRange(records);
        await db.SaveChangesAsync();

        var folderNodes = Enumerable.Range(0, folders).Select(f => new CatalogNodeEntity
        {
            PublicId = "sf" + f,
            LibraryId = kit.Meta.LibraryId,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = "Synthetic Folder " + f,
            RelativePath = "sf" + f,
            PathKey = "sf" + f,
            SortKey = "0synthetic folder " + f,
            CreatedAt = now,
        }).ToList();
        db.CatalogNodes.AddRange(folderNodes);
        await db.SaveChangesAsync();
        var archives = new List<CatalogNodeEntity>(folders * archivesPerFolder);
        foreach (var folder in folderNodes)
        {
            for (var a = 0; a < archivesPerFolder; a++)
            {
                archives.Add(new CatalogNodeEntity
                {
                    PublicId = folder.PublicId + "a" + a,
                    LibraryId = kit.Meta.LibraryId,
                    ParentId = folder.Id,
                    Kind = (int)CatalogNodeKind.Archive,
                    DisplayName = $"{folder.DisplayName} v{a + 1:00}.cbz",
                    RelativePath = folder.PublicId + "a" + a,
                    PathKey = folder.PublicId + "a" + a,
                    SortKey = $"1{folder.DisplayName.ToLowerInvariant()} v{a + 1:00}.cbz",
                    CreatedAt = now,
                });
            }
        }
        db.CatalogNodes.AddRange(archives);
        await db.SaveChangesAsync();
        db.ArchiveItems.AddRange(archives.Select(a => new ArchiveItemEntity { NodeId = a.Id, ContentVersion = 1, PageCount = 3 }));
        // 600 folders linked (100 records twice: split series), ComicInfo on the first two archives of the 400 others.
        for (var f = 0; f < 700; f++)
        {
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = folderNodes[f].Id,
                LibraryId = kit.Meta.LibraryId,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = records[f % records.Count].Id,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }
        for (var f = 700; f < folders; f++)
        {
            foreach (var a in archives.Skip(f * archivesPerFolder).Take(2))
            {
                db.EmbeddedMetadata.Add(new EmbeddedMetadataEntity
                {
                    NodeId = a.Id,
                    ContentVersion = 1,
                    State = 1,
                    Series = "Synthetic Saga " + (f % 150),
                    Year = 2000 + (f % 20),
                    CreatorsJson = $"[{{\"name\":\"Writer {f % 300} Name\",\"role\":\"writer\"}},{{\"name\":\"Solo{f % 97}\",\"role\":\"penciller\"}}]",
                    GenresJson = "[\"action\",\"Slice of Life\"]",
                    TagsJson = $"[\"Found Family\",\"found-family\",\"Tag {f % 236}\"]",
                    ReadAt = now,
                });
            }
        }
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        Assert.Equal(folders * (archivesPerFolder + 1), await db.CatalogNodes.CountAsync());

        var watch = Stopwatch.StartNew();
        var first = await kit.RebuildAsync();
        watch.Stop();

        Assert.Equal(FacetRebuildOutcome.Rebuilt, first.Outcome);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(90), $"30k-node rebuild took {watch.Elapsed.TotalSeconds:0.0} s");
        Assert.Equal(600 + 300, first.Works); // 600 distinct records (100 split across two folders) + 300 unlinked folders
        Assert.Equal(600, await db.FacetWorks.CountAsync(w => w.Kind == (int)FacetWorkKind.Record));
        Assert.True(first.Entries > 6000, $"{first.Entries} entries");

        // 1.49.0 (lane T): the Tags bucket's share of the index - every linked location carries its record's 20 categories, every
        // ComicInfo location its folded tags (two archives with "Found Family" / "found-family" / "Tag n" = 2 values per location).
        var tagEntries = await (from e in db.FacetEntries join v in db.FacetValues on e.ValueId equals v.Id
                                where v.Bucket == (int)FacetBucket.Tags select e).CountAsync();
        var ciLocations = await (from e in db.FacetEntries join w in db.FacetWorks on e.WorkId equals w.Id
                                 where w.Kind == (int)FacetWorkKind.ComicInfo select e.LocationNodeId).Distinct().CountAsync();
        var tagValues = await db.FacetValues.CountAsync(v => v.Bucket == (int)FacetBucket.Tags);
        var expectedValues = Enumerable.Range(0, 600).SelectMany(i => Enumerable.Range(0, 20).Select(k => (i * 7 + k * 31) % 2756))
            .Concat(Enumerable.Range(700, 300).Select(f => f % 236)).Distinct().Count() + 1; // + "found family"
        Assert.Equal((700 * 20) + (2 * ciLocations), tagEntries);
        Assert.Equal(expectedValues, tagValues);
        output.WriteLine($"Tag index growth (synthetic 30k nodes): {first.Entries} entries, of them {tagEntries} Tags " +
            $"(+{100.0 * tagEntries / (first.Entries - tagEntries):0}% over the other buckets), {tagValues} Tags values; {ciLocations} ComicInfo locations; " +
            $"first build {watch.Elapsed.TotalSeconds:0.0} s");

        var second = await kit.RebuildAsync();
        Assert.Equal((0, 0, 0), (second.Inserted, second.Updated, second.Deleted));
        Assert.True(second.ElapsedMs < 90_000);
    }

    // 1.49.0 (lane T, T1): a linked series' categories and its archives' ComicInfo Tags land in ONE Tags bucket, case / spacing folded.
    [Fact]
    public async Task Tags_FromRecordCategoriesAndComicInfoTags_OneValuePerFoldedTag()
    {
        await using var kit = await Kit.CreateAsync();
        var (folder, record) = await LinkedSeriesAsync(kit);
        record.CategoriesJson = "[{\"name\":\"Time Travel\",\"votes\":12},{\"name\":\"Revenge\",\"votes\":-2}]";
        await kit.Meta.Db.SaveChangesAsync();
        var archive = await kit.Meta.Db.CatalogNodes.FirstAsync(n => n.ParentId == folder.Id);
        await kit.Meta.AddComicInfoAsync(archive, "Series Alpha");
        var row = await kit.Meta.Db.EmbeddedMetadata.SingleAsync(e => e.NodeId == archive.Id);
        row.TagsJson = "[\"time-travel\",\"Found  Family\"]";
        await kit.Meta.Db.SaveChangesAsync();

        var result = await kit.RebuildAsync();

        Assert.Equal(FacetRebuildOutcome.Rebuilt, result.Outcome);
        var tags = await (from e in kit.Meta.Db.FacetEntries
                          join v in kit.Meta.Db.FacetValues on e.ValueId equals v.Id
                          where v.Bucket == (int)FacetBucket.Tags
                          orderby v.ValueKey
                          select new { v.ValueKey, v.Display, e.Sources, e.LocationNodeId }).ToListAsync();
        Assert.Equal(
            new[] { ("tg:found family", "Found Family", (int)FacetSources.ComicInfo), ("tg:revenge", "Revenge", (int)FacetSources.Web),
                ("tg:time travel", "Time Travel", (int)(FacetSources.Web | FacetSources.ComicInfo)) },
            tags.Select(t => (t.ValueKey, t.Display, t.Sources)).ToArray());
        Assert.All(tags, t => Assert.Equal(folder.Id, t.LocationNodeId));
    }

    // 1.49.0: the rules version is part of the inputs key, so the release that adds Tags rebuilds every library once.
    [Fact]
    public void RulesVersion_IsBumpedForTheTagsBucket() => Assert.True(FacetIndexService.RulesVersion >= 2);
}
