namespace com.lifepixer.mangapixer.Tests.Server.Features.Export;

using System.Text.Json;
using System.Text.Json.Nodes;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Metadata.Volumes;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// The synthetic sample export page for client authors (1.33.0, asked for by MangaList): one incremental page with every link state,
/// a folder and an archive item, a removal, a <c>carriedFrom</c>, a volume list with dates and ISBNs, companions and official links -
/// fixed clock, fixed public ids, synthetic names and example.com URLs. Compared with <c>contracts/samples/metadata-export-v1.json</c>
/// like the OpenAPI snapshot; <c>MANGAPIXER_UPDATE_SNAPSHOT=1</c> rewrites it.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class ExportSampleSnapshotTests
{
    private static readonly string SamplePath = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "contracts", "samples", "metadata-export-v1.json");

    private static readonly DateTimeOffset T0 = ExportTestKit.Start;

    [Fact]
    public async Task SamplePage_MatchesTheCommittedSample()
    {
        var json = await BuildSampleAsync();

        if (Environment.GetEnvironmentVariable("MANGAPIXER_UPDATE_SNAPSHOT") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SamplePath)!);
            await File.WriteAllTextAsync(SamplePath, json);
            Assert.Fail($"Sample updated at {SamplePath}. Re-run without MANGAPIXER_UPDATE_SNAPSHOT to verify.");
        }
        Assert.True(File.Exists(SamplePath), $"Sample not found at {SamplePath}");
        Assert.Equal(Format(await File.ReadAllTextAsync(SamplePath)), json);
    }

    private static string Format(string json) =>
        JsonNode.Parse(json)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }).ReplaceLineEndings("\n") + "\n";

    private static async Task<string> BuildSampleAsync()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var db = kit.Db.Db;
        var lib = await kit.Db.AddLibraryAsync("lib0manga", "Manga");
        await db.Libraries.Where(l => l.Id == lib.Id).ExecuteUpdateAsync(u => u.SetProperty(l => l.LastScanCompleted, T0.AddHours(-1)));
        db.DeclaredFacts.Add(new DeclaredFactEntity { LibraryId = lib.Id, Key = DeclaredFactKeys.Type, Value = "manga", CreatedAt = T0, UpdatedAt = T0 });
        await db.SaveChangesAsync();

        var shonen = await kit.Db.AddFolderAsync(null, "Shonen", lib.Id);
        var quest = await kit.Db.AddFolderAsync(shonen, "Synthetic Quest");
        foreach (var v in new[] { "01", "02", "03" })
            await kit.Db.AddArchiveAsync(quest, $"Synthetic Quest v{v}.cbz");
        await kit.Db.AddArchiveAsync(quest, "Synthetic Quest c025.cbz");
        var oldName = await kit.Db.AddFolderAsync(shonen, "Synthetic Saga");
        await kit.Db.AddArchiveAsync(oldName, "Synthetic Saga v01.cbz");
        var doujin = await kit.Db.AddFolderAsync(null, "Doujin", lib.Id);
        var oneShot = await kit.Db.AddArchiveAsync(doujin, "Synthetic Circle - Short Story.zip");
        // 1.34.0: a folder of works about Synthetic Quest (fan works) - its record is a label only.
        var fanWorks = await kit.Db.AddFolderAsync(doujin, "Synthetic Quest Fan Works");
        await kit.Db.AddArchiveAsync(fanWorks, "[Sample Circle] Side Story.cbz");
        var misc = await kit.Db.AddFolderAsync(null, "Misc", lib.Id);
        var gone = await kit.Db.AddFolderAsync(null, "Synthetic Gone", lib.Id);

        var questRecord = Record("10000000001", "Synthetic Quest", MetadataOriginStatus.Ongoing, 5, 41,
            "[{\"name\":\"Synthetic Press\",\"kind\":\"english\",\"volumes\":3,\"status\":\"ongoing\"},{\"name\":\"Synthetic Original\",\"kind\":\"original\"}]",
            "5 Volumes (Ongoing)", licensed: true, refreshDays: 14);
        questRecord.AltTitlesJson = "[\"Synthetic Quest: The Journey\",\"Shinsetsu Kuesuto\"]";
        var sagaRecord = Record("10000000002", "Synthetic Saga", MetadataOriginStatus.Complete, 2, 12, null, "2 Volumes (Complete)", licensed: false, refreshDays: null);
        var mangadex = new MetadataRecordEntity
        {
            PublicId = "mrsamplemd",
            Provider = "mangadex",
            ExternalId = "00000000-0000-4000-8000-000000000001",
            Title = "Synthetic Quest",
            ExtraJson = JsonSerializer.Serialize(new CompanionLinkService.MangaDexExtra("ja", null, null)
            {
                Links =
                [
                    new("raw", "https://publisher.example.com/synthetic-quest"),
                    new("engtl", "https://english.example.com/titles/synthetic-quest"),
                    new("bw", "https://bookwalker.jp/series/100001/list"),
                ],
            }),
            FetchedAt = T0.AddDays(-2),
        };
        var aniList = new MetadataRecordEntity
        {
            PublicId = "mrsampleal",
            Provider = "anilist",
            ExternalId = "100001",
            Title = "Synthetic Quest",
            LatestChapter = 41,
            OriginVolumes = 5,
            CrossIdsJson = "{\"mangaupdates\":\"10000000001\"}",
            FetchedAt = T0.AddDays(-2),
        };
        db.MetadataRecords.AddRange(questRecord, sagaRecord, mangadex, aniList);
        await db.SaveChangesAsync();
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = questRecord.Id, Provider = "mangadex", CompanionRecordId = mangadex.Id, State = 0 });
        db.MetadataCompanions.Add(new MetadataCompanionEntity { RecordId = questRecord.Id, Provider = "anilist", CompanionRecordId = aniList.Id, State = 0 });
        db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = questRecord.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\",\"3\",\"4\",\"5\",\"6\",\"7\",\"8\"]},{\"v\":\"2\",\"c\":[\"9\",\"10\",\"11\",\"12\",\"13\",\"14\",\"15\",\"16\"]},"
                + "{\"v\":\"3\",\"c\":[\"17\",\"18\",\"19\",\"20\",\"21\",\"22\",\"23\",\"24\",\"24.5\"]}]",
            ChaptersPerVolume = 8.3,
            KnownVolumeCount = 5,
            ContentHash = "sample",
            Version = 1,
            FetchedAt = T0.AddDays(-2),
        });
        db.WikipediaLists.Add(new WikipediaListEntity
        {
            RecordId = questRecord.Id,
            State = 0,
            DetailsJson = "[{\"v\":\"1\",\"d\":\"2025-03-04\",\"i\":\"9780000000011\"},{\"v\":\"2\",\"d\":\"2025-07-01\",\"i\":\"9780000000028\"},"
                + "{\"v\":\"3\",\"d\":\"2026-01\"},{\"v\":\"4\",\"d\":\"2027-02-09\"}]",
            CheckedAt = T0.AddDays(-1),
        });
        Link(db, quest, questRecord, SeriesLinkState.Confirmed, MetadataMatchMethod.Search, 0.97);
        Link(db, oldName, sagaRecord, SeriesLinkState.Auto, MetadataMatchMethod.Auto, 0.91);
        Link(db, oneShot, null, SeriesLinkState.NeedsReview, MetadataMatchMethod.Auto, null);
        Link(db, misc, null, SeriesLinkState.DontMatch, null, null);
        Link(db, fanWorks, questRecord, SeriesLinkState.CollectionAbout, MetadataMatchMethod.Search, null);
        Link(db, gone, null, SeriesLinkState.DontMatch, null, null);
        await db.SaveChangesAsync();

        await kit.RebuildAsync(lib.Id);

        // A day later: the saga's folder is renamed (carry-over moves its link) and "Synthetic Gone" loses its link.
        kit.Clock.Advance(TimeSpan.FromDays(1));
        var newName = await kit.Db.AddFolderAsync(shonen, "Synthetic Saga (Complete)");
        await kit.Db.AddArchiveAsync(newName, "Synthetic Saga v01.cbz");
        await kit.TombstoneAsync(oldName);
        await kit.CarryOver().MoveRowsAsync(oldName.Id, newName.Id, default);
        await db.NodeSeriesLinks.Where(l => l.NodeId == gone.Id).ExecuteDeleteAsync();
        // 1.37.0: a folder marked as one artist's works - no record; its works are items of their own (created last: earlier ids stay).
        var artistFolder = await kit.Db.AddFolderAsync(doujin, "Sample Artist");
        await kit.Db.AddArchiveAsync(artistFolder, "Sample Artist - Night Story.cbz");
        Link(db, artistFolder, null, SeriesLinkState.ArtistFolder, null, null);
        await db.SaveChangesAsync();

        var answer = await kit.Export().PageAsync(lib.PublicId, ExportJson.Format(T0), null, null, null);
        Assert.True(answer.Json is not null, answer.Error);
        return Format(answer.Json!);
    }

    private static MetadataRecordEntity Record(
        string externalId, string title, MetadataOriginStatus status, int volumes, double latest, string? publishers, string statusText,
        bool licensed, int? refreshDays)
    {
        return new MetadataRecordEntity
        {
            PublicId = "mr" + externalId,
            Provider = "mangaupdates",
            ExternalId = externalId,
            Title = title,
            Origin = (int)MetadataOrigin.Japan,
            ProviderType = "Manga",
            OriginStatus = (int)status,
            OriginVolumes = volumes,
            LatestChapter = latest,
            StatusText = statusText,
            LicensedEn = licensed,
            TranslationComplete = false,
            PublishersJson = publishers,
            SiteUrl = "https://www.mangaupdates.com/series/" + externalId + "/sample",
            FetchedAt = T0.AddDays(-3),
            RefreshCadenceDays = refreshDays,
        };
    }

    private static void Link(
        MangaPixerDbContext db, CatalogNodeEntity node, MetadataRecordEntity? record, SeriesLinkState state,
        MetadataMatchMethod? method, double? score) =>
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
        {
            NodeId = node.Id,
            LibraryId = node.LibraryId,
            State = (int)state,
            RecordId = record?.Id,
            MatchMethod = (int?)method,
            MatchScore = score,
            CreatedAt = T0.AddDays(-5),
            UpdatedAt = T0.AddDays(-5),
        });
}
