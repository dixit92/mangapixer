namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) of duplicate chapter numbers (1.31.0), through the three places the numbers are read: the
/// Volumes view (stack card and stack page), the Missing report (and the viewer's series line), and the review row. The owner's
/// shape: one chapter folder where chapters 1 and 2 each exist twice (the same chapter in two uploads). Stored rows only, synthetic names.
/// </summary>
public sealed class DuplicateNumbersHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const string LibPubId = "dnLib1";
    private const string SeriesPubId = "dnSeries";
    private const string CleanPubId = "dnClean";
    private const string ReviewPubId = "dnReview";
    private readonly MangaPixerWebApplicationFactory _factory;

    public DuplicateNumbersHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    // dnSeries (linked; volume 1 lists chapters 1-9): chapters 1 and 2 twice, 3 once, plus the split chapter 4.1 + 4.2 (NOT duplicates).
    // dnClean (linked, same shape without a repeat). dnReview (in review, with a Volumes subfolder holding volume 1 twice and a
    // Chapters subfolder with chapter 1 twice: two directories, one duplicate each).
    private async Task SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPubId))
            return;
        var lib = await VolumeTestData.AddLibraryAsync(db, LibPubId);
        var series = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "DN Series", SeriesPubId);
        foreach (var (name, id) in new[]
        {
            ("DN Series - Chapter 001", "dn1a"), ("DN Series - Chapter 001 [part 2]", "dn1b"), ("DN Series - Chapter 002", "dn2a"),
            ("DN Series - Chapter 002 [part 2]", "dn2b"), ("DN Series - Chapter 003", "dn3"), ("DN Series - c004.1", "dn41"), ("DN Series - c004.2", "dn42"),
        })
            await VolumeTestData.AddArchiveAsync(db, lib.Id, series.Id, name, id);
        var record = await VolumeTestData.AddRecordAsync(db);
        await VolumeTestData.LinkAsync(db, series, record.Id);
        await VolumeTestData.AddMapAsync(db, record.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (1, 1, 9));

        var clean = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "DN Clean", CleanPubId);
        await VolumeTestData.AddChaptersAsync(db, lib.Id, clean.Id, "DN Clean - Chapter ", 1, 3);
        var cleanRecord = await VolumeTestData.AddRecordAsync(db);
        await VolumeTestData.LinkAsync(db, clean, cleanRecord.Id);
        await VolumeTestData.AddMapAsync(db, cleanRecord.Id, 1, null, null, VolumeMapState.Ok, VolumeMapSource.MangaDexAggregate, (1, 1, 9));

        var review = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "DN Review", ReviewPubId);
        var volumes = await VolumeTestData.AddFolderAsync(db, lib.Id, review.Id, "Volumes", "dnVols");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, volumes.Id, "DN Review v01", "dnv1a");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, volumes.Id, "DN Review v01 [other scan]", "dnv1b");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, volumes.Id, "DN Review v02", "dnv2");
        var chapters = await VolumeTestData.AddFolderAsync(db, lib.Id, review.Id, "Chapters", "dnChs");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, chapters.Id, "DN Review - Chapter 001", "dnr1a");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, chapters.Id, "DN Review - Chapter 001 [copy]", "dnr1b");
        await VolumeTestData.AddArchiveAsync(db, lib.Id, chapters.Id, "DN Review - Chapter 002", "dnr2");
        var now = DateTimeOffset.UtcNow;
        db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity { NodeId = review.Id, LibraryId = lib.Id, State = (int)SeriesLinkState.NeedsReview, MatchMethod = 3, CreatedAt = now, UpdatedAt = now });
        db.MetadataMatchQueue.Add(new MetadataMatchQueueEntity
        {
            NodeId = review.Id, LibraryId = lib.Id, State = QueueState.Done, Level = (int)MatchLevel.Folder, WorkClass = (int)WorkClass.Series,
            Outcome = (int)MatchBand.NeedsReview, RulesRevision = MatcherRules.Revision, EnqueuedAt = now, CompletedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private async Task<HttpClient> AdminAsync()
    {
        await SeedAsync();
        return await _factory.LoginAsAdminWithChangedPasswordAsync();
    }

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    private static IReadOnlyList<(MissingUnitKind, string, int)> Found(IEnumerable<DuplicateUnitDto> d) => d.Select(x => (x.Kind, x.Number, x.Files)).ToList();

    [Fact]
    public async Task StackCard_CountsEachChapterOnce_AndNamesTheDuplicates()
    {
        var admin = await AdminAsync();

        var page = await OkAsync<PageResponse<CatalogNodeDto>>(await admin.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={SeriesPubId}"));

        var stack = Assert.Single(page.Items, n => n.Kind == CatalogNodeKind.VolumeStack).VolumeStack!;
        // Files: 1, 1, 2, 2, 3, 4.1, 4.2 = seven; distinct chapters: 1, 2, 3, 4.1, 4.2 = five (the split chapter is no duplicate).
        Assert.Equal(5, stack.PresentCount);
        Assert.Equal([(MissingUnitKind.Chapter, "1", 2), (MissingUnitKind.Chapter, "2", 2)], Found(stack.Duplicates!));
        Assert.Equal((9, 4), (stack.ChapterCount, stack.ChaptersPresent)); // 1, 2, 3 and the split chapter 4 (both parts here)
    }

    [Fact]
    public async Task StackPage_KeepsEveryFile_AndSaysWhichChaptersRepeat()
    {
        var admin = await AdminAsync();

        var stack = await OkAsync<VolumeStackDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/volumes/1"));

        Assert.Equal(5, stack.PresentCount);
        Assert.Equal([(MissingUnitKind.Chapter, "1", 2), (MissingUnitKind.Chapter, "2", 2)], Found(stack.Duplicates!));
        Assert.Equal(7, stack.Slots.Count(s => s.Kind == VolumeSlotKind.Item)); // one card per file
        Assert.Equal(2, stack.Slots.Count(s => s.Chapter == "1" && s.Kind == VolumeSlotKind.Item));
        Assert.Empty((await OkAsync<PageResponse<CatalogNodeDto>>(await admin.GetAsync($"/api/v1/libraries/{LibPubId}/browse?parentId={CleanPubId}")))
            .Items.Where(n => n.VolumeStack is { Duplicates.Count: > 0 }));
    }

    [Fact]
    public async Task MissingReport_ListsTheDuplicatesPerSeries_ForAdminsAndTheSeriesLine()
    {
        var admin = await AdminAsync();

        var row = await OkAsync<MissingSeriesDto>(await admin.GetAsync($"/api/v1/admin/metadata/missing/{SeriesPubId}"));
        Assert.Equal([(MissingUnitKind.Chapter, "1", 2), (MissingUnitKind.Chapter, "2", 2)], Found(row.Duplicates));
        Assert.Equal(2, row.DuplicateCount);

        var line = await OkAsync<MissingSeriesDto>(await admin.GetAsync($"/api/v1/nodes/{SeriesPubId}/missing")); // the viewer endpoint
        Assert.Equal(2, line.DuplicateCount);

        var clean = await OkAsync<MissingSeriesDto>(await admin.GetAsync($"/api/v1/admin/metadata/missing/{CleanPubId}"));
        Assert.Empty(clean.Duplicates);
        Assert.Equal(0, clean.DuplicateCount);
    }

    [Fact]
    public async Task ReviewRow_SaysHowManyNumbersAreDuplicated_PerDirectory()
    {
        var admin = await AdminAsync();

        var page = await OkAsync<MetadataReviewPageDto>(await admin.GetAsync("/api/v1/admin/metadata/review?tab=NeedsReview"));

        var item = Assert.Single(page.Items, i => i.NodeId == ReviewPubId);
        // Volume 1 twice in Volumes, chapter 1 twice in Chapters: one duplicate of each kind (each directory on its own).
        Assert.Equal((1, 1), (item.DuplicateChapters, item.DuplicateVolumes));
    }
}
