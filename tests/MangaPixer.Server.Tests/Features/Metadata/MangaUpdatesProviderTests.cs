namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using Xunit;

/// <summary>
/// Unit tests (1.24.0, lane B2): MangaUpdates JSON mapping from the RECORDED
/// fixtures (search + get for Berserk and Solo Leveling), the reference / status
/// parsers, the type -> origin/format and webtoon mapping, text flattening, the
/// allowlist handler, body caps and image magic bytes. The provider runs on the
/// REAL named-client registration with a scripted primary handler - no network.
/// </summary>
public sealed class MangaUpdatesProviderTests : IAsyncLifetime
{
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

    // --- Search mapping ---

    [Fact]
    public async Task Search_Berserk_MapsHitsFromFixture()
    {
        var page = await _h.Provider.SearchSeriesAsync(new ProviderSearchQuery("Berserk", _db.LibraryId), CancellationToken.None);

        Assert.Equal(61, page.TotalHits);
        Assert.Equal(5, page.Hits.Count);
        var top = page.Hits[0];
        Assert.Equal(MuFixtures.BerserkId.ToString(), top.ExternalId);
        Assert.Equal("Berserk", top.Title);
        Assert.Null(top.HitTitle); // same as the title
        Assert.Equal("Manga", top.ProviderType);
        Assert.Equal(1989, top.Year);
        Assert.Equal("https://cdn.mangaupdates.com/image/thumb/i501230.png", top.ImageRemoteUrl);
        // hit_title differing from the title is kept ("Kuang Bao Ni Xi" matched as "Berserk Counterattack").
        Assert.Contains(page.Hits, h => h.Title == "Kuang Bao Ni Xi" && h.HitTitle == "Berserk Counterattack");
    }

    [Fact]
    public async Task Search_SendsExactlyTheApprovedBodyAndHeaders()
    {
        await _h.Provider.SearchSeriesAsync(new ProviderSearchQuery("Solo Leveling", _db.LibraryId, 2, 10), CancellationToken.None);

        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Post, seen.Method);
        Assert.Equal("https://api.mangaupdates.com/v1/series/search", seen.Uri.AbsoluteUri);
        using var body = JsonDocument.Parse(seen.Body!);
        Assert.Equal(["search", "page", "perpage"], body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Solo Leveling", body.RootElement.GetProperty("search").GetString());
        Assert.Equal("MangaPixer-Metadata", seen.Headers["User-Agent"]);
        Assert.False(seen.Headers.ContainsKey("Cookie"));
        Assert.False(seen.Headers.ContainsKey("Authorization"));
        Assert.False(seen.Headers.ContainsKey("Referer"));
    }

    [Fact]
    public async Task Search_HideDoujinshiAndNovels_AddsOnlyTheFixedTypeFilter()
    {
        await _h.Provider.SearchSeriesAsync(
            new ProviderSearchQuery("Solo Leveling", _db.LibraryId, 1, 10, HideDoujinshiAndNovels: true), CancellationToken.None);

        var seen = Assert.Single(_h.Handler.Seen);
        using var body = JsonDocument.Parse(seen.Body!);
        Assert.Equal(["search", "page", "perpage", "filter_types"], body.RootElement.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(
            ["Doujinshi", "Novel", "Artbook", "Drama CD"],
            body.RootElement.GetProperty("filter_types").EnumerateArray().Select(e => e.GetString()!).ToArray());
    }

    // --- Get mapping ---

    [Fact]
    public async Task Get_Berserk_MapsRecordFromFixture()
    {
        var r = await _h.Provider.GetSeriesAsync(MuFixtures.BerserkId.ToString(), CancellationToken.None);

        Assert.NotNull(r);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal($"https://api.mangaupdates.com/v1/series/{MuFixtures.BerserkId}", seen.Uri.AbsoluteUri);
        Assert.Null(seen.Body);

        Assert.Equal("mangaupdates", r!.Provider);
        Assert.Equal("Berserk", r.Title);
        Assert.Equal(MetadataOrigin.Japan, r.Origin);
        Assert.Equal(MetadataFormat.Comic, r.Format);
        Assert.False(r.Webtoon); // categories present, no webtoon category
        Assert.Equal("Manga", r.ProviderType);
        Assert.Equal(1989, r.StartYear);
        Assert.Equal(MetadataOriginStatus.Ongoing, r.OriginStatus);
        Assert.Equal(43, r.OriginVolumes);
        Assert.Equal(386, r.LatestChapter);
        Assert.True(r.LicensedEn);
        Assert.False(r.TranslationComplete);
        Assert.Equal(10, r.AltTitles.Count);
        Assert.Contains(new MetadataJson.Creator("MIURA Kentaro", "author"), r.Creators);
        Assert.Contains(new MetadataJson.Creator("MIURA Kentaro", "artist"), r.Creators);
        Assert.Contains(new MetadataJson.Publisher("Hakusensha", "original"), r.Publishers);
        Assert.Contains(new MetadataJson.Publisher("Dark Horse", "english"), r.Publishers);
        Assert.Contains("Seinen", r.Genres);
        Assert.Equal(MangaUpdatesMapping.MaxStoredCategories, r.Categories.Count);
        Assert.True(r.Categories.Zip(r.Categories.Skip(1)).All(p => p.First.Votes >= p.Second.Votes));
        Assert.Equal("https://www.mangaupdates.com/series/njeqwry/berserk", r.SiteUrl);
        Assert.Equal("https://cdn.mangaupdates.com/image/i501230.png", r.ImageRemoteUrl);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1787905283), r.ProviderUpdatedAt);

        // Markdown links in the status note are flattened to their labels; no URL survives.
        Assert.StartsWith("43 Volumes (Ongoing)", r.StatusText);
        Assert.Contains("MORI Kouji took over", r.StatusText);
        Assert.DoesNotContain("http", r.StatusText);
        Assert.DoesNotContain("**", r.Description);
    }

    [Fact]
    public async Task Get_SoloLeveling_IsKoreanWebtoon_AndLinksFlattened()
    {
        var r = await _h.Provider.GetSeriesAsync(MuFixtures.SoloLevelingId.ToString(), CancellationToken.None);

        Assert.NotNull(r);
        Assert.Equal(MetadataOrigin.Korea, r!.Origin);
        Assert.Equal(MetadataFormat.Comic, r.Format);
        Assert.True(r.Webtoon); // "Webtoon/Webcomic" 65-1 net votes
        Assert.Equal(MetadataOriginStatus.Complete, r.OriginStatus);
        Assert.Equal(15, r.OriginVolumes);
        Assert.True(r.TranslationComplete);
        Assert.Contains(r.Categories, c => c.Name == "Webtoon/Webcomic" && c.Votes == 64);
        Assert.Contains("Daum", r.Description);
        Assert.DoesNotContain("https://", r.Description);
        Assert.Contains(new MetadataJson.Creator("DISCIPLES (Redice Studio)", "artist"), r.Creators);
    }

    [Fact]
    public async Task Get_404_ReturnsNull()
    {
        Assert.Null(await _h.Provider.GetSeriesAsync("999", CancellationToken.None));
        Assert.Null(await _h.Provider.GetSeriesAsync("not-a-number", CancellationToken.None));
        Assert.Single(_h.Handler.Seen); // the non-numeric id never left
    }

    [Fact]
    public async Task Get_UnknownFieldsAndMissingFields_AreTolerated()
    {
        _h.Handler.Respond = _ => ScriptedHandler.Json("{\"series_id\":7,\"title\":\"Synthetic\",\"brand_new_field\":{\"x\":[1,2]},\"type\":null}");
        var r = await _h.Provider.GetSeriesAsync("7", CancellationToken.None);
        Assert.NotNull(r);
        Assert.Equal("Synthetic", r!.Title);
        Assert.Null(r.Origin);
        Assert.Null(r.Format);
        Assert.Null(r.Webtoon); // no categories = unknown
        Assert.Empty(r.Creators);
    }

    [Fact]
    public async Task Get_MalformedJson_ThrowsTypedError()
    {
        _h.Handler.Respond = _ => ScriptedHandler.Json("{not json");
        var ex = await Assert.ThrowsAsync<MetadataResponseInvalidException>(() => _h.Provider.GetSeriesAsync("7", CancellationToken.None));
        Assert.Equal("malformed_json", ex.Code);
    }

    [Fact]
    public async Task Get_BodyOverCap_IsRefused()
    {
        _h.Handler.Respond = _ => ScriptedHandler.Json("{\"title\":\"" + new string('x', MetadataHttp.MaxJsonBytes) + "\"}");
        await Assert.ThrowsAsync<MetadataResponseTooLargeException>(() => _h.Provider.GetSeriesAsync("7", CancellationToken.None));
    }

    [Fact]
    public async Task Get_ServerError_ThrowsStatusWithRetryAfter()
    {
        _h.Handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(42));
            return response;
        };
        var ex = await Assert.ThrowsAsync<MetadataHttpStatusException>(() => _h.Provider.GetSeriesAsync("7", CancellationToken.None));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ex.Status);
        Assert.Equal(TimeSpan.FromSeconds(42), ex.RetryAfterDelta);
    }

    // --- Allowlist handler ---

    [Fact]
    public async Task Allowlist_RefusesOtherHost_WithoutReachingTheNetwork()
    {
        var client = _h.HttpFactory.CreateClient(MetadataHttp.MangaUpdatesApiClient);
        var ex = await Assert.ThrowsAsync<MetadataHostRefusedException>(() => client.GetAsync("https://example.com/v1/series/1"));
        Assert.Equal("host_not_allowed", ex.Code);
        await Assert.ThrowsAsync<MetadataHostRefusedException>(() => client.GetAsync("http://api.mangaupdates.com/v1/series/1"));
        await Assert.ThrowsAsync<MetadataHostRefusedException>(() => client.GetAsync("https://api.mangaupdates.com:8443/v1/series/1"));
        await Assert.ThrowsAsync<MetadataHostRefusedException>(() => client.GetAsync("https://cdn.mangaupdates.com/image/i1.png"));
        await Assert.ThrowsAsync<MetadataHostRefusedException>(() =>
            _h.HttpFactory.CreateClient(MetadataHttp.MangaUpdatesImageClient).GetAsync("https://api.mangaupdates.com/v1/series/1"));
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Allowlist_RefusesRedirects_AndNeverFollowsThem()
    {
        _h.Handler.Respond = _ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Found);
            response.Headers.Location = new Uri("https://attacker.example/steal");
            return response;
        };
        var ex = await Assert.ThrowsAsync<MetadataHostRefusedException>(() => _h.Provider.GetSeriesAsync("7", CancellationToken.None));
        Assert.Equal("redirect_refused", ex.Code);
        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(MetadataHttp.MangaUpdatesApiHost, seen.Uri.Host);
    }

    [Fact]
    public void Allowlist_ProductionPrimaryHandler_NeverRedirectsOrKeepsCookies()
    {
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();
        Microsoft.Extensions.DependencyInjection.LoggingServiceCollectionExtensions.AddLogging(services);
        com.lifepixer.mangapixer.Server.Program.AddMetadataClient(services, MetadataHttp.MangaUpdatesApiClient, MetadataHttp.MangaUpdatesApiHost, "application/json");
        using var sp = Microsoft.Extensions.DependencyInjection.ServiceCollectionContainerBuilderExtensions.BuildServiceProvider(services);
        var factory = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IHttpMessageHandlerFactory>(sp);

        HttpMessageHandler? handler = factory.CreateHandler(MetadataHttp.MangaUpdatesApiClient);
        var sawAllowlist = false;
        while (handler is DelegatingHandler delegating)
        {
            sawAllowlist |= delegating is HostAllowlistHandler;
            handler = delegating.InnerHandler;
        }
        Assert.True(sawAllowlist);
        var primary = Assert.IsType<SocketsHttpHandler>(handler);
        Assert.False(primary.AllowAutoRedirect);
        Assert.False(primary.UseCookies);
        var client = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IHttpClientFactory>(sp)
            .CreateClient(MetadataHttp.MangaUpdatesApiClient);
        Assert.Equal(TimeSpan.FromSeconds(10), client.Timeout);
        Assert.Equal("MangaPixer-Metadata", client.DefaultRequestHeaders.UserAgent.ToString());
    }

    // --- References ---

    [Theory]
    [InlineData("mu:51239621230", 51239621230)]
    [InlineData("MU: njeqwry", 51239621230)]
    [InlineData("https://www.mangaupdates.com/series/njeqwry/berserk", 51239621230)]
    [InlineData("https://www.mangaupdates.com/series/njeqwry", 51239621230)]
    [InlineData("www.mangaupdates.com/series/6z1uqw7/solo-leveling", 15180124327)]
    [InlineData("http://mangaupdates.com/series/6Z1UQW7/solo-leveling?tab=x", 15180124327)]
    public void Reference_ParsesLocallyToTheSeriesId(string input, long expected)
    {
        var (kind, id) = MangaUpdatesReference.Parse(input);
        Assert.Equal(MangaUpdatesReferenceKind.Series, kind);
        Assert.Equal(expected, id);
    }

    [Theory]
    [InlineData("https://www.mangaupdates.com/series.html?id=1234")]
    [InlineData("https://mangaupdates.com/series.html?id=88")]
    public void Reference_LegacyUrl_IsRecognizedAndRejected(string input) =>
        Assert.Equal(MangaUpdatesReferenceKind.Legacy, MangaUpdatesReference.Parse(input).Kind);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Berserk")]
    [InlineData("mu:")]
    [InlineData("mu:0")]
    [InlineData("mu:not-base36!")]
    [InlineData("mu:zzzzzzzzzzzzz")] // overflows a long
    [InlineData("https://evil.example/series/njeqwry/berserk")]
    [InlineData("https://www.mangaupdates.com/authors.html?id=1")]
    [InlineData("ftp://www.mangaupdates.com/series/njeqwry")]
    public void Reference_Invalid(string? input) =>
        Assert.Equal(MangaUpdatesReferenceKind.Invalid, MangaUpdatesReference.Parse(input).Kind);

    [Fact]
    public void Reference_Base36_RoundTrips()
    {
        Assert.Equal("njeqwry", MangaUpdatesReference.ToBase36(51239621230));
        foreach (var id in new long[] { 1, 35, 36, 15180124327, long.MaxValue })
        {
            Assert.True(MangaUpdatesReference.TryDecodeBase36(MangaUpdatesReference.ToBase36(id), out var back));
            Assert.Equal(id, back);
        }
        Assert.True(_h.Provider.TryParseReference("mu:njeqwry", out var reference));
        Assert.Equal(new ProviderRef("mangaupdates", "51239621230"), reference);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    // --- Mapping ---

    [Theory]
    [InlineData("Manga", MetadataOrigin.Japan, MetadataFormat.Comic)]
    [InlineData("Manhwa", MetadataOrigin.Korea, MetadataFormat.Comic)]
    [InlineData("Manhua", MetadataOrigin.ChinaTaiwan, MetadataFormat.Comic)]
    [InlineData("OEL", MetadataOrigin.EnglishOriginal, MetadataFormat.Comic)]
    [InlineData("Filipino", MetadataOrigin.Philippines, MetadataFormat.Comic)]
    [InlineData("Indonesian", MetadataOrigin.Indonesia, MetadataFormat.Comic)]
    [InlineData("Thai", MetadataOrigin.Thailand, MetadataFormat.Comic)]
    [InlineData("Vietnamese", MetadataOrigin.Vietnam, MetadataFormat.Comic)]
    [InlineData("Malaysian", MetadataOrigin.Malaysia, MetadataFormat.Comic)]
    [InlineData("Nordic", MetadataOrigin.Nordic, MetadataFormat.Comic)]
    [InlineData("French", MetadataOrigin.French, MetadataFormat.Comic)]
    [InlineData("Spanish", MetadataOrigin.Spanish, MetadataFormat.Comic)]
    [InlineData("German", MetadataOrigin.German, MetadataFormat.Comic)]
    [InlineData("Novel", null, MetadataFormat.Novel)]
    [InlineData("Artbook", null, MetadataFormat.Artbook)]
    [InlineData("Doujinshi", null, MetadataFormat.Doujinshi)]
    [InlineData("Drama CD", null, MetadataFormat.Audio)]
    [InlineData("Something New", MetadataOrigin.Other, MetadataFormat.Comic)]
    public void Type_MapsToOriginAndFormat(string type, MetadataOrigin? origin, MetadataFormat format)
    {
        Assert.Equal(origin, MangaUpdatesMapping.OriginOf(type));
        Assert.Equal(format, MangaUpdatesMapping.FormatOf(type));
    }

    [Fact]
    public void Webtoon_TriState_UsesNetVoteThreshold()
    {
        static MuCategory Cat(string name, int plus, int minus) => new() { Category = name, Votes = plus - minus, VotesPlus = plus, VotesMinus = minus };
        Assert.Null(MangaUpdatesMapping.WebtoonOf(null));
        Assert.Null(MangaUpdatesMapping.WebtoonOf([]));
        Assert.False(MangaUpdatesMapping.WebtoonOf([Cat("Magic", 10, 0)]));
        Assert.Null(MangaUpdatesMapping.WebtoonOf([Cat("Webtoon/Webcomic", 5, 1)])); // 4 net: unsure
        Assert.True(MangaUpdatesMapping.WebtoonOf([Cat("Webtoon/Webcomic", 6, 1)]));
        Assert.True(MangaUpdatesMapping.WebtoonOf([new MuCategory { Category = "webtoon/webcomic", Votes = 9 }]));
    }

    [Theory]
    [InlineData("43 Volumes (Ongoing)", 43, MetadataOriginStatus.Ongoing)]
    [InlineData("200 Chapters + Prologue (Complete)  \n15 Volumes (Complete)", 15, MetadataOriginStatus.Complete)]
    [InlineData("12 Chapters (Ongoing)", null, MetadataOriginStatus.Ongoing)]
    [InlineData("3 Volumes (Hiatus)", 3, MetadataOriginStatus.Hiatus)]
    [InlineData("2 Volumes (Discontinued)", 2, MetadataOriginStatus.Cancelled)]
    [InlineData("1 Volume (Complete)", 1, MetadataOriginStatus.Complete)]
    [InlineData("see notes", null, null)]
    public void StatusParser_ReadsVolumesAndStatus(string status, int? volumes, MetadataOriginStatus? expected)
    {
        var result = MangaUpdatesStatusParser.Parse(status);
        Assert.Equal(volumes, result.Volumes);
        Assert.Equal(expected, result.Status);
        Assert.NotNull(result.Text);
    }

    [Fact]
    public void StatusParser_Empty_IsAllNull() =>
        Assert.Equal(new MangaUpdatesStatusParser.Result(null, null, null), MangaUpdatesStatusParser.Parse("  "));

    [Fact]
    public void Text_Flatten_RemovesMarkupAndBoundsLength()
    {
        var flat = MetadataText.Flatten("**Bold** and *it* [label](https://x.example/a_(b)) <b>tag</b> &amp; <br>next\u0007\n\n\n\nend", 1000);
        Assert.Equal("Bold and it label tag &\nnext\n\nend", flat);
        Assert.Equal(5, MetadataText.Flatten(new string('a', 50), 5)!.Length);
        Assert.Null(MetadataText.Flatten("<p></p>", 10));
        Assert.Equal("a b", MetadataText.Line("a\n\nb", 10));
    }

    [Fact]
    public void Text_Flatten_StripsMarkdownHeadingsQuotesAndEmphasis_KeepingTheWords()
    {
        // The shape of a real provider description: a synopsis, then a "Notes" heading block.
        const string raw = "A delinquent finds a baby on the riverbank.\n\n##### Notes:\nIncludes a __one-shot__ and *extra* pages.\n"
            + "> Quoted from the _publisher_.\n#Hashtag stays\n### \nsnake_case_name stays";

        var flat = MetadataText.Flatten(raw, 1000);

        Assert.Equal(
            "A delinquent finds a baby on the riverbank.\n\nNotes:\nIncludes a one-shot and extra pages.\n"
            + "Quoted from the publisher.\n#Hashtag stays\n\nsnake_case_name stays",
            flat);
    }

    [Theory]
    [InlineData("# Title", "Title")]
    [InlineData("###### Six", "Six")]
    [InlineData("   ## Indented", "Indented")]
    [InlineData("Price #1 in sales", "Price #1 in sales")]
    [InlineData(">> nested quote", "nested quote")]
    [InlineData("a > b", "a > b")]
    [InlineData("&gt; encoded quote", "encoded quote")]
    public void Text_Flatten_MarkdownMarkers(string raw, string expected) =>
        Assert.Equal(expected, MetadataText.Flatten(raw, 1000));

    // --- Images ---

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }, "jpg")]
    [InlineData(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, "png")]
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }, "gif")]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }, "webp")]
    [InlineData(new byte[] { 0x3C, 0x73, 0x76, 0x67 }, null)] // <svg
    [InlineData(new byte[] { 0x3C, 0x68, 0x74, 0x6D, 0x6C }, null)] // <html
    [InlineData(new byte[] { }, null)]
    public void Image_MagicBytes(byte[] bytes, string? expected) =>
        Assert.Equal(expected, MetadataImageStore.DetectExtension(bytes));
}
