namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Net;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of the identify flow (1.24.0, lane B2): context (no
/// network), ranked search + candidate image tokens, look-up by pasted reference
/// (local parse, only the id sent), preview record reuse, link-with-fetch that
/// stores the record and poster, refresh (404 -> gone), and the image store
/// (atomic publish, magic-byte reject, deletion with the record).
/// </summary>
public sealed class MetadataIdentifyServiceTests : IAsyncLifetime
{
    private const string Mu = "mangaupdates";
    private static readonly string BerserkId = MuFixtures.BerserkId.ToString();
    private MetadataTestDb _db = null!;
    private GatewayHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new GatewayHarness(_db);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    private Task<MetadataRecordEntity> RecordAsync(string externalId) =>
        _db.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == Mu && r.ExternalId == externalId);

    // --- Context ---

    [Fact]
    public async Task Context_NoNetwork_SuggestionsHintAvailabilityAndLocalSignals()
    {
        var folder = await _db.AddFolderAsync(null, "[Grp] Dungeon Meshi (2014) [Delicious in Dungeon]");
        var a1 = await _db.AddArchiveAsync(folder, "v01.cbz");
        var a2 = await _db.AddArchiveAsync(folder, "v02.cbz");
        await _db.AddComicInfoAsync(a1, "Dungeon Meshi", web: ["https://www.mangaupdates.com/series/njeqwry/berserk"]);
        await _db.AddComicInfoAsync(a2, "Dungeon Meshi");
        for (var i = 0; i < 30; i++)
            _db.Db.PageEntries.Add(new PageEntryEntity { ItemId = a1.Id, ContentVersion = 1, Ordinal = i, EntryKey = "k" + i, SourceEntryLocator = "l" + i, MediaType = "image/jpeg", Width = 800, Height = 4000 });
        await _db.Db.SaveChangesAsync();

        var ctx = await _h.Identify().GetContextAsync(folder.PublicId);

        Assert.NotNull(ctx);
        Assert.False(ctx!.FetchAvailable);
        Assert.Equal("metadata_disabled", ctx.UnavailableCode);
        Assert.Equal(["Dungeon Meshi", "Delicious in Dungeon"], ctx.Suggestions);
        Assert.Equal(new IdentifyReferenceDto { Provider = Mu, ExternalId = BerserkId }, ctx.ComicInfoHint);
        Assert.Equal(2, ctx.Local.ItemCount);
        Assert.Equal("Dungeon Meshi", ctx.Local.ComicInfoSeries);
        Assert.True(ctx.Local.TallStrips);
        Assert.Equal(2014, ctx.Local.YearHint);
        Assert.Equal(5000, ctx.DailyBudget);
        Assert.Equal(0, _h.Handler.CallCount);

        await _h.EnableAsync();
        Assert.True((await _h.Identify().GetContextAsync(folder.PublicId))!.FetchAvailable);
        Assert.Null(await _h.Identify().GetContextAsync("missing"));
    }

    // --- Search ---

    [Fact]
    public async Task Search_RanksCandidates_AndServesCandidateImagesByToken()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Berserk");

        var result = await _h.Identify().SearchAsync(folder.PublicId, new IdentifySearchRequest { Query = "Berserk" });

        Assert.NotNull(result);
        var top = result!.Candidates[0];
        Assert.Equal(BerserkId, top.ExternalId);
        Assert.Equal(MatchStrength.Strong, top.Strength);
        Assert.Equal(MetadataOrigin.Japan, top.Origin);
        Assert.True(result.Candidates.Zip(result.Candidates.Skip(1)).All(p => p.First.Score >= p.Second.Score));
        Assert.Equal(1, result.BudgetUsedToday);
        Assert.NotNull(top.ImageToken);
        Assert.Equal(1, _h.Handler.CallCount);

        var image = await _h.Identify().GetCandidateImageAsync(top.ImageToken!);
        Assert.Equal("image/png", image!.Value.ContentType);
        await _h.Identify().GetCandidateImageAsync(top.ImageToken!); // held in memory
        Assert.Equal(2, _h.Handler.CallCount);
        Assert.Equal("https://cdn.mangaupdates.com/image/thumb/i501230.png", _h.Handler.Seen[1].Uri.AbsoluteUri);

        Assert.Null(await _h.Identify().GetCandidateImageAsync("unknown-token"));
        Assert.Equal(2, _h.Handler.CallCount);
    }

    // --- Look up ---

    [Fact]
    public async Task Lookup_ParsesLocally_SendsOnlyTheId()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Some Folder");

        var preview = await _h.Identify().LookupAsync(folder.PublicId, new IdentifyLookupRequest { Reference = "https://www.mangaupdates.com/series/njeqwry/berserk" });

        Assert.Equal("Berserk", preview!.Title);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal($"/v1/series/{BerserkId}", seen.Uri.AbsolutePath);
        Assert.DoesNotContain("berserk", seen.Uri.AbsoluteUri, StringComparison.OrdinalIgnoreCase);
        Assert.Null(seen.Body);
    }

    [Theory]
    [InlineData("https://www.mangaupdates.com/series.html?id=88", "legacy_url")]
    [InlineData("Berserk", "invalid_reference")]
    [InlineData("https://evil.example/series/njeqwry", "invalid_reference")]
    public async Task Lookup_BadReferences_Are400_WithoutAnyCall(string reference, string code)
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Some Folder");
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Identify().LookupAsync(folder.PublicId, new IdentifyLookupRequest { Reference = reference }));
        Assert.Equal(400, ex.HttpStatus);
        Assert.Equal(code, ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    // --- Preview ---

    [Fact]
    public async Task Preview_StoresRecord_AndReusesItWithin24Hours()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Berserk (1989)");
        var request = new IdentifyPreviewRequest { Provider = Mu, ExternalId = BerserkId };

        var first = await _h.Identify().PreviewAsync(folder.PublicId, request);
        Assert.Equal(MatchStrength.Strong, first!.Strength);
        Assert.Empty(first.Warnings);
        Assert.Equal(10, first.AltTitles.Count);
        Assert.NotNull(first.ImageToken);
        Assert.Equal(1, _h.Handler.CallCount);

        _h.Time.Advance(TimeSpan.FromHours(23));
        await _h.Identify().PreviewAsync(folder.PublicId, request);
        Assert.Equal(1, _h.Handler.CallCount);

        _h.Time.Advance(TimeSpan.FromHours(2));
        await _h.Identify().PreviewAsync(folder.PublicId, request);
        Assert.Equal(2, _h.Handler.CallCount);
        Assert.Single(await _db.Db.MetadataRecords.ToListAsync());
    }

    [Fact]
    public async Task Preview_WarnsOnNovel_Year_AndCount()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = _ => ScriptedHandler.Json(
            "{\"series_id\":13184758110,\"title\":\"Solo Leveling (Novel)\",\"type\":\"Novel\",\"year\":\"2016\",\"status\":\"2 Volumes (Complete)\"}");
        var folder = await _db.AddFolderAsync(null, "Solo Leveling (2018)");
        for (var i = 0; i < 10; i++)
            await _db.AddArchiveAsync(folder, $"c{i:D3}.cbz");

        var preview = await _h.Identify().PreviewAsync(folder.PublicId, new IdentifyPreviewRequest { Provider = Mu, ExternalId = "13184758110" });

        var codes = preview!.Warnings.Select(w => w.Code).ToList();
        Assert.Contains("format_novel", codes);
        Assert.Contains("year_mismatch", codes);
        Assert.Contains("count_mismatch", codes);
        Assert.Equal(MetadataFormat.Novel, preview.Format);
    }

    [Fact]
    public async Task Preview_UnknownRecord_Is404Gone()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "X");
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Identify().PreviewAsync(folder.PublicId, new IdentifyPreviewRequest { Provider = Mu, ExternalId = "999" }));
        Assert.Equal("record_gone", ex.Code);
        Assert.Empty(await _db.Db.MetadataRecords.ToListAsync());
    }

    // --- Link with fetch ---

    [Fact]
    public async Task Link_FetchesStoresRecordAndPoster_ResolverServesIt()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Berserk");
        var archive = await _db.AddArchiveAsync(folder, "v01.cbz");

        var (code, change) = await _h.Identify().LinkAsync(folder.PublicId,
            new LinkSeriesRequest { Provider = Mu, ExternalId = BerserkId, MatchMethod = MetadataMatchMethod.Search, MatchScore = 0.97 }, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(SeriesLinkState.Confirmed, change!.Link!.State);
        Assert.Equal(2, _h.Handler.CallCount); // one GET + the poster
        Assert.Equal("https://cdn.mangaupdates.com/image/i501230.png", _h.Handler.Seen[1].Uri.AbsoluteUri);

        var record = await RecordAsync(BerserkId);
        Assert.Equal("Berserk", record.Title);
        Assert.Equal(1, record.ImageState);
        Assert.Equal(1, record.ImageVersion);
        Assert.NotNull(record.CategoriesJson);
        Assert.Single(Directory.GetFiles(_h.ImageRoot), f => Path.GetFileName(f) == $"{record.Id}-1.png");

        var info = await _h.Resolver().ResolveAsync(archive);
        Assert.True(info.Web!.HasImage);
        Assert.Equal($"/api/v1/nodes/{archive.PublicId}/series-info/image?v={record.PublicId}-1", info.Web.ImageUrl);
        Assert.Equal("MangaUpdates", info.Web.ProviderName);
        Assert.Contains(await _db.Db.AuditEvents.Select(a => a.Action).ToListAsync(), a => a == "metadata.link");
    }

    [Fact]
    public async Task Link_StoredRecord_NeedsNoNetwork_EvenWhenSwitchedOff()
    {
        var folder = await _db.AddFolderAsync(null, "Synthetic");
        await _db.AddRecordAsync("4242", "Synthetic Title");
        var (code, _) = await _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = Mu, ExternalId = "4242" }, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Link_NotStored_SwitchedOff_Is409_ZeroCalls()
    {
        var folder = await _db.AddFolderAsync(null, "Synthetic");
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = Mu, ExternalId = BerserkId }, "admin"));
        Assert.Equal("metadata_disabled", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.Empty(await _db.Db.NodeSeriesLinks.ToListAsync());
    }

    [Fact]
    public async Task Link_PosterWithoutImageMagic_IsRejected_LinkStillMade()
    {
        await _h.EnableAsync();
        _h.Handler.Respond = r => r.RequestUri!.Host == "cdn.mangaupdates.com"
            ? ScriptedHandler.Bytes("<html>not an image</html>"u8.ToArray())
            : ScriptedHandler.Json(MuFixtures.Load("berserk-get"));
        var folder = await _db.AddFolderAsync(null, "Berserk");

        var (code, _) = await _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = Mu, ExternalId = BerserkId }, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(2, (await RecordAsync(BerserkId)).ImageState); // failed
        Assert.False(Directory.Exists(_h.ImageRoot) && Directory.EnumerateFiles(_h.ImageRoot).Any());
    }

    [Fact]
    public async Task Unlink_DeletesRecordAndItsPoster()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Berserk");
        await _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = Mu, ExternalId = BerserkId }, "admin");
        Assert.NotEmpty(Directory.GetFiles(_h.ImageRoot));

        await _h.Links().RemoveAsync(folder.PublicId, onlyDontMatch: false, "admin");

        Assert.Empty(await _db.Db.MetadataRecords.ToListAsync());
        Assert.Empty(Directory.GetFiles(_h.ImageRoot));
    }

    // --- Refresh ---

    [Fact]
    public async Task Refresh_FromAnInheritingArchive_UpdatesTheRecord_404MarksGone()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Berserk");
        var archive = await _db.AddArchiveAsync(folder, "v01.cbz");
        await _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = Mu, ExternalId = BerserkId }, "admin");

        _h.Time.Advance(TimeSpan.FromDays(3));
        var ok = await _h.Identify().RefreshAsync(archive.PublicId, "admin");
        Assert.Equal("Ok", ok!.State);
        Assert.False(ok.ImageUpdated); // same image URL, already stored
        Assert.Equal(_h.Time.Now, (await RecordAsync(BerserkId)).FetchedAt);

        _h.Handler.Respond = _ => ScriptedHandler.Json("{}", HttpStatusCode.NotFound);
        var gone = await _h.Identify().RefreshAsync(folder.PublicId, "admin");
        Assert.Equal("Gone", gone!.State);
        var record = await RecordAsync(BerserkId);
        Assert.Equal(1, record.FetchState);
        Assert.Equal("Berserk", record.Title); // stored data kept
        Assert.Equal(2, await _db.Db.AuditEvents.CountAsync(a => a.Action == "metadata.refresh"));
    }

    [Fact]
    public async Task Refresh_NewImageUrl_BumpsImageVersion()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Berserk");
        await _h.Identify().LinkAsync(folder.PublicId, new LinkSeriesRequest { Provider = Mu, ExternalId = BerserkId }, "admin");
        _h.Handler.Respond = r => r.RequestUri!.Host == "cdn.mangaupdates.com"
            ? ScriptedHandler.Bytes(MuFixtures.Png)
            : ScriptedHandler.Json(MuFixtures.Load("berserk-get").Replace("i501230.png", "i999.png", StringComparison.Ordinal));

        var result = await _h.Identify().RefreshAsync(folder.PublicId, "admin");

        Assert.True(result!.ImageUpdated);
        var record = await RecordAsync(BerserkId);
        Assert.Equal(2, record.ImageVersion);
        Assert.Equal([$"{record.Id}-2.png"], Directory.GetFiles(_h.ImageRoot).Select(f => Path.GetFileName(f)!).ToArray());
    }

    [Fact]
    public async Task Refresh_WithoutWebLink_Is404NoWebLink()
    {
        await _h.EnableAsync();
        var folder = await _db.AddFolderAsync(null, "Plain");
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Identify().RefreshAsync(folder.PublicId, "admin"));
        Assert.Equal("no_web_link", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    // --- Image store ---

    [Fact]
    public async Task ImageStore_PublishesAtomically_ReplacesOldVersions_RejectsNonImages()
    {
        var store = _h.Images;
        await store.PublishAsync(7, 1, MuFixtures.Png);
        await store.PublishAsync(7, 2, [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3]);
        await store.PublishAsync(70, 1, MuFixtures.Png);

        Assert.Equal(["7-2.jpg", "70-1.png"], Directory.GetFiles(_h.ImageRoot).Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray());
        Assert.Null(store.Open(7, 1));
        var opened = store.Open(7, 2);
        Assert.Equal("image/jpeg", opened!.Value.ContentType);
        opened.Value.Stream.Dispose();

        await Assert.ThrowsAsync<MetadataResponseInvalidException>(() => store.PublishAsync(8, 1, "GIF-ish text"u8.ToArray()));
        Assert.DoesNotContain(Directory.GetFiles(_h.ImageRoot), f => f.EndsWith(".tmp", StringComparison.Ordinal) || Path.GetFileName(f).StartsWith("8-", StringComparison.Ordinal));

        await store.OnRecordsRemovedAsync([7], CancellationToken.None);
        Assert.Equal(["70-1.png"], Directory.GetFiles(_h.ImageRoot).Select(f => Path.GetFileName(f)!).ToArray());
    }
}
