namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Declared;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Export;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests for the per-folder edition override and "Track completion: off" (1.39.0, owner 2026-10-09): a folder of a
/// Master / omnibus edition is counted against the volumes declared for it (1..N), not against the regular edition's volume list, and a
/// folder whose tracking is off gives no Completion / missing / upgrade answer (Missing report, export). The declared facts are seeded
/// as stored rows (keys <c>volumes</c>, <c>edition</c>, <c>tracking</c>), so the stored form is pinned too. Synthetic names only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class EditionOverrideTests
{
    private static MissingReportService Report(MetadataTestDb t) => new(t.Db, NullLogger<MissingReportService>.Instance);

    /// <summary>A finished series whose regular edition has 30 volumes of 10 chapters each (MangaDex list, English edition complete).</summary>
    private static async Task<MetadataRecordEntity> RegularThirtyAsync(MetadataTestDb t, string id)
    {
        var record = await t.AddRecordAsync(id, "Synthetic Regular " + id);
        record.OriginStatus = (int)MetadataOriginStatus.Complete;
        record.OriginVolumes = 30;
        record.StatusText = "30 Volumes (Complete)";
        record.PublishersJson = "[{\"name\":\"Synthetic Press\",\"kind\":\"english\",\"volumes\":30,\"status\":\"complete\"}]";
        var volumes = Enumerable.Range(1, 30).Select(v => new
        {
            v = v.ToString(System.Globalization.CultureInfo.InvariantCulture),
            c = Enumerable.Range((10 * (v - 1)) + 1, 10).Select(c => c.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray(),
        });
        t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = record.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = JsonSerializer.Serialize(volumes),
            ChaptersPerVolume = 10,
            KnownVolumeCount = 30,
            ContentHash = "h" + id,
            Version = 1,
            FetchedAt = DateTimeOffset.UtcNow,
        });
        await t.Db.SaveChangesAsync();
        return record;
    }

    private static async Task<CatalogNodeEntity> FolderOfVolumesAsync(MetadataTestDb t, string name, int volumes)
    {
        var folder = await t.AddFolderAsync(null, name);
        for (var v = 1; v <= volumes; v++)
            await t.AddArchiveAsync(folder, $"{name} v{v:00}.cbz");
        return folder;
    }

    private static async Task DeclareAsync(MetadataTestDb t, CatalogNodeEntity folder, params (string Key, string Value)[] facts)
    {
        foreach (var (key, value) in facts)
        {
            t.Db.DeclaredFacts.Add(new DeclaredFactEntity
            {
                LibraryId = folder.LibraryId,
                NodeId = folder.Id,
                Key = key,
                Value = value,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }
        await t.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task VolumeOverride_CountsTheEditionsVolumes_NotTheRegularList()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var record = await RegularThirtyAsync(t, "e1");
        // The same record twice: the regular folder (10 of 30 volumes) and a 10-volume omnibus edition declared as such.
        var regular = await FolderOfVolumesAsync(t, "Synthetic Regular", 10);
        await t.AddLinkAsync(regular, record);
        var omnibus = await FolderOfVolumesAsync(t, "Synthetic Omnibus", 10);
        await t.AddLinkAsync(omnibus, record);
        await DeclareAsync(t, omnibus, ("volumes", "10"), ("edition", "omnibus"));

        var plain = await Report(t).ForNodeAsync(regular.PublicId);
        var edition = await Report(t).ForNodeAsync(omnibus.PublicId);

        // Control: without the override the regular edition's 30 volumes apply (volumes 11-30 are missing).
        Assert.Equal((20, SeriesAnswer.FinishedMissing), (plain!.Progress!.MissingVolumes, plain.Progress.Answer));
        // With it: volumes 1..10 of the edition, all here - nothing missing, finished and held whole.
        Assert.Equal(0, edition!.Progress!.MissingVolumes);
        Assert.Equal((10, 0), (edition.Volumes!.Available, edition.Volumes.BehindBy));
        Assert.Equal(MissingVerdict.UpToDate, edition.Verdict);
        Assert.Equal(SeriesCompletion.CompleteCollection, edition.Progress.Completion);
        Assert.Equal(SeriesAnswer.HaveItAll, edition.Progress.Answer);
        Assert.Empty(edition.Progress.UpgradeVolumes);
    }

    [Fact]
    public async Task VolumeOverride_MissingVolumesAreTheEditionsOwn()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var record = await RegularThirtyAsync(t, "e2");
        var omnibus = await t.AddFolderAsync(null, "Synthetic Master");
        foreach (var v in new[] { 1, 2, 4 })
            await t.AddArchiveAsync(omnibus, $"Synthetic Master v{v:00}.cbz");
        await t.AddLinkAsync(omnibus, record);
        await DeclareAsync(t, omnibus, ("volumes", "6"), ("edition", "master"));

        var row = await Report(t).ForNodeAsync(omnibus.PublicId);

        // Volume 3 is a hole; 5 and 6 come after the highest volume here; the regular 30 never count.
        Assert.Equal(3, row!.Progress!.MissingVolumes);
        Assert.Equal((6, 2, 1), (row.Volumes!.Available, row.Volumes.BehindBy, row.Volumes.MissingCount));
        Assert.Equal(new[] { 3 }, row.Volumes.Missing);
        Assert.Equal(SeriesAnswer.FinishedMissing, row.Progress.Answer);
    }

    [Fact]
    public async Task TrackingOff_LeavesTheSeriesOutOfTheMissingReport()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var record = await RegularThirtyAsync(t, "e3");
        var tracked = await FolderOfVolumesAsync(t, "Synthetic Tracked", 3);
        await t.AddLinkAsync(tracked, record);
        var quiet = await FolderOfVolumesAsync(t, "Synthetic Quiet", 3);
        await t.AddLinkAsync(quiet, record);
        await DeclareAsync(t, quiet, ("tracking", "off"));

        var (_, page) = await Report(t).ListAsync(null, onlyMissing: false, cursor: null, limit: 50);

        Assert.Equal("Synthetic Tracked", Assert.Single(page!.Items).DisplayName);
        Assert.Equal(1, page.Summary.Series);
        Assert.Equal(1, page.Total);
    }

    [Fact]
    public async Task Export_CarriesTheOverride_AndTrackingOffDropsTheCompletionBlock()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var t = kit.Db;
        var record = await RegularThirtyAsync(t, "e4");
        var plain = await FolderOfVolumesAsync(t, "Synthetic Plain", 2);
        await t.AddLinkAsync(plain, record);
        var omnibus = await FolderOfVolumesAsync(t, "Synthetic Omnibus", 10);
        await t.AddLinkAsync(omnibus, record);
        await DeclareAsync(t, omnibus, ("volumes", "10"), ("edition", "omnibus"));
        var quiet = await FolderOfVolumesAsync(t, "Synthetic Quiet", 2);
        await t.AddLinkAsync(quiet, record);
        await DeclareAsync(t, quiet, ("tracking", "off"));

        await kit.RebuildAsync();
        var page = await kit.PageAsync();
        var items = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("nodeId").GetString()!);

        // No override, tracking on: the item keeps its form - none of the new keys.
        var p = items[plain.PublicId];
        Assert.False(p.TryGetProperty("tracking", out _));
        Assert.False(p.GetProperty("completion").TryGetProperty("volumeTotalOverride", out _));
        Assert.False(p.GetProperty("completion").TryGetProperty("edition", out _));

        var o = items[omnibus.PublicId];
        Assert.False(o.TryGetProperty("tracking", out _));
        Assert.Equal(10, o.GetProperty("completion").GetProperty("volumeTotalOverride").GetInt32());
        Assert.Equal("omnibus", o.GetProperty("completion").GetProperty("edition").GetString());
        Assert.Equal("HaveItAll", o.GetProperty("completion").GetProperty("answer").GetString());

        var q = items[quiet.PublicId];
        Assert.Equal("off", q.GetProperty("tracking").GetString());
        Assert.Equal(JsonValueKind.Null, q.GetProperty("completion").ValueKind);
        Assert.NotEqual(JsonValueKind.Null, q.GetProperty("record").ValueKind); // the link and the record stay
    }
}
