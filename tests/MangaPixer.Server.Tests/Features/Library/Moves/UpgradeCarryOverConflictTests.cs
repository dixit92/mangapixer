namespace com.lifepixer.mangapixer.Tests.Server.Features.Library.Moves;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the 1.41.0 overlap rule (owner decision of 2026-10-11, census shape g): two live files of one folder that hold
/// a volume in common (<c>v01-03</c> next to <c>v03</c> or <c>v03-05</c>) are a conflict - neither takes any read state, and the pass
/// counts it. Synthetic names and bytes.
/// </summary>
public sealed class UpgradeCarryOverConflictTests : IDisposable
{
    private const string Folder = "Synthetic Saga";
    private const int Pages = 8;

    private readonly MoveTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData("Synthetic Saga v03.cbz")]
    [InlineData("Synthetic Saga v03-05.cbz")]
    public async Task CaseG_ARangeFileNextToALiveFileOfOneOfItsVolumes_IsAConflict_NothingIsCarried(string other)
    {
        await _h.InitAsync();
        var lib = await _h.AddLibraryAsync("manga");
        var chapters = Enumerable.Range(1, 18).Select(i => $"Synthetic Saga c{i:000}.cbz").ToList();
        var seed = 100;
        foreach (var name in chapters.Append(other))
            _h.WriteArchive(lib, $"{Folder}/{name}", seed++);
        await _h.ScanAsync(lib);
        await _h.AnalyseLibraryAsync(lib, Pages);
        var folder = await _h.NodeAsync(lib, Folder);
        var record = await _h.AddRecordAsync("500", "Synthetic Saga");
        await _h.LinkAsync(folder.Id, SeriesLinkState.Confirmed, record);
        await using (var db = _h.NewContext())
        {
            var list = string.Join(",", Enumerable.Range(1, 5).Select(v =>
                $"{{\"v\":\"{v}\",\"c\":[{string.Join(",", Enumerable.Range((v * 9) - 8, 9).Select(c => $"\"{c}\""))}]}}"));
            db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
            {
                RecordId = record,
                Source = (int)VolumeMapSource.MangaDexAggregate,
                State = (int)VolumeMapState.Ok,
                VolumesJson = "[" + list + "]",
                ChaptersPerVolume = 9,
                KnownVolumeCount = 5,
                ContentHash = "h1",
                Version = 1,
                FetchedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }
        var reader = await _h.AddUserAsync("reader");
        foreach (var name in chapters)
            await _h.MarkReadAsync(reader, (await _h.NodeAsync(lib, $"{Folder}/{name}")).Id);

        foreach (var name in chapters)
            File.Delete(_h.PathOf(lib, $"{Folder}/{name}"));
        const string range = "Synthetic Saga v01-03.cbz";
        _h.WriteArchive(lib, $"{Folder}/{range}", 9999);
        await _h.ScanAsync(lib);
        var rangeId = (await _h.NodeAsync(lib, $"{Folder}/{range}")).Id;
        await _h.AnalyseAsync(rangeId, Pages);
        var otherId = (await _h.NodeAsync(lib, $"{Folder}/{other}")).Id;

        var pass = await _h.PairAsync();

        Assert.Equal(1, pass.Upgrades.Conflicts);
        Assert.Equal(0, pass.Upgrades.MarkedRead + pass.Upgrades.SetInProgress);
        await using var check = _h.NewContext();
        Assert.False(await check.ReadMarks.AnyAsync(m => m.ItemId == rangeId || m.ItemId == otherId));
        Assert.False(await check.ReadingProgress.AnyAsync(p => p.ItemId == rangeId || p.ItemId == otherId));
    }
}
