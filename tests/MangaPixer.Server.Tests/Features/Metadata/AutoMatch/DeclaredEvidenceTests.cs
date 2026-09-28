namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Declared;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of declared facts as matching evidence (1.28.0) through the production path: a
/// <c>declared_facts</c> row -> <see cref="DeclaredFactsReader"/> -> <c>MetadataAutoMatchService.ProcessAsync</c> -> the real
/// planner and scorer. Two records share a title and differ only by their author disambiguator: without a declaration
/// the work waits in review; with the library's declared author it links the right record automatically. Synthetic.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class DeclaredEvidenceTests : IAsyncLifetime
{
    private const string Title = "Qzv Harbor Tale";
    private MetadataTestDb _db = null!;
    private AutoMatchHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new AutoMatchHarness(_db);
        await _h.EnableAutomaticAsync();
        _h.Search[Title] = [new MuJson.Hit(801, $"{Title} (ALPHA Writer)"), new MuJson.Hit(802, $"{Title} (BETA Painter)")];
        _h.Records[801] = MuJson.Get(801, $"{Title} (ALPHA Writer)");
        _h.Records[802] = MuJson.Get(802, $"{Title} (BETA Painter)");
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private async Task<CatalogNodeEntity> FolderAsync()
    {
        var folder = await _db.AddFolderAsync(null, Title);
        for (var i = 1; i <= 3; i++)
            await _db.AddArchiveAsync(folder, $"{Title} v{i:D2}");
        return folder;
    }

    private async Task DeclareAsync(long? nodeId, string key, string value)
    {
        _db.Db.DeclaredFacts.Add(new DeclaredFactEntity
        {
            LibraryId = _db.LibraryId,
            NodeId = nodeId,
            Key = key,
            Value = value,
            Position = 0,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await _db.Db.SaveChangesAsync();
    }

    private async Task<NodeSeriesLinkEntity> MatchAsync(CatalogNodeEntity folder, MetadataAutoMatchOptions? options = null)
    {
        var service = _h.ServiceWithRealMatcher(new DeclaredFactsReader(_db.Db), options);
        await service.StartBulkAsync(_db.LibraryPublicId, new Core.Api.MetadataMatchLibraryRequest(), "admin");
        var row = await service.LeaseNextAsync("test");
        await _h.ServiceWithRealMatcher(new DeclaredFactsReader(_db.Db), options).ProcessAsync(row!);
        _db.Db.ChangeTracker.Clear();
        return await _db.Db.NodeSeriesLinks.AsNoTracking().Include(l => l.Record).SingleAsync(l => l.NodeId == folder.Id);
    }

    [Fact]
    public async Task WithoutADeclaration_TheSameTitledRecordsWaitInReview()
    {
        var link = await MatchAsync(await FolderAsync());

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
    }

    [Fact]
    public async Task TheLibrarysDeclaredAuthor_LinksTheRecordByThatAuthor()
    {
        var folder = await FolderAsync();
        await DeclareAsync(null, DeclaredFactKeys.Creator, "Beta Painter");

        var link = await MatchAsync(folder);

        Assert.Equal((int)SeriesLinkState.Auto, link.State);
        Assert.Equal("802", link.Record!.ExternalId);
        Assert.Equal(0, _h.Handler.Seen.Count(r => r.Body?.Contains("Beta", StringComparison.OrdinalIgnoreCase) == true)); // never sent
    }

    private IReadOnlyList<string> SentFilterTypes() =>
        _h.Handler.Seen.Where(r => r.Method == HttpMethod.Post).Select(r => System.Text.Json.JsonDocument.Parse(r.Body!).RootElement)
            .SelectMany(b => b.GetProperty("filter_types").EnumerateArray().Select(t => t.GetString()!)).Distinct().Order(StringComparer.Ordinal).ToList();

    [Fact]
    public async Task ADeclaredType_IsNotSentAsASearchFilter_UnlessTheOwnerGatedSwitchIsOn()
    {
        var folder = await FolderAsync();
        await DeclareAsync(folder.Id, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa));

        await MatchAsync(folder);

        Assert.Equal(["Artbook", "Doujinshi", "Drama CD", "Novel"], SentFilterTypes()); // the fixed filter only
    }

    [Fact]
    public async Task WithTheSwitchOn_ADeclaredManhwa_LeavesTheOtherTwoOriginsOutOfAutomaticSearches()
    {
        var folder = await FolderAsync();
        await DeclareAsync(folder.Id, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Manhwa));

        await MatchAsync(folder, new MetadataAutoMatchOptions { DeclaredTypeFilter = true });

        Assert.Equal(["Artbook", "Doujinshi", "Drama CD", "Manga", "Manhua", "Novel"], SentFilterTypes());
    }

    [Fact]
    public async Task WithTheSwitchOn_ADeclaredWebtoon_AddsNothing()
    {
        var folder = await FolderAsync();
        await DeclareAsync(null, DeclaredFactKeys.Type, DeclaredFactKeys.TypeSlug(DeclaredType.Webtoon));

        await MatchAsync(folder, new MetadataAutoMatchOptions { DeclaredTypeFilter = true });

        Assert.Equal(["Artbook", "Doujinshi", "Drama CD", "Novel"], SentFilterTypes());
    }

    [Fact]
    public async Task ADeclarationOnAnotherFolder_DoesNotApply()
    {
        var folder = await FolderAsync();
        var other = await _db.AddFolderAsync(null, "Qzv Unrelated Shelf");
        await DeclareAsync(other.Id, DeclaredFactKeys.Creator, "Beta Painter");

        var link = await MatchAsync(folder);

        Assert.Equal((int)SeriesLinkState.NeedsReview, link.State);
    }
}
