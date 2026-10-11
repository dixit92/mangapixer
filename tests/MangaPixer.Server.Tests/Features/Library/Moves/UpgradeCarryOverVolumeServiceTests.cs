namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the 1.41.0 carry-over follow-ups (owner decisions of 2026-10-11): a RANGE volume file (<c>v01-03</c>) replacing
/// the chapters of every volume in the range, and a volume file replacing a volume file (a re-encode or re-release, with or without a series
/// link, guarded by the name). Driven through the public pass (<c>MovePairingService.RunAsync</c>) and asserted on stored state only. The
/// shapes follow the live census (counts only); synthetic names and bytes.
/// </summary>
public sealed class UpgradeCarryOverVolumeServiceTests : IDisposable
{
    private const string Folder = "Synthetic Saga";
    private const string RangeName = "Synthetic Saga v01-03 (2026) (Digital).cbz";
    private const int Pages = 8;

    private readonly MoveTestHarness _h = new();
    private long _lib;
    private int _seed = 100;

    public void Dispose() => _h.Dispose();

    private static string Chapter(int number) => $"Synthetic Saga c{number:000}.cbz";

    private static string Volume(int number, string suffix = "") => $"Synthetic Saga v{number:00}{suffix}.cbz";

    private static string ListJson(params (int Volume, int From, int To)[] volumes) =>
        "[" + string.Join(",", volumes.Select(v =>
            $"{{\"v\":\"{v.Volume}\",\"c\":[{string.Join(",", Enumerable.Range(v.From, v.To - v.From + 1).Select(c => $"\"{c}\""))}]}}")) + "]";

    /// <summary>Volumes 1-4 by nine chapters each (1-9, 10-18, 19-27, 28-36).</summary>
    private static string FourVolumes => ListJson((1, 1, 9), (2, 10, 18), (3, 19, 27), (4, 28, 36));

    /// <summary>The folder with the given files, scanned and analysed (pages per file when given); linked (Confirmed) with a list when given.</summary>
    private async Task<Dictionary<string, long>> SeedAsync(IEnumerable<string> files, string? listJson, bool link = true,
        SeriesLinkState state = SeriesLinkState.Confirmed, string folder = Folder, IReadOnlyDictionary<string, int>? pages = null)
    {
        await _h.InitAsync();
        _lib = await _h.AddLibraryAsync("manga");
        var names = files.ToList();
        foreach (var name in names)
            _h.WriteArchive(_lib, $"{folder}/{name}", _seed++);
        await _h.ScanAsync(_lib);
        await _h.AnalyseLibraryAsync(_lib, Pages);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
        {
            ids[name] = (await _h.NodeAsync(_lib, $"{folder}/{name}")).Id;
            if (pages?.GetValueOrDefault(name) is { } count and > 0)
                await _h.AnalyseAsync(ids[name], count);
        }
        if (link)
        {
            var folderNode = await _h.NodeAsync(_lib, folder);
            var record = await _h.AddRecordAsync("500", "Synthetic Saga");
            await _h.LinkAsync(folderNode.Id, state, state is SeriesLinkState.DontMatch or SeriesLinkState.ArtistFolder ? null : record);
            if (listJson is not null)
            {
                await using var db = _h.NewContext();
                db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
                {
                    RecordId = record,
                    Source = (int)VolumeMapSource.MangaDexAggregate,
                    State = (int)VolumeMapState.Ok,
                    VolumesJson = listJson,
                    ChaptersPerVolume = 9,
                    KnownVolumeCount = 4,
                    ContentHash = "h1",
                    Version = 1,
                    FetchedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }
        return ids;
    }

    /// <summary>The replacement: the removed files go, the new files come; one scan sees both; the new files are analysed.</summary>
    private async Task<Dictionary<string, long>> ReplaceAsync(IEnumerable<string> removed, IReadOnlyDictionary<string, int> added, string folder = Folder,
        bool analyse = true)
    {
        foreach (var name in removed)
            File.Delete(_h.PathOf(_lib, $"{folder}/{name}"));
        var seed = 9000;
        foreach (var name in added.Keys)
            _h.WriteArchive(_lib, $"{folder}/{name}", seed++);
        await _h.ScanAsync(_lib);
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var (name, pages) in added)
        {
            ids[name] = (await _h.NodeAsync(_lib, $"{folder}/{name}")).Id;
            if (analyse)
                await _h.AnalyseAsync(ids[name], pages);
        }
        return ids;
    }

    private async Task<ReadMarkEntity?> MarkAsync(long userId, long itemId)
    {
        await using var db = _h.NewContext();
        return await db.ReadMarks.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.ItemId == itemId);
    }

    private Task<int> StateRowsAsync(long itemId) =>
        _h.CountAsync(async db => await db.ReadMarks.CountAsync(m => m.ItemId == itemId) + await db.ReadingProgress.CountAsync(p => p.ItemId == itemId));

    private async Task AssertReadAsync(long userId, long item, int pages = Pages)
    {
        var mark = await MarkAsync(userId, item);
        Assert.NotNull(mark);
        Assert.Equal("upgrade", mark!.Source);
        var progress = await _h.ProgressAsync(userId, item);
        Assert.NotNull(progress);
        Assert.Equal(2, progress!.State);
        Assert.Equal(pages - 1, progress.Ordinal);
    }

    private async Task AssertInProgressAsync(long userId, long item, int ordinal)
    {
        Assert.Null(await MarkAsync(userId, item));
        var progress = await _h.ProgressAsync(userId, item);
        Assert.NotNull(progress);
        Assert.Equal(1, progress!.State);
        Assert.Equal(ordinal, progress.Ordinal);
    }

    private async Task AssertNothingAsync(long userId, long item)
    {
        Assert.Null(await MarkAsync(userId, item));
        Assert.Null(await _h.ProgressAsync(userId, item));
    }

    private static IEnumerable<string> Chapters(int from, int to) => Enumerable.Range(from, to - from + 1).Select(Chapter);

    // (a) + (b): v01-03 replacing chapters 1-27 listed across three volumes.
    [Fact]
    public async Task CaseA_ARangeFileReplacingEveryChapterOfItsVolumes_IsRead_AndCaseB_OneRequiredChapterUnread_IsInProgress()
    {
        var files = Chapters(1, 27).ToList();
        var ids = await SeedAsync(files, FourVolumes);
        var all = await _h.AddUserAsync("all");
        var missing = await _h.AddUserAsync("missing14");
        var first = await _h.AddUserAsync("firstonly");
        var bystander = await _h.AddUserAsync("bystander");
        foreach (var (name, id) in ids)
        {
            await _h.MarkReadAsync(all, id);
            if (name != Chapter(14))
                await _h.MarkReadAsync(missing, id);
        }
        await _h.SetProgressAsync(first, ids[Chapter(1)], 3, state: 2);

        var range = (await ReplaceAsync(files, new Dictionary<string, int> { [RangeName] = Pages }))[RangeName];
        await _h.PairAsync();

        await AssertReadAsync(all, range);
        await AssertInProgressAsync(missing, range, 0);
        await AssertInProgressAsync(first, range, 0);
        await AssertNothingAsync(bystander, range);
        // The replaced chapters keep their marks, re-tagged as carried (the 1.40.0 reset guard).
        Assert.Equal("carried", (await MarkAsync(all, ids[Chapter(1)]))!.Source);

        var before = await _h.ProgressAsync(missing, range);
        await _h.PairAsync();
        Assert.Equal(before!.Revision, (await _h.ProgressAsync(missing, range))!.Revision);
        Assert.Equal(1, await _h.CountAsync(db => db.ReadMarks.CountAsync(m => m.UserId == all && m.ItemId == range)));
    }

    [Fact]
    public async Task ARangeWithAVolumeTheListDoesNotGiveExactly_GivesNothing()
    {
        // The list gives volumes 1 and 2 only: volume 3 of the range would be a guess.
        var files = Chapters(1, 27).ToList();
        var ids = await SeedAsync(files, ListJson((1, 1, 9), (2, 10, 18)));
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);

        var range = (await ReplaceAsync(files, new Dictionary<string, int> { [RangeName] = Pages }))[RangeName];
        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(range));
    }

    [Fact]
    public async Task ARangeFile_ExistingStateIsNeverOverwrittenOrLowered()
    {
        var files = Chapters(1, 27).ToList();
        var ids = await SeedAsync(files, FourVolumes);
        var marked = await _h.AddUserAsync("marked");
        var reading = await _h.AddUserAsync("reading");
        var finished = await _h.AddUserAsync("finished");
        var opened = await _h.AddUserAsync("opened");
        foreach (var user in new[] { marked, reading, finished, opened })
            foreach (var id in ids.Values)
                await _h.MarkReadAsync(user, id);

        var range = (await ReplaceAsync(files, new Dictionary<string, int> { [RangeName] = Pages }))[RangeName];
        await _h.MarkReadAsync(marked, range);
        await _h.SetProgressAsync(reading, range, 3, state: 1);
        await _h.SetProgressAsync(finished, range, 2, state: 2);
        await _h.SetProgressAsync(opened, range, 0, state: 1);
        await _h.PairAsync();

        Assert.Equal("manual", (await MarkAsync(marked, range))!.Source);
        Assert.Null(await _h.ProgressAsync(marked, range));
        Assert.Null(await MarkAsync(reading, range));
        Assert.Equal((1, 3), ((await _h.ProgressAsync(reading, range))!.State, (await _h.ProgressAsync(reading, range))!.Ordinal));
        Assert.Null(await MarkAsync(finished, range));
        Assert.Equal((2, 2), ((await _h.ProgressAsync(finished, range))!.State, (await _h.ProgressAsync(finished, range))!.Ordinal));
        // Opened at page 1 says nothing beyond "opened": raised to read (1.40.0 R6).
        await AssertReadAsync(opened, range);
    }

    // (c) A re-encoded v02 replacing v02, in a folder with NO series link.
    [Fact]
    public async Task CaseC_AVolumeFileReplacingTheSameVolume_CarriesRead_WithoutASeriesLink()
    {
        var ids = await SeedAsync([Volume(1), Volume(2)], listJson: null, link: false);
        var reader = await _h.AddUserAsync("reader");
        var completed = await _h.AddUserAsync("completed");
        var bystander = await _h.AddUserAsync("bystander");
        await _h.MarkReadAsync(reader, ids[Volume(2)]);
        await _h.SetProgressAsync(completed, ids[Volume(2)], Pages - 1, state: 2);
        await _h.MarkReadAsync(bystander, ids[Volume(1)]);

        var reencode = Volume(2, " (Re-encode) [HQ]");
        var neu = (await ReplaceAsync([Volume(2)], new Dictionary<string, int> { [reencode] = Pages }))[reencode];
        await _h.PairAsync();

        await AssertReadAsync(reader, neu);
        await AssertReadAsync(completed, neu);
        await AssertNothingAsync(bystander, neu);
        Assert.Equal("carried", (await MarkAsync(reader, ids[Volume(2)]))!.Source);

        await _h.PairAsync();
        Assert.Equal(1, await _h.CountAsync(db => db.ReadMarks.CountAsync(m => m.UserId == reader && m.ItemId == neu)));
    }

    // (d) In progress at page 30 of 180: the same page when the page counts agree, else page 1.
    [Theory]
    [InlineData(180, 30)]
    [InlineData(175, 0)]
    public async Task CaseD_AnInProgressVolumeReplacingAVolume_KeepsThePageOnlyWithEqualPageCounts(int newPages, int expected)
    {
        var ids = await SeedAsync([Volume(2)], listJson: null, link: false, pages: new Dictionary<string, int> { [Volume(2)] = 180 });
        var reader = await _h.AddUserAsync("reader");
        var opened = await _h.AddUserAsync("opened");
        await _h.SetProgressAsync(reader, ids[Volume(2)], 30, state: 1);
        await _h.SetProgressAsync(opened, ids[Volume(2)], 0, state: 1);

        var reencode = Volume(2, " [Re-release]");
        var neu = (await ReplaceAsync([Volume(2)], new Dictionary<string, int> { [reencode] = newPages }))[reencode];
        await _h.PairAsync();

        await AssertInProgressAsync(reader, neu, expected);
        // An old volume merely opened at page 1 says nothing.
        await AssertNothingAsync(opened, neu);

        var before = await _h.ProgressAsync(reader, neu);
        await _h.PairAsync();
        Assert.Equal(before!.Revision, (await _h.ProgressAsync(reader, neu))!.Revision);
    }

    // (e) v01-03 replacing the v01 and v02 volume files and chapters 19-27 of volume 3.
    [Fact]
    public async Task CaseE_ARangeCoveredByVolumeFilesAndChapters_IsReadWhenAllAreRead()
    {
        var files = new[] { Volume(1), Volume(2) }.Concat(Chapters(19, 27)).ToList();
        var ids = await SeedAsync(files, FourVolumes);
        var all = await _h.AddUserAsync("all");
        var noV2 = await _h.AddUserAsync("nov2");
        var v2Only = await _h.AddUserAsync("v2only");
        foreach (var (name, id) in ids)
        {
            await _h.MarkReadAsync(all, id);
            if (name != Volume(2))
                await _h.MarkReadAsync(noV2, id);
        }
        await _h.SetProgressAsync(v2Only, ids[Volume(2)], 4, state: 1);

        var range = (await ReplaceAsync(files, new Dictionary<string, int> { [RangeName] = Pages }))[RangeName];
        await _h.PairAsync();

        await AssertReadAsync(all, range);
        await AssertInProgressAsync(noV2, range, 0);
        // A volume file in progress is part of the range read: in progress at page 1 (a range never keeps a page).
        await AssertInProgressAsync(v2Only, range, 0);
    }

    [Fact]
    public async Task ARangeCoveredOnlyByVolumeFiles_NeedsNoSeriesLink()
    {
        var files = new[] { Volume(1), Volume(2), Volume(3) };
        var ids = await SeedAsync(files, listJson: null, link: false);
        var reader = await _h.AddUserAsync("reader");
        var two = await _h.AddUserAsync("two");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);
        await _h.MarkReadAsync(two, ids[Volume(1)]);
        await _h.MarkReadAsync(two, ids[Volume(2)]);

        var range = (await ReplaceAsync(files, new Dictionary<string, int> { [RangeName] = Pages }))[RangeName];
        await _h.PairAsync();

        await AssertReadAsync(reader, range);
        await AssertInProgressAsync(two, range, 0);
    }

    // (f) Another series' v03 in the same flat folder is not evidence (the edition guard); that series' own v02 re-release is.
    [Fact]
    public async Task CaseF_AnotherWorksVolumeInTheSameFolder_IsNotEvidence_ItsOwnReReleaseIs()
    {
        const string shelf = "Shelf";
        var ids = await SeedAsync(["Synthetic Saga v03.cbz", "Other Tale v02.cbz"], listJson: null, link: false, folder: shelf);
        var reader = await _h.AddUserAsync("reader");
        await _h.MarkReadAsync(reader, ids["Synthetic Saga v03.cbz"]);
        await _h.MarkReadAsync(reader, ids["Other Tale v02.cbz"]);

        var added = await ReplaceAsync(["Synthetic Saga v03.cbz", "Other Tale v02.cbz"],
            new Dictionary<string, int> { ["Other Tale v03.cbz"] = Pages, ["Other Tale v02 (2026) [Re-release].cbz"] = Pages }, folder: shelf);
        await _h.PairAsync();

        await AssertReadAsync(reader, added["Other Tale v02 (2026) [Re-release].cbz"]);
        await AssertNothingAsync(reader, added["Other Tale v03.cbz"]);
    }

    [Fact]
    public async Task AnotherEditionOfTheSameVolume_IsNotEvidence()
    {
        var ids = await SeedAsync([Volume(3)], listJson: null, link: false);
        var reader = await _h.AddUserAsync("reader");
        await _h.MarkReadAsync(reader, ids[Volume(3)]);

        var kanzenban = "Synthetic Saga Kanzenban v03.cbz";
        var neu = (await ReplaceAsync([Volume(3)], new Dictionary<string, int> { [kanzenban] = Pages }))[kanzenban];
        await _h.PairAsync();

        await AssertNothingAsync(reader, neu);
    }

    [Fact]
    public async Task AVolumeReplacingAVolume_ExistingStateIsNeverOverwrittenOrLowered()
    {
        var ids = await SeedAsync([Volume(2)], listJson: null, link: false, pages: new Dictionary<string, int> { [Volume(2)] = 40 });
        var marked = await _h.AddUserAsync("marked");
        var further = await _h.AddUserAsync("further");
        var finished = await _h.AddUserAsync("finished");
        var opened = await _h.AddUserAsync("opened");
        await _h.MarkReadAsync(marked, ids[Volume(2)]);
        await _h.SetProgressAsync(further, ids[Volume(2)], 30, state: 1);
        await _h.SetProgressAsync(finished, ids[Volume(2)], 10, state: 1);
        await _h.SetProgressAsync(opened, ids[Volume(2)], 20, state: 1);

        var reencode = Volume(2, " (Re-encode)");
        var neu = (await ReplaceAsync([Volume(2)], new Dictionary<string, int> { [reencode] = 40 }))[reencode];
        await _h.SetProgressAsync(marked, neu, 5, state: 1);     // read the old one, now reading the new one at page 6
        await _h.SetProgressAsync(further, neu, 35, state: 1);   // further on the new one than on the old one
        await _h.SetProgressAsync(finished, neu, 39, state: 2);  // finished the new one
        await _h.SetProgressAsync(opened, neu, 0, state: 1);     // opened the new one at page 1
        await _h.PairAsync();

        Assert.Null(await MarkAsync(marked, neu));
        Assert.Equal((1, 5), ((await _h.ProgressAsync(marked, neu))!.State, (await _h.ProgressAsync(marked, neu))!.Ordinal));
        Assert.Equal((1, 35), ((await _h.ProgressAsync(further, neu))!.State, (await _h.ProgressAsync(further, neu))!.Ordinal));
        Assert.Null(await MarkAsync(finished, neu));
        Assert.Equal((2, 39), ((await _h.ProgressAsync(finished, neu))!.State, (await _h.ProgressAsync(finished, neu))!.Ordinal));
        // Opened at page 1: raised to the old page (equal page counts).
        await AssertInProgressAsync(opened, neu, 20);
    }

    [Fact]
    public async Task TwoReplacedFilesOfOneVolume_TheMostRecentlyReadOneDecidesPerUser()
    {
        var a = Volume(2, " [GroupA]");
        var b = Volume(2, " [GroupB]");
        var ids = await SeedAsync([a, b], listJson: null, link: false, pages: new Dictionary<string, int> { [a] = 50, [b] = 50 });
        var rereading = await _h.AddUserAsync("rereading");
        var finishedLater = await _h.AddUserAsync("finishedlater");
        await _h.MarkReadAsync(rereading, ids[a]);
        await Task.Delay(20);
        await _h.SetProgressAsync(rereading, ids[b], 12, state: 1);
        await _h.SetProgressAsync(finishedLater, ids[b], 12, state: 1);
        await Task.Delay(20);
        await _h.MarkReadAsync(finishedLater, ids[a]);

        var reencode = Volume(2, " (Re-encode)");
        var neu = (await ReplaceAsync([a, b], new Dictionary<string, int> { [reencode] = 50 }))[reencode];
        await _h.PairAsync();

        await AssertInProgressAsync(rereading, neu, 12);
        await AssertReadAsync(finishedLater, neu, 50);
    }

    [Fact]
    public async Task IdenticalBytes_AreNeverAnUpgrade_EvenWhenTheMoveIsNotPaired()
    {
        var ids = await SeedAsync([Volume(2)], listJson: null, link: false);
        var reader = await _h.AddUserAsync("reader");
        await _h.MarkReadAsync(reader, ids[Volume(2)]);

        // The new file carries the old file's content signature (the same bytes) but another page list, so the 1.31.0 pairing leaves it
        // (a manifest mismatch): it is a move left undecided, never an upgrade.
        var renamed = Volume(2, " (Renamed)");
        var neu = (await ReplaceAsync([Volume(2)], new Dictionary<string, int> { [renamed] = Pages + 1 }))[renamed];
        await using (var db = _h.NewContext())
        {
            var old = await db.ArchiveItems.AsNoTracking().SingleAsync(a => a.NodeId == ids[Volume(2)]);
            await db.ArchiveItems.Where(a => a.NodeId == neu).ExecuteUpdateAsync(u => u
                .SetProperty(a => a.ContentSignature, old.ContentSignature)
                .SetProperty(a => a.ByteLength, old.ByteLength));
        }
        await _h.PairAsync();

        Assert.Equal(0, await _h.CountAsync(db => db.NodeMoves.CountAsync(m => m.FromNodeId == ids[Volume(2)])));
        await AssertNothingAsync(reader, neu);
    }

    [Fact]
    public async Task AVolumeReplacingAVolume_InAFolderMarkedDontMatch_IsNotCarried()
    {
        var ids = await SeedAsync([Volume(2)], listJson: null, state: SeriesLinkState.DontMatch);
        var reader = await _h.AddUserAsync("reader");
        await _h.MarkReadAsync(reader, ids[Volume(2)]);

        var reencode = Volume(2, " (Re-encode)");
        var neu = (await ReplaceAsync([Volume(2)], new Dictionary<string, int> { [reencode] = Pages }))[reencode];
        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(neu));
    }

    [Fact]
    public async Task AVolumeMarkedUnreadAfterAVolumeCarry_IsNotCarriedAgain()
    {
        var ids = await SeedAsync([Volume(2)], listJson: null, link: false);
        var reader = await _h.AddUserAsync("reader");
        await _h.MarkReadAsync(reader, ids[Volume(2)]);
        var reencode = Volume(2, " (Re-encode)");
        var neu = (await ReplaceAsync([Volume(2)], new Dictionary<string, int> { [reencode] = Pages }))[reencode];
        await _h.PairAsync();
        await AssertReadAsync(reader, neu);

        await using (var db = _h.NewContext())
        {
            await db.ReadMarks.Where(m => m.ItemId == neu).ExecuteDeleteAsync();
            await db.ReadingProgress.Where(p => p.ItemId == neu).ExecuteDeleteAsync();
        }
        await _h.PairAsync();

        await AssertNothingAsync(reader, neu);
    }

    [Fact]
    public async Task AVolumeFileTheCatalogSawNextToTheOldOne_IsNotAReplacement()
    {
        var ids = await SeedAsync([Volume(2)], listJson: null, link: false);
        var reader = await _h.AddUserAsync("reader");
        await _h.MarkReadAsync(reader, ids[Volume(2)]);
        // The re-release arrives first and is scanned next to the old file; the old file is deleted only later.
        var reencode = Volume(2, " (Re-encode)");
        _h.WriteArchive(_lib, $"{Folder}/{reencode}", 9999);
        await _h.ScanAsync(_lib);
        var neu = (await _h.NodeAsync(_lib, $"{Folder}/{reencode}")).Id;
        await _h.AnalyseAsync(neu, Pages);
        File.Delete(_h.PathOf(_lib, $"{Folder}/{Volume(2)}"));
        await _h.ScanAsync(_lib);

        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(neu));
    }
}
