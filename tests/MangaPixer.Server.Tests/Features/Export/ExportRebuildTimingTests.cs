namespace com.lifepixer.mangapixer.Tests.Server.Features.Export;

using System.Diagnostics;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// Rebuild timing on a synthetic 10,000-item library (1.33.0 sizing: MangaList asked for 10k items). A measurement, not a gate: it runs
/// only with <c>MANGAPIXER_EXPORT_PERF=1</c> and prints its timings.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ExportRebuildTimingTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Rebuild_Of10kItems_Timing()
    {
        if (Environment.GetEnvironmentVariable("MANGAPIXER_EXPORT_PERF") != "1")
            return;
        await using var kit = await ExportTestKit.CreateAsync();
        var db = kit.Db.Db;
        const int Count = 10_000;
        var seed = Stopwatch.StartNew();
        var folders = Enumerable.Range(0, Count).Select(i => new CatalogNodeEntity
        {
            PublicId = $"pf{i:D5}",
            LibraryId = kit.Db.LibraryId,
            Kind = (int)CatalogNodeKind.Folder,
            DisplayName = $"Series {i:D5}",
            RelativePath = $"f{i}",
            PathKey = $"f{i}",
            SortKey = $"0Series {i:D5}",
            CreatedAt = ExportTestKit.Start,
        }).ToList();
        db.CatalogNodes.AddRange(folders);
        var records = Enumerable.Range(0, Count).Select(i => new MetadataRecordEntity
        {
            PublicId = $"pr{i:D5}",
            Provider = "mangaupdates",
            ExternalId = $"{900000 + i}",
            Title = $"Series {i:D5}",
            OriginStatus = (int)MetadataOriginStatus.Ongoing,
            OriginVolumes = 10,
            PublishersJson = "[{\"name\":\"Synthetic Press\",\"kind\":\"english\",\"volumes\":8}]",
            FetchedAt = ExportTestKit.Start,
        }).ToList();
        db.MetadataRecords.AddRange(records);
        await db.SaveChangesAsync();
        db.CatalogNodes.AddRange(folders.SelectMany(f => Enumerable.Range(1, 3).Select(v => new CatalogNodeEntity
        {
            PublicId = $"{f.PublicId}v{v}",
            LibraryId = kit.Db.LibraryId,
            ParentId = f.Id,
            Kind = (int)CatalogNodeKind.Archive,
            DisplayName = $"{f.DisplayName} v{v:D2}.cbz",
            RelativePath = $"{f.RelativePath}/v{v}",
            PathKey = $"{f.PathKey}/v{v}",
            SortKey = $"1v{v:D2}",
            CreatedAt = ExportTestKit.Start,
        })));
        db.NodeSeriesLinks.AddRange(folders.Zip(records).Select(p => new NodeSeriesLinkEntity
        {
            NodeId = p.First.Id,
            LibraryId = kit.Db.LibraryId,
            State = (int)SeriesLinkState.Confirmed,
            RecordId = p.Second.Id,
            CreatedAt = ExportTestKit.Start,
            UpdatedAt = ExportTestKit.Start,
        }));
        db.SeriesVolumeMaps.AddRange(records.Select(r => new SeriesVolumeMapEntity
        {
            RecordId = r.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = "[" + string.Join(",", Enumerable.Range(1, 10).Select(v =>
                $"{{\"v\":\"{v}\",\"c\":[{string.Join(",", Enumerable.Range(v * 8 - 7, 8).Select(c => $"\"{c}\""))}]}}")) + "]",
            ContentHash = "h",
            FetchedAt = ExportTestKit.Start,
        }));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        output.WriteLine($"seeded {Count} items in {seed.ElapsedMilliseconds} ms");

        var first = await kit.RebuildAsync();
        output.WriteLine($"first rebuild: {first.Items} items, {first.Written} written in {first.ElapsedMs} ms");
        kit.Clock.Advance(TimeSpan.FromMinutes(10));
        var second = await kit.RebuildAsync();
        output.WriteLine($"unchanged rebuild: {second.Written} written in {second.ElapsedMs} ms");
        var page = Stopwatch.StartNew();
        var json = await kit.Export().PageAsync(kit.Db.LibraryPublicId, null, null, 500, null);
        output.WriteLine($"500-item page (incl. a rebuild): {json.Json!.Length / 1024} KB in {page.ElapsedMilliseconds} ms");
        Assert.Equal(Count, first.Items);
        Assert.Equal(0, second.Written);
    }
}
