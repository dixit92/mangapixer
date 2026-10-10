namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of read state that follows a chapter-to-volume upgrade (1.40.0, owner finding of 2026-10-10): a folder's chapter
/// archives are replaced by the volume archive that holds them; the move-pairing pass carries the users' read state of the replaced chapters
/// onto the volume by the stored volume list. Driven through the public pass (<c>MovePairingService.RunAsync</c>) and asserted on stored
/// state only. The shapes follow the live census (counts only): (a) every listed chapter + its .5 extra, all read; (b) 9 of a 12-unit list
/// present (3, 4.1, 4.2 never were); (c) two files per chapter. Synthetic names and bytes.
/// </summary>
public sealed class UpgradeCarryOverServiceTests : IDisposable
{
    private const string Folder = "Synthetic Saga";
    private const string VolumeName = "Synthetic Saga v01 (2026) (Digital).cbz";
    private const int VolumePages = 8;

    private readonly MoveTestHarness _h = new();
    private long _lib;
    private int _seed = 100;

    public void Dispose() => _h.Dispose();

    private static string Chapter(string number) => $"Synthetic Saga c{number}.cbz";

    private static string Pad(decimal n) => n.ToString("000.##", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>A volume list: <c>(volume, chapters)</c> as stored (<c>[{"v":"1","c":["1",...]}]</c>).</summary>
    private static string ListJson(params (int Volume, string[] Chapters)[] volumes) =>
        "[" + string.Join(",", volumes.Select(v => $"{{\"v\":\"{v.Volume}\",\"c\":[{string.Join(",", v.Chapters.Select(c => $"\"{c}\""))}]}}")) + "]";

    private static string[] Range(int from, int to, params string[] more) =>
        Enumerable.Range(from, to - from + 1).Select(i => i.ToString(System.Globalization.CultureInfo.InvariantCulture)).Concat(more).ToArray();

    /// <summary>The folder with the given chapter files, scanned and analysed; linked (Confirmed) with a stored MangaDex list when given.</summary>
    private async Task<Dictionary<string, long>> SeedAsync(IEnumerable<string> files, string? listJson, SeriesLinkState link = SeriesLinkState.Confirmed,
        string folder = Folder)
    {
        await _h.InitAsync();
        _lib = await _h.AddLibraryAsync("manga");
        var names = files.ToList();
        foreach (var name in names)
            _h.WriteArchive(_lib, $"{folder}/{name}", _seed++);
        await _h.ScanAsync(_lib);
        await _h.AnalyseLibraryAsync(_lib);
        var folderNode = await _h.NodeAsync(_lib, folder);
        var record = await _h.AddRecordAsync("500", "Synthetic Saga");
        await _h.LinkAsync(folderNode.Id, link, link is SeriesLinkState.DontMatch or SeriesLinkState.ArtistFolder ? null : record);
        if (listJson is not null)
        {
            await using var db = _h.NewContext();
            db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = record,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                VolumesJson = listJson,
                ChaptersPerVolume = 10,
                KnownVolumeCount = 3,
                ContentHash = "h1",
                Version = 1,
                FetchedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var ids = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var name in names)
            ids[name] = (await _h.NodeAsync(_lib, $"{folder}/{name}")).Id;
        return ids;
    }

    /// <summary>MangaList's upgrade: the chapter files go, the volume file comes; one scan sees both; the volume is analysed.</summary>
    private async Task<long> UpgradeAsync(IEnumerable<string> removed, bool analyse = true, string folder = Folder)
    {
        foreach (var name in removed)
            File.Delete(_h.PathOf(_lib, $"{folder}/{name}"));
        _h.WriteArchive(_lib, $"{folder}/{VolumeName}", 9999);
        await _h.ScanAsync(_lib);
        var volume = await _h.NodeAsync(_lib, $"{folder}/{VolumeName}");
        if (analyse)
            await _h.AnalyseAsync(volume.Id, VolumePages);
        return volume.Id;
    }

    private async Task<ReadMarkEntity?> MarkAsync(long userId, long itemId)
    {
        await using var db = _h.NewContext();
        return await db.ReadMarks.AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.ItemId == itemId);
    }

    private Task<int> StateRowsAsync(long itemId) =>
        _h.CountAsync(async db => await db.ReadMarks.CountAsync(m => m.ItemId == itemId) + await db.ReadingProgress.CountAsync(p => p.ItemId == itemId));

    private async Task AssertReadAsync(long userId, long volume)
    {
        var mark = await MarkAsync(userId, volume);
        Assert.NotNull(mark);
        Assert.Equal("upgrade", mark!.Source);
        var progress = await _h.ProgressAsync(userId, volume);
        Assert.NotNull(progress);
        Assert.Equal(2, progress!.State);
        Assert.Equal(VolumePages - 1, progress.Ordinal);
    }

    private async Task AssertInProgressAtFirstPageAsync(long userId, long volume)
    {
        Assert.Null(await MarkAsync(userId, volume));
        var progress = await _h.ProgressAsync(userId, volume);
        Assert.NotNull(progress);
        Assert.Equal(1, progress!.State);
        Assert.Equal(0, progress.Ordinal);
    }

    private async Task AssertNothingAsync(long userId, long volume)
    {
        Assert.Null(await MarkAsync(userId, volume));
        Assert.Null(await _h.ProgressAsync(userId, volume));
    }

    // (a) The owner's case: chapters 1-9 + 9.5 listed, all ten replaced by volume 1.
    private static readonly string[] CaseAFiles = [.. Enumerable.Range(1, 9).Select(i => Chapter(Pad(i))), Chapter("009.5")];
    private static string CaseAList => ListJson((1, Range(1, 9, "9.5")), (2, Range(10, 18)));

    [Fact]
    public async Task CaseA_EveryListedChapterRead_TheVolumeIsRead_ExtrasOptional_AndASecondPassChangesNothing()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList);
        var owner = await _h.AddUserAsync("owner", admin: true);
        var noExtra = await _h.AddUserAsync("noextra");
        var idle = await _h.AddUserAsync("idle");
        var opened = await _h.AddUserAsync("opened");
        foreach (var (name, id) in ids)
        {
            // Read = a read mark OR completed progress.
            if (name.Contains("005", StringComparison.Ordinal))
                await _h.SetProgressAsync(owner, id, 3, state: 2);
            else
                await _h.MarkReadAsync(owner, id);
            if (!name.Contains("009.5", StringComparison.Ordinal))
                await _h.MarkReadAsync(noExtra, id);
        }
        // In progress on a chapter is not "read": nothing for a user who finished none.
        await _h.SetProgressAsync(idle, ids[Chapter("001")], 1, state: 1);

        var volume = await UpgradeAsync(ids.Keys);
        // Opened the volume at page 1 before the pass (what the reader saves on close): every chapter read -> raised to Read.
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(opened, id);
        await _h.SetProgressAsync(opened, volume, 0, state: 1);

        await _h.PairAsync();

        await AssertReadAsync(owner, volume);
        await AssertReadAsync(noExtra, volume);
        await AssertReadAsync(opened, volume);
        await AssertNothingAsync(idle, volume);
        // The tombstones keep their own rows (the trash removes them as before).
        Assert.NotNull(await MarkAsync(owner, ids[Chapter("001")]));

        var before = await _h.ProgressAsync(owner, volume);
        await _h.PairAsync();
        var after = await _h.ProgressAsync(owner, volume);
        Assert.Equal(before!.Revision, after!.Revision);
        Assert.Equal(1, await _h.CountAsync(db => db.ReadMarks.CountAsync(m => m.UserId == owner && m.ItemId == volume)));
    }

    // (b) 12 listed units (3, 4.1 and 4.2 never present) - only 9 files existed.
    private static readonly string[] CaseBFiles = [Chapter("001"), Chapter("002"), .. Enumerable.Range(5, 7).Select(i => Chapter(Pad(i)))];
    private static string CaseBList => ListJson((1, ["1", "2", "3", "4.1", "4.2", "5", "6", "7", "8", "9", "10", "11"]), (2, Range(12, 20)));

    [Fact]
    public async Task CaseB_SomeListedChaptersRead_TheVolumeIsInProgressAtPage1_NeverRead()
    {
        var ids = await SeedAsync(CaseBFiles, CaseBList);
        var some = await _h.AddUserAsync("some");
        var all = await _h.AddUserAsync("allpresent");
        await _h.MarkReadAsync(some, ids[Chapter("001")]);
        await _h.SetProgressAsync(some, ids[Chapter("002")], 3, state: 2);
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(all, id);

        var volume = await UpgradeAsync(ids.Keys);
        await _h.PairAsync();

        await AssertInProgressAtFirstPageAsync(some, volume);
        // Every file present was read, but 3, 4.1 and 4.2 never were: in progress, not read.
        await AssertInProgressAtFirstPageAsync(all, volume);

        var before = await _h.ProgressAsync(some, volume);
        await _h.PairAsync();
        Assert.Equal(before!.Revision, (await _h.ProgressAsync(some, volume))!.Revision);
    }

    // (c) Two files per chapter 1-5 and one of 6, list 1-6.
    private static readonly string[] CaseCFiles =
        [.. Enumerable.Range(1, 5).SelectMany(i => new[] { $"Synthetic Saga c{i:000} [GroupA].cbz", $"Synthetic Saga c{i:000} [GroupB].cbz" }), Chapter("006")];

    [Fact]
    public async Task CaseC_DuplicatedChapterFiles_AChapterIsReadWhenAnyOfItsFilesWas()
    {
        var ids = await SeedAsync(CaseCFiles, ListJson((1, Range(1, 6)), (2, Range(7, 12))));
        var reader = await _h.AddUserAsync("reader");
        foreach (var (name, id) in ids)
        {
            // One copy of each chapter: A for odd chapters, B for even ones, and the single 6.
            var odd = name.Contains("001", StringComparison.Ordinal) || name.Contains("003", StringComparison.Ordinal) || name.Contains("005", StringComparison.Ordinal);
            if ((odd && name.Contains("GroupA", StringComparison.Ordinal)) || (!odd && !name.Contains("GroupA", StringComparison.Ordinal)))
                await _h.MarkReadAsync(reader, id);
        }

        var volume = await UpgradeAsync(ids.Keys);
        await _h.PairAsync();

        await AssertReadAsync(reader, volume);
    }

    [Fact]
    public async Task NoStoredVolumeList_NothingIsCarried()
    {
        var ids = await SeedAsync(CaseAFiles, listJson: null);
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);

        var volume = await UpgradeAsync(ids.Keys);
        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(volume));
    }

    [Fact]
    public async Task AFolderMarkedDontMatch_NothingIsCarried()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList, link: SeriesLinkState.DontMatch);
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);

        var volume = await UpgradeAsync(ids.Keys);
        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(volume));
    }

    [Fact]
    public async Task ChapterNumbersOutsideTheVolumesList_AreNotEvidence()
    {
        // Volume 1 lists 1-5; chapters 6-10 (volume 2's) were replaced too.
        var files = Enumerable.Range(1, 10).Select(i => Chapter(Pad(i))).ToList();
        var ids = await SeedAsync(files, ListJson((1, Range(1, 5)), (2, Range(6, 10))));
        var later = await _h.AddUserAsync("later");
        var everyone = await _h.AddUserAsync("everyone");
        foreach (var name in files.Skip(5))
            await _h.MarkReadAsync(later, ids[name]);
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(everyone, id);

        var volume = await UpgradeAsync(ids.Keys);
        await _h.PairAsync();

        await AssertNothingAsync(later, volume);
        await AssertReadAsync(everyone, volume);
    }

    [Fact]
    public async Task ExistingStateOnTheVolume_IsNeverOverwrittenOrLowered()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList);
        var marked = await _h.AddUserAsync("marked");
        var reading = await _h.AddUserAsync("reading");
        var finished = await _h.AddUserAsync("finished");
        var partial = await _h.AddUserAsync("partial");
        foreach (var user in new[] { marked, reading, finished })
            foreach (var id in ids.Values)
                await _h.MarkReadAsync(user, id);
        await _h.MarkReadAsync(partial, ids[Chapter("001")]);

        var volume = await UpgradeAsync(ids.Keys);
        await _h.MarkReadAsync(marked, volume);                 // a manual mark, no progress
        await _h.SetProgressAsync(reading, volume, 3, state: 1); // past page 1
        await _h.SetProgressAsync(finished, volume, 2, state: 2); // completed (re-read flip)
        await _h.SetProgressAsync(partial, volume, 5, state: 1); // further than page 1

        await _h.PairAsync();

        var mark = await MarkAsync(marked, volume);
        Assert.Equal("manual", mark!.Source);
        Assert.Null(await _h.ProgressAsync(marked, volume));
        Assert.Null(await MarkAsync(reading, volume));
        Assert.Equal((1, 3), ((await _h.ProgressAsync(reading, volume))!.State, (await _h.ProgressAsync(reading, volume))!.Ordinal));
        Assert.Null(await MarkAsync(finished, volume));
        Assert.Equal((2, 2), ((await _h.ProgressAsync(finished, volume))!.State, (await _h.ProgressAsync(finished, volume))!.Ordinal));
        Assert.Equal((1, 5), ((await _h.ProgressAsync(partial, volume))!.State, (await _h.ProgressAsync(partial, volume))!.Ordinal));
    }

    [Fact]
    public async Task ARealIdenticalContentMove_IsHandledByMovePairingOnly()
    {
        // Chapter 1 moves to another library (byte-identical); chapters 2-3 are replaced with volume 1 (list 1-3).
        var files = new[] { Chapter("001"), Chapter("002"), Chapter("003") };
        var ids = await SeedAsync(files, ListJson((1, Range(1, 3)), (2, Range(4, 6))));
        var other = await _h.AddLibraryAsync("elsewhere");
        await _h.ScanAsync(other);
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);

        _h.Move(_lib, $"{Folder}/{Chapter("001")}", other, $"Moved/{Chapter("001")}");
        await _h.ScanAsync(other);
        var moved = await _h.NodeAsync(other, $"Moved/{Chapter("001")}");
        await _h.AnalyseAsync(moved.Id);
        var volume = await UpgradeAsync(files.Skip(1));

        await _h.PairAsync();

        // The move took chapter 1 (its mark followed the identical copy); the volume only saw chapters 2 and 3 read.
        Assert.NotNull(await MarkAsync(reader, moved.Id));
        Assert.True(await _h.CountAsync(db => db.NodeMoves.CountAsync(m => m.FromNodeId == ids[Chapter("001")] && m.ToNodeId == moved.Id)) == 1);
        await AssertInProgressAtFirstPageAsync(reader, volume);
    }

    [Fact]
    public async Task AUserWhoReadNothing_GetsNothing()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList);
        var reader = await _h.AddUserAsync("reader");
        var bystander = await _h.AddUserAsync("bystander");
        await _h.MarkReadAsync(reader, ids[Chapter("002")]);

        var volume = await UpgradeAsync(ids.Keys);
        await _h.PairAsync();

        await AssertInProgressAtFirstPageAsync(reader, volume);
        await AssertNothingAsync(bystander, volume);
    }

    [Fact]
    public async Task AVolumeWaitingForAnalysis_IsCarriedOnceItIsAnalysed()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList);
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);

        var volume = await UpgradeAsync(ids.Keys, analyse: false);
        await _h.PairAsync();
        Assert.Equal(0, await StateRowsAsync(volume));

        await _h.AnalyseAsync(volume, VolumePages);
        await _h.PairAsync();
        await AssertReadAsync(reader, volume);
    }

    [Fact]
    public async Task AVolumeTheCatalogSawNextToTheChapters_IsNotAnUpgrade()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList);
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);
        // The volume arrives first and is scanned next to the chapters; the chapters are deleted only later.
        _h.WriteArchive(_lib, $"{Folder}/{VolumeName}", 9999);
        await _h.ScanAsync(_lib);
        var volume = (await _h.NodeAsync(_lib, $"{Folder}/{VolumeName}")).Id;
        await _h.AnalyseAsync(volume, VolumePages);
        foreach (var name in ids.Keys)
            File.Delete(_h.PathOf(_lib, $"{Folder}/{name}"));
        await _h.ScanAsync(_lib);

        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(volume));
    }

    [Fact]
    public async Task ChaptersTombstonedBeforeTheMoveWindow_AreNotEvidence()
    {
        var ids = await SeedAsync(CaseAFiles, CaseAList);
        var reader = await _h.AddUserAsync("reader");
        foreach (var id in ids.Values)
            await _h.MarkReadAsync(reader, id);
        var volume = await UpgradeAsync(ids.Keys);
        foreach (var id in ids.Values)
            await _h.SetTombstonedAtAsync(id, DateTimeOffset.UtcNow.AddDays(-31));

        await _h.PairAsync();

        Assert.Equal(0, await StateRowsAsync(volume));
    }
}
