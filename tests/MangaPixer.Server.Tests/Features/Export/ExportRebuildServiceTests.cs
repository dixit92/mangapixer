namespace com.lifepixer.mangapixer.Tests.Server.Features.Export;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>The export's services over a migrated SQLite database (1.33.0): synthetic names, a fixed clock.</summary>
public sealed class ExportTestKit : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private readonly ExportRebuildGate _gate = new();

    private ExportTestKit(MetadataTestDb db) => Db = db;

    public MetadataTestDb Db { get; }
    public ManualTime Clock { get; } = new(Start);
    public TimeSpan MinInterval { get; set; } = TimeSpan.Zero;

    public static async Task<ExportTestKit> CreateAsync() => new(await MetadataTestDb.CreateAsync());

    public ExportRebuildService Rebuilds() =>
        new(Db.Db, new ExportItemBuilder(Db.Db), _gate, new ExportOptions(MinInterval), Clock, NullLogger<ExportRebuildService>.Instance);

    public ExportService Export() => new(Db.Db, Rebuilds(), Clock);

    public MetadataCarryOverService CarryOver() =>
        new(Db.Db, new AuditService(Db.Db), Clock, [], NullLogger<MetadataCarryOverService>.Instance);

    public Task<ExportRebuildResult> RebuildAsync(long? libraryId = null) => Rebuilds().RebuildAsync(libraryId ?? Db.LibraryId);

    /// <summary>A full page (every item, removals) of the default library as parsed JSON.</summary>
    public async Task<JsonElement> PageAsync(string? updatedSince = null, int? limit = 500, string? cursor = null, string? include = null, string? library = null)
    {
        var answer = await Export().PageAsync(library ?? Db.LibraryPublicId, updatedSince, cursor, limit, include);
        Assert.True(answer.Json is not null, "export error " + answer.Error);
        return JsonDocument.Parse(answer.Json!).RootElement.Clone();
    }

    public async Task<Dictionary<string, ExportItemDto>> ItemsAsync(long? libraryId = null)
    {
        var rows = await Db.Db.ExportItems.AsNoTracking().Where(i => i.LibraryId == (libraryId ?? Db.LibraryId)).ToListAsync();
        return rows.ToDictionary(r => r.NodePublicId, r => ExportJson.ReadItem(r.Json)!);
    }

    public async Task<Dictionary<string, string>> RemovalsAsync(long? libraryId = null) =>
        await Db.Db.ExportRemovals.AsNoTracking().Where(r => r.LibraryId == (libraryId ?? Db.LibraryId)).ToDictionaryAsync(r => r.NodePublicId, r => r.Reason);

    public async Task TombstoneAsync(CatalogNodeEntity node)
    {
        await Db.Db.CatalogNodes.Where(n => n.Id == node.Id).ExecuteUpdateAsync(u => u
            .SetProperty(n => n.Availability, (int)CatalogNodeAvailability.Tombstoned)
            .SetProperty(n => n.TombstonedAt, Clock.Now));
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}

[Trait("Category", "ServiceDb")]
public sealed class ExportRebuildServiceTests
{
    [Fact]
    public async Task FirstRebuild_ExportsEveryLiveNodeWithItsOwnLink_FoldersAndArchives()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var shelf = await kit.Db.AddFolderAsync(null, "Shonen");
        var series = await kit.Db.AddFolderAsync(shelf, "Synthetic Series");
        await kit.Db.AddArchiveAsync(series, "Synthetic Series v01.cbz");
        var record = await kit.Db.AddRecordAsync("9001", "Synthetic Series");
        await kit.Db.AddLinkAsync(series, record);
        var collection = await kit.Db.AddFolderAsync(null, "Collections");
        var work = await kit.Db.AddArchiveAsync(collection, "Synthetic Artist - One Shot.zip");
        await kit.Db.AddLinkAsync(work, null, SeriesLinkState.NeedsReview);
        var notOne = await kit.Db.AddFolderAsync(null, "Misc");
        await kit.Db.AddLinkAsync(notOne, null, SeriesLinkState.DontMatch);
        await kit.Db.AddFolderAsync(null, "Unlinked");

        var result = await kit.RebuildAsync();

        Assert.Equal((3, 3, 0), (result.Items, result.Written, result.Removed));
        var items = await kit.ItemsAsync();
        var folder = items[series.PublicId];
        Assert.Equal(("folder", "Confirmed"), (folder.NodeKind, folder.Link.State));
        Assert.Equal(["Shonen", "Synthetic Series"], folder.Trail);
        Assert.Equal(("mangaupdates", "9001", "Synthetic Series", "Ongoing", false), (folder.Record!.Provider, folder.Record.ExternalId,
            folder.Record.Title, folder.Record.OriginStatus, folder.Record.CompletedInOrigin));
        Assert.NotNull(folder.Completion);
        Assert.Equal(ExportTestKit.Start, folder.Completion!.ComputedAt);
        Assert.NotNull(folder.Refresh);
        Assert.Equal(30, folder.Refresh!.IntervalDays); // ongoing, cadence not computed yet
        Assert.Equal(ExportTestKit.Start, folder.UpdatedAt);

        var archive = items[work.PublicId];
        Assert.Equal(("archive", "NeedsReview"), (archive.NodeKind, archive.Link.State));
        Assert.Equal(["Collections", "Synthetic Artist - One Shot.zip"], archive.Trail);
        Assert.Null(archive.Record);
        Assert.Null(archive.Completion);
        Assert.Null(archive.Refresh);
        Assert.Null(archive.Volumes);
        Assert.Empty(archive.OfficialLinks);

        Assert.Equal("DontMatch", items[notOne.PublicId].Link.State);
        Assert.Null(items[notOne.PublicId].Record);

        var state = await kit.Db.Db.ExportLibraryStates.AsNoTracking().SingleAsync();
        Assert.Equal((ExportTestKit.Start, ExportTestKit.Start, 3), (state.WatermarkAt, state.LastRebuildAt, state.ItemCount));
    }

    [Fact]
    public async Task AnUnchangedRebuild_WritesNothing_AndAChangedItemMovesAlone()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var a = await kit.Db.AddFolderAsync(null, "Series A");
        var b = await kit.Db.AddFolderAsync(null, "Series B");
        var recordA = await kit.Db.AddRecordAsync("1", "Series A");
        await kit.Db.AddLinkAsync(a, recordA);
        await kit.Db.AddLinkAsync(b, await kit.Db.AddRecordAsync("2", "Series B"));
        await kit.RebuildAsync();

        kit.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await kit.RebuildAsync()).Written);

        // A scan alone (only the completion's scan time moves) is no change either.
        await kit.Db.Db.Libraries.Where(l => l.Id == kit.Db.LibraryId).ExecuteUpdateAsync(u => u.SetProperty(l => l.LastScanCompleted, kit.Clock.Now));
        Assert.Equal(0, (await kit.RebuildAsync()).Written);

        kit.Clock.Advance(TimeSpan.FromHours(1));
        await kit.Db.Db.MetadataRecords.Where(r => r.Id == recordA.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.OriginVolumes, 13));
        Assert.Equal(1, (await kit.RebuildAsync()).Written);
        var items = await kit.ItemsAsync();
        Assert.Equal(ExportTestKit.Start.AddHours(2), items[a.PublicId].UpdatedAt);
        Assert.Equal(13, items[a.PublicId].Record!.OriginVolumes);
        Assert.Equal(kit.Clock.Now.AddHours(-1), items[a.PublicId].Completion!.BasedOnScanAt);
        Assert.Equal(ExportTestKit.Start, items[b.PublicId].UpdatedAt);
    }

    [Fact]
    public async Task VanishedItems_BecomeRemovals_WithTheirReason()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var cleared = await kit.Db.AddFolderAsync(null, "Cleared");
        var tombstoned = await kit.Db.AddFolderAsync(null, "Tombstoned");
        var purged = await kit.Db.AddFolderAsync(null, "Purged");
        var traveller = await kit.Db.AddArchiveAsync(await kit.Db.AddFolderAsync(null, "Loose"), "Traveller.cbz");
        foreach (var node in new[] { cleared, tombstoned, purged })
            await kit.Db.AddLinkAsync(node, null, SeriesLinkState.DontMatch);
        await kit.Db.AddLinkAsync(traveller, null, SeriesLinkState.NeedsReview);
        var other = await kit.Db.AddLibraryAsync("otherlib", "Other Lib");
        await kit.RebuildAsync();

        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        await kit.Db.Db.NodeSeriesLinks.Where(l => l.NodeId == cleared.Id).ExecuteDeleteAsync();
        await kit.TombstoneAsync(tombstoned);
        await kit.Db.Db.CatalogNodes.Where(n => n.Id == purged.Id).ExecuteDeleteAsync();
        // An archive keeps its node when a scan of another library claims it.
        await kit.Db.Db.CatalogNodes.Where(n => n.Id == traveller.Id).ExecuteUpdateAsync(u => u.SetProperty(n => n.LibraryId, other.Id).SetProperty(n => n.ParentId, (long?)null));
        await kit.Db.Db.NodeSeriesLinks.Where(l => l.NodeId == traveller.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.LibraryId, other.Id));

        var result = await kit.RebuildAsync();
        await kit.RebuildAsync(other.Id);

        Assert.Equal((0, 4), (result.Items, result.Removed));
        Assert.Equal(new Dictionary<string, string>
        {
            [cleared.PublicId] = "linkCleared",
            [tombstoned.PublicId] = "nodeGone",
            [purged.PublicId] = "nodeGone",
            [traveller.PublicId] = "movedToOtherLibrary",
        }, await kit.RemovalsAsync());
        Assert.Equal([traveller.PublicId], (await kit.ItemsAsync(other.Id)).Keys);
        Assert.Equal(["Traveller.cbz"], (await kit.ItemsAsync(other.Id))[traveller.PublicId].Trail);

        // Incremental: the removals since the first rebuild, on the first page only.
        var page = await kit.PageAsync(updatedSince: ExportJson.Format(ExportTestKit.Start));
        Assert.Equal(4, page.GetProperty("removed").GetArrayLength());
        Assert.Equal(0, page.GetProperty("items").GetArrayLength());
        // A full sync has no removals.
        Assert.Equal(0, (await kit.PageAsync()).GetProperty("removed").GetArrayLength());
    }

    [Fact]
    public async Task ANodeThatComesBack_LosesItsRemoval()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Back Again");
        await kit.Db.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);
        await kit.RebuildAsync();
        await kit.Db.Db.NodeSeriesLinks.ExecuteDeleteAsync();
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await kit.RebuildAsync();
        Assert.Single(await kit.RemovalsAsync());

        await kit.Db.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);
        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        await kit.RebuildAsync();

        Assert.Empty(await kit.RemovalsAsync());
        Assert.Equal(kit.Clock.Now, (await kit.ItemsAsync())[folder.PublicId].UpdatedAt);
    }

    [Fact]
    public async Task CarryOver_ShowsTheOldNodeAsCarriedFrom_InsteadOfARemoval()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var oldFolder = await kit.Db.AddFolderAsync(null, "Synthetic Saga");
        await kit.Db.AddLinkAsync(oldFolder, await kit.Db.AddRecordAsync("77", "Synthetic Saga"));
        await kit.RebuildAsync();

        // The folder is renamed: a new node; carry-over moves the link.
        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        var renamed = await kit.Db.AddFolderAsync(null, "Synthetic Saga (2026)");
        await kit.TombstoneAsync(oldFolder);
        var moved = await kit.CarryOver().MoveRowsAsync(oldFolder.Id, renamed.Id, default);
        Assert.True(moved.Link);

        var result = await kit.RebuildAsync();

        Assert.Equal((1, 0, 1), (result.Items, result.Removed, result.Carried));
        var item = (await kit.ItemsAsync())[renamed.PublicId];
        Assert.Equal(oldFolder.PublicId, item.CarriedFrom);
        Assert.Equal(["Synthetic Saga (2026)"], item.Trail);
        Assert.Empty(await kit.RemovalsAsync());

        // Carried again before a client looked: the newest item names the last id, and the first one is reported removed.
        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        var third = await kit.Db.AddFolderAsync(null, "Synthetic Saga - Complete");
        await kit.TombstoneAsync(renamed);
        await kit.CarryOver().MoveRowsAsync(renamed.Id, third.Id, default);
        await kit.RebuildAsync();
        Assert.Equal(renamed.PublicId, (await kit.ItemsAsync())[third.PublicId].CarriedFrom);
        Assert.Equal(new Dictionary<string, string> { [oldFolder.PublicId] = "nodeGone" }, await kit.RemovalsAsync());
    }

    [Fact]
    public async Task CarryOverIntoAnotherLibrary_IsAMoveThere()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var other = await kit.Db.AddLibraryAsync("otherlib", "Other Lib");
        var oldFolder = await kit.Db.AddFolderAsync(null, "Wanderer");
        await kit.Db.AddLinkAsync(oldFolder, null, SeriesLinkState.DontMatch);
        await kit.RebuildAsync();
        var newFolder = await kit.Db.AddFolderAsync(null, "Wanderer", other.Id);
        await kit.TombstoneAsync(oldFolder);
        await kit.CarryOver().MoveRowsAsync(oldFolder.Id, newFolder.Id, default);

        await kit.RebuildAsync();
        await kit.RebuildAsync(other.Id);

        Assert.Equal(new Dictionary<string, string> { [oldFolder.PublicId] = "movedToOtherLibrary" }, await kit.RemovalsAsync());
        Assert.Equal(oldFolder.PublicId, (await kit.ItemsAsync(other.Id))[newFolder.PublicId].CarriedFrom);
    }

    [Fact]
    public async Task UpdatedSince_BeforeTheWatermark_NeedsAFullSync_AndThePoolFollowsItsWindow()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Kept");
        await kit.Db.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);
        var gone = await kit.Db.AddFolderAsync(null, "Gone");
        await kit.Db.AddLinkAsync(gone, null, SeriesLinkState.DontMatch);
        var export = kit.Export();

        // The first rebuild starts the pool: nothing before it was tracked.
        var first = await export.PageAsync(kit.Db.LibraryPublicId, null, null, null, null);
        Assert.Equal(200, first.Status);
        var before = await export.PageAsync(kit.Db.LibraryPublicId, ExportJson.Format(ExportTestKit.Start.AddMilliseconds(-1)), null, null, null);
        Assert.Equal((409, "fullSyncRequired"), (before.Status, before.Error));
        // Inclusive: the first rebuild's own time returns its items.
        var atStart = await kit.PageAsync(updatedSince: ExportJson.Format(ExportTestKit.Start));
        Assert.Equal(2, atStart.GetProperty("items").GetArrayLength());
        Assert.Equal(ExportJson.Format(ExportTestKit.Start), atStart.GetProperty("serverTime").GetString());

        await kit.Db.Db.NodeSeriesLinks.Where(l => l.NodeId == gone.Id).ExecuteDeleteAsync();
        kit.Clock.Advance(TimeSpan.FromDays(1));
        await kit.RebuildAsync();
        Assert.Single(await kit.RemovalsAsync());

        // 31 days later (trash retention unset = 30 days, the pool's minimum): the removal ages out and the watermark follows.
        kit.Clock.Advance(TimeSpan.FromDays(31));
        await kit.RebuildAsync();
        Assert.Empty(await kit.RemovalsAsync());
        var state = await kit.Db.Db.ExportLibraryStates.AsNoTracking().SingleAsync();
        Assert.Equal(kit.Clock.Now.AddDays(-30), state.WatermarkAt);
        var stale = await kit.Export().PageAsync(kit.Db.LibraryPublicId, ExportJson.Format(ExportTestKit.Start.AddDays(1)), null, null, null);
        Assert.Equal(409, stale.Status);
        var recent = await kit.Export().PageAsync(kit.Db.LibraryPublicId, ExportJson.Format(kit.Clock.Now.AddDays(-2)), null, null, null);
        Assert.Equal(200, recent.Status);
    }

    [Fact]
    public async Task ThePool_CoversALongerTrashRetention()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        kit.Db.Db.AppSettings.Add(new AppSettingsEntity { Id = AppSettingsEntity.SingletonId, TrashRetentionDays = TrashRetention.Quarterly });
        await kit.Db.Db.SaveChangesAsync();
        var gone = await kit.Db.AddFolderAsync(null, "Gone");
        await kit.Db.AddLinkAsync(gone, null, SeriesLinkState.DontMatch);
        await kit.RebuildAsync();
        await kit.Db.Db.NodeSeriesLinks.ExecuteDeleteAsync();
        kit.Clock.Advance(TimeSpan.FromDays(1));
        await kit.RebuildAsync();

        kit.Clock.Advance(TimeSpan.FromDays(60));
        await kit.RebuildAsync();
        Assert.Single(await kit.RemovalsAsync());
    }

    [Fact]
    public async Task Rebuilds_WaitForTheMinimumInterval()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        kit.MinInterval = TimeSpan.FromMinutes(5);
        var folder = await kit.Db.AddFolderAsync(null, "Paced");
        await kit.Db.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);
        var rebuilds = kit.Rebuilds();

        var state = await rebuilds.EnsureFreshAsync(kit.Db.LibraryId);
        Assert.Equal(ExportTestKit.Start, state.LastRebuildAt);
        await kit.Db.Db.NodeSeriesLinks.ExecuteDeleteAsync();

        kit.Clock.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(ExportTestKit.Start, (await rebuilds.EnsureFreshAsync(kit.Db.LibraryId)).LastRebuildAt);
        Assert.Single(await kit.ItemsAsync()); // the stored snapshot is served

        kit.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(kit.Clock.Now, (await rebuilds.EnsureFreshAsync(kit.Db.LibraryId)).LastRebuildAt);
        Assert.Empty(await kit.ItemsAsync());
    }

    [Fact]
    public async Task CompanionsOfficialLinksAndVolumes_ComeFromTheStoredRows()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Linked");
        var series = await kit.Db.AddRecordAsync("4242", "Linked");
        await kit.Db.AddLinkAsync(folder, series);
        var db = kit.Db.Db;
        var mangadex = new MetadataRecordEntity
        {
            PublicId = "mdrec",
            Provider = "mangadex",
            ExternalId = "801513ba-a712-498c-8f57-cae55b38cc92",
            Title = "Linked",
            ExtraJson = JsonSerializer.Serialize(new CompanionLinkService.MangaDexExtra("ja", null, null)
            {
                Links = [new("engtl", "https://publisher.example.com/linked")],
            }),
            FetchedAt = ExportTestKit.Start,
        };
        var aniList = new MetadataRecordEntity
        {
            PublicId = "alrec",
            Provider = "anilist",
            ExternalId = "30002",
            Title = "Linked",
            LatestChapter = 120,
            OriginVolumes = 12,
            CrossIdsJson = "{\"mangaupdates\":\"4242\"}",
            FetchedAt = ExportTestKit.Start,
        };
        db.MetadataRecords.AddRange(mangadex, aniList);
        await db.SaveChangesAsync();
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = series.Id, Provider = "mangadex", CompanionRecordId = mangadex.Id, State = 0 });
        db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = series.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\"]}]",
            ContentHash = "h",
            FetchedAt = ExportTestKit.Start,
        });
        await db.SaveChangesAsync();

        await kit.RebuildAsync();

        var item = (await kit.ItemsAsync())[folder.PublicId];
        Assert.Equal("801513ba-a712-498c-8f57-cae55b38cc92", item.Companions.Mangadex);
        Assert.Equal((30002L, 120, 12), (item.Companions.Anilist!.Id, item.Companions.Anilist.Chapters, item.Companions.Anilist.Volumes));
        Assert.Equal([("publisher", "Official English release", "https://publisher.example.com/linked")],
            item.OfficialLinks.Select(l => (l.Kind, l.Label, l.Url)));
        Assert.Equal([("1", "1", "2")], item.Volumes!.Items.Select(v => (v.Volume, v.Chapters!.From, v.Chapters.To)));
    }

    [Fact]
    public async Task MangaDexReleased_ComesFromTheStoredPreferredLanguageList_AndIsLeftOutWithoutOne()
    {
        // 1.42.0 (MangaList): MangaDex's released chapters in the preferred language - the stored list only, no request.
        await using var kit = await ExportTestKit.CreateAsync();
        var read = await kit.Db.AddFolderAsync(null, "Read In English");
        var readRecord = await kit.Db.AddRecordAsync("5151", "Read In English");
        await kit.Db.AddLinkAsync(read, readRecord);
        var unread = await kit.Db.AddFolderAsync(null, "Not Read In English");
        var unreadRecord = await kit.Db.AddRecordAsync("5252", "Not Read In English");
        await kit.Db.AddLinkAsync(unread, unreadRecord);
        var about = await kit.Db.AddFolderAsync(null, "About Read In English");
        await kit.Db.AddLinkAsync(about, readRecord, SeriesLinkState.CollectionAbout);
        var fetched = ExportTestKit.Start.AddHours(-3);
        kit.Db.Db.SeriesVolumeMaps.AddRange(
            new SeriesVolumeMapEntity
            {
                RecordId = readRecord.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Empty,
                ReleasedLanguage = "en",
                ReleasedChaptersJson = "[\"1\",\"2\",\"4.1\",\"4.2\",\"18\"]",
                ContentHash = "r",
                FetchedAt = fetched,
            },
            new SeriesVolumeMapEntity
            {
                RecordId = unreadRecord.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\"]}]",
                ContentHash = "u",
                FetchedAt = fetched,
            });
        await kit.Db.Db.SaveChangesAsync();

        await kit.RebuildAsync();

        var items = (await kit.PageAsync()).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("nodeId").GetString()!);
        Assert.True(items[read.PublicId].GetProperty("companions").TryGetProperty("mangadexReleased", out var released),
            "the series with a stored English list exports it");
        Assert.Equal("en", released.GetProperty("language").GetString());
        Assert.Equal("18", released.GetProperty("latestChapter").GetString());
        Assert.Equal(4, released.GetProperty("chapters").GetInt32());
        Assert.Equal(ExportJson.Format(fetched), released.GetProperty("fetchedAt").GetString());
        // Not read for a language, or a "Collection about" label: the field is left out (older MangaList builds see no change).
        Assert.False(items[unread.PublicId].GetProperty("companions").TryGetProperty("mangadexReleased", out _));
        Assert.False(items[about.PublicId].GetProperty("companions").TryGetProperty("mangadexReleased", out _));
    }

    [Fact]
    public async Task MangaDexReadable_GivesTheHighestReadableWholeChapter_AndIsLeftOutWhenNotRead()
    {
        // 1.43.0 (MangaList, owner 2026-10-15): latestChapter / chapters count chapters MangaDex took down; readable names the highest
        // WHOLE chapter that can be read there. Three series: partly taken down, all taken down, and a list read before 1.43.0.
        await using var kit = await ExportTestKit.CreateAsync();
        var released = "[\"1\",\"2\",\"3\",\"4.1\",\"4.2\",\"5.5\",\"18\"]";
        async Task<(string NodeId, long RecordId)> SeriesAsync(string mu, string name, string? readable)
        {
            var folder = await kit.Db.AddFolderAsync(null, name);
            var record = await kit.Db.AddRecordAsync(mu, name);
            await kit.Db.AddLinkAsync(folder, record);
            kit.Db.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = record.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Empty,
                ReleasedLanguage = "en",
                ReleasedChaptersJson = released,
                ReadableChaptersJson = readable,
                ContentHash = mu,
                FetchedAt = ExportTestKit.Start.AddHours(-1),
            });
            return (folder.PublicId, record.Id);
        }
        var partly = await SeriesAsync("6161", "Partly Taken Down", "[\"1\",\"2\",\"4.1\",\"4.2\",\"5.5\"]");
        var none = await SeriesAsync("6262", "All Taken Down", "[]");
        var before = await SeriesAsync("6363", "Read Before The Field", null);
        await kit.Db.Db.SaveChangesAsync();

        await kit.RebuildAsync();

        var items = (await kit.PageAsync()).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("nodeId").GetString()!);
        JsonElement Released(string nodeId) => items[nodeId].GetProperty("companions").GetProperty("mangadexReleased");

        // The released list keeps its meaning: taken-down chapters still count.
        Assert.All(new[] { partly, none, before }, s => Assert.Equal(("18", 6),
            (Released(s.NodeId).GetProperty("latestChapter").GetString(), Released(s.NodeId).GetProperty("chapters").GetInt32())));
        // Readable up to 5.5 (an extra after 5): the highest whole chapter is 5; 1, 2, 4 (as 4.1 + 4.2) and 5 are four whole chapters.
        var readable = Released(partly.NodeId).GetProperty("readable");
        Assert.Equal(("5", 4), (readable.GetProperty("latestWholeChapter").GetString(), readable.GetProperty("chapters").GetInt32()));
        // Nothing readable: the object is there, its latest chapter null.
        var nothing = Released(none.NodeId).GetProperty("readable");
        Assert.Equal(JsonValueKind.Null, nothing.GetProperty("latestWholeChapter").ValueKind);
        Assert.Equal(0, nothing.GetProperty("chapters").GetInt32());
        // Not read yet (a list stored before 1.43.0): left out, the item exactly as before.
        Assert.False(Released(before.NodeId).TryGetProperty("readable", out _));
    }

    [Fact]
    public async Task EstimatedVolumes_AreExportedWithTheirChapterFiles_AndFlagged()
    {
        // 1.42.0 (MangaList): the Volumes view's "~ Volume N" stacks - a held volume whose chapters the list does not map - with the
        // chapter files the estimate places in it, so MangaList can offer "replace these chapters with the volume".
        await using var kit = await ExportTestKit.CreateAsync();
        var series = await kit.Db.AddFolderAsync(null, "Synthetic Saga");
        foreach (var name in new[] { "Synthetic Saga v01.cbz", "Synthetic Saga v02.cbz", "Synthetic Saga v03.cbz",
                     "Synthetic Saga c011.cbz", "Synthetic Saga c012.cbz", "Synthetic Saga c013.cbz", "Synthetic Saga c016.cbz" })
            await kit.Db.AddArchiveAsync(series, name);
        var record = await kit.Db.AddRecordAsync("9301", "Synthetic Saga");
        await kit.Db.AddLinkAsync(series, record);
        var listed = await kit.Db.AddFolderAsync(null, "Synthetic Listed");
        await kit.Db.AddArchiveAsync(listed, "Synthetic Listed v01.cbz");
        await kit.Db.AddArchiveAsync(listed, "Synthetic Listed v02.cbz");
        var listedRecord = await kit.Db.AddRecordAsync("9302", "Synthetic Listed");
        await kit.Db.AddLinkAsync(listed, listedRecord);
        foreach (var r in new[] { record, listedRecord })
        {
            kit.Db.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = r.Id,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                // The list maps volumes 1-2; MangaDex knows 4 volumes at 5 chapters each, so 3 and 4 are estimated (11-15, 16-20).
                VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\",\"3\",\"4\",\"5\"]},{\"v\":\"2\",\"c\":[\"6\",\"7\",\"8\",\"9\",\"10\"]}]",
                ChaptersPerVolume = 5,
                KnownVolumeCount = 4,
                ContentHash = "h" + r.Id,
                FetchedAt = ExportTestKit.Start,
            });
        }
        await kit.Db.Db.SaveChangesAsync();

        await kit.RebuildAsync();

        var items = (await kit.PageAsync()).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("nodeId").GetString()!);
        var rows = items[series.PublicId].GetProperty("volumes").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["1", "2", "3", "4"], rows.Select(r => r.GetProperty("volume").GetString()));
        // The list's own rows are unchanged and carry no flag.
        Assert.False(rows[0].TryGetProperty("chaptersEstimated", out _));
        Assert.Equal(("1", "5"), (rows[0].GetProperty("chapters").GetProperty("from").GetString(), rows[0].GetProperty("chapters").GetProperty("to").GetString()));
        // Volume 3: the volume file + chapters 11-13 here; volume 4: chapter 16 only - the chapter FILES the estimate places in each.
        Assert.True(rows[2].GetProperty("chaptersEstimated").GetBoolean());
        Assert.Equal(("11", "13"), (rows[2].GetProperty("chapters").GetProperty("from").GetString(), rows[2].GetProperty("chapters").GetProperty("to").GetString()));
        Assert.Equal(["estimate"], rows[2].GetProperty("sources").EnumerateArray().Select(s => s.GetString()));
        Assert.True(rows[3].GetProperty("chaptersEstimated").GetBoolean());
        Assert.Equal(("16", "16"), (rows[3].GetProperty("chapters").GetProperty("from").GetString(), rows[3].GetProperty("chapters").GetProperty("to").GetString()));
        // A folder that holds only listed volumes gets no estimated rows (its item keeps its form).
        var listedRows = items[listed.PublicId].GetProperty("volumes").GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(["1", "2"], listedRows.Select(r => r.GetProperty("volume").GetString()));
        Assert.All(listedRows, r => Assert.False(r.TryGetProperty("chaptersEstimated", out _)));
    }

    [Fact]
    public async Task Duplicates_NameEachFileByItsNodeId_AndAreLeftOutWhenThereAreNone()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var series = await kit.Db.AddFolderAsync(null, "Synthetic Series");
        var first = await kit.Db.AddArchiveAsync(series, "Synthetic Series c001.cbz");
        var again = await kit.Db.AddArchiveAsync(series, "Synthetic Series c001 [v2].cbz");
        await kit.Db.AddArchiveAsync(series, "Synthetic Series c002.cbz");
        await kit.Db.AddLinkAsync(series, await kit.Db.AddRecordAsync("9101", "Synthetic Series"));
        var clean = await kit.Db.AddFolderAsync(null, "Synthetic Other");
        await kit.Db.AddArchiveAsync(clean, "Synthetic Other c001.cbz");
        await kit.Db.AddArchiveAsync(clean, "Synthetic Other c002.cbz");
        await kit.Db.AddLinkAsync(clean, await kit.Db.AddRecordAsync("9102", "Synthetic Other"));

        await kit.RebuildAsync();
        var items = (await kit.PageAsync()).GetProperty("items").EnumerateArray()
            .ToDictionary(i => i.GetProperty("nodeId").GetString()!);

        var duplicate = Assert.Single(items[series.PublicId].GetProperty("duplicates").EnumerateArray());
        Assert.Equal(("Chapter", "1"), (duplicate.GetProperty("kind").GetString(), duplicate.GetProperty("number").GetString()));
        var files = duplicate.GetProperty("files").EnumerateArray().ToList();
        // In the folder's name order (a space sorts before the dot).
        Assert.Equal([again.PublicId, first.PublicId], files.Select(f => f.GetProperty("nodeId").GetString()));
        Assert.Equal(["Synthetic Series c001 [v2].cbz", "Synthetic Series c001.cbz"], files.Select(f => f.GetProperty("name").GetString()));
        Assert.Equal(JsonValueKind.Null, files[0].GetProperty("folder").ValueKind); // in the series folder itself
        // No duplicates: the key is left out, so the item's stored form (and its fingerprint) is what it was before 1.38.0.
        Assert.False(items[clean.PublicId].TryGetProperty("duplicates", out _));

        var withoutBlock = (await kit.PageAsync(include: "completion")).GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("nodeId").GetString() == series.PublicId);
        Assert.False(withoutBlock.TryGetProperty("duplicates", out _));
    }

    [Fact]
    public async Task Paging_WalksEveryItemOnce_AndIncludeDropsBlocks()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        for (var i = 0; i < 5; i++)
            await kit.Db.AddLinkAsync(await kit.Db.AddFolderAsync(null, $"Series {i}"), await kit.Db.AddRecordAsync($"{100 + i}", $"Series {i}"));

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = await kit.PageAsync(limit: 2, cursor: cursor, include: "refresh");
            foreach (var item in page.GetProperty("items").EnumerateArray())
            {
                seen.Add(item.GetProperty("nodeId").GetString()!);
                Assert.True(item.TryGetProperty("refresh", out _));
                Assert.False(item.TryGetProperty("volumes", out _));
                Assert.False(item.TryGetProperty("completion", out _));
            }
            cursor = page.GetProperty("nextCursor").GetString();
            pages++;
        }
        while (cursor is not null);

        Assert.Equal(3, pages);
        Assert.Equal(5, seen.Distinct().Count());
        Assert.Equal(5, seen.Count);
    }

    [Fact]
    public async Task Errors_AreCodes()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var export = kit.Export();
        Assert.Equal((400, "libraryRequired"), Code(await export.PageAsync(null, null, null, null, null)));
        Assert.Equal((404, "libraryNotFound"), Code(await export.PageAsync("nolib", null, null, null, null)));
        Assert.Equal((400, "invalidUpdatedSince"), Code(await export.PageAsync(kit.Db.LibraryPublicId, "yesterday", null, null, null)));
        Assert.Equal((400, "invalidCursor"), Code(await export.PageAsync(kit.Db.LibraryPublicId, null, "@@", null, null)));
        Assert.Equal((400, "invalidInclude"), Code(await export.PageAsync(kit.Db.LibraryPublicId, null, null, null, "path")));

        static (int, string?) Code(ExportAnswer a) => (a.Status, a.Error);
    }
}
