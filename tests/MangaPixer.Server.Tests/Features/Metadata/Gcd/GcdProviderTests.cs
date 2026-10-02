namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Gcd;

using System.Net;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// The Grand Comics Database provider (1.32.0, lane B) over the real named clients and the gateway, answered from recorded
/// fixtures of public titles: the request shape (paths under /api/ only, the start year, the fixed User-Agent), the record mapping
/// (edition origin, shape, distinct issue counts, no stored cover), Cloudflare / 429 backoff, and the slow 25-an-hour bucket.
/// </summary>
public sealed class GcdProviderTests : IAsyncLifetime
{
    private const string Gcd = "gcd";
    private MetadataTestDb _db = null!;
    private GcdHarness _h = null!;

    public async Task InitializeAsync()
    {
        _db = await MetadataTestDb.CreateAsync();
        _h = new GcdHarness(_db);
        await _h.EnableAsync();
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _db.DisposeAsync();
    }

    [Fact]
    public async Task Search_WithStartYear_OneGetUnderApi_FixedUserAgent_HitsAreWholeRecords()
    {
        var page = await _h.Gateway().SearchAsync(Gcd, _db.LibraryId, "Bone", 1, startYear: 1991);

        var request = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("https://www.comics.org/api/series/name/Bone/year/1991/?format=json", request.Uri.AbsoluteUri);
        Assert.Equal(MetadataHttp.UserAgent, request.Headers["User-Agent"]);
        Assert.Null(request.Body);

        Assert.Equal(3, page.TotalHits);
        var bone = Assert.Single(page.Hits, h => h.ExternalId == GcdFixtures.BoneId);
        Assert.NotNull(bone.Record);
        Assert.Equal("Bone", bone.Title);
        Assert.Equal(1991, bone.Year);
        Assert.Null(bone.ImageRemoteUrl);
    }

    [Fact]
    public async Task Search_StartYearIsNeverSentToMangaUpdates_AndOnlyAYearIsAccepted()
    {
        await _h.Gateway().SearchAsync(Gcd, _db.LibraryId, "Blacksad", 1, startYear: 99999);
        Assert.Equal("/api/series/name/Blacksad/", Assert.Single(_h.Handler.Seen).Uri.AbsolutePath);

        _h.Handler.Reset();
        _h.Handler.Respond = _ => ScriptedHandler.Json("{\"total_hits\":0,\"results\":[]}");
        await _h.Gateway().SearchAsync("mangaupdates", _db.LibraryId, "Bone", 1, startYear: 1991);
        var mu = Assert.Single(_h.Handler.Seen);
        Assert.DoesNotContain("1991", mu.Body!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Mapping_BoneIssueRun_DistinctIssues_NoCover_PublisherById()
    {
        var record = (await _h.Gateway().GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId))!;

        Assert.Equal("Bone", record.Title);
        Assert.Equal(1991, record.StartYear);
        Assert.Equal(MetadataOriginStatus.Complete, record.OriginStatus);
        Assert.Equal(MetadataOrigin.EnglishOriginal, record.Origin);
        Assert.Equal(MetadataFormat.Comic, record.Format);
        Assert.Equal(ComicsShape.Issues, record.Shape);
        Assert.Equal("en", record.Language);
        // 45 issue records (nine printings of #1 ...) are 20 issues.
        Assert.Equal(20, record.TotalChapters);
        Assert.Equal(20, record.LatestChapter);
        Assert.Null(record.OriginVolumes);
        Assert.Null(record.ImageRemoteUrl); // GCD covers are for identification only, never a stored cover.
        Assert.Equal("https://www.comics.org/series/4347/", record.SiteUrl);
        var extra = GcdExtra.Read(record.ExtraJson)!;
        Assert.Equal(672, extra.PublisherId);
        Assert.Equal(49773, extra.FirstIssueId);
        Assert.Equal("us", extra.Country);
        Assert.Empty(record.Publishers); // The series names its publisher by id: one more request, made only for a chosen candidate.
    }

    [Fact]
    public async Task Mapping_EditionsAndShapes_SagaRunVsTrades_BlacksadTranslations_AsterixAlbums()
    {
        var gateway = _h.Gateway();
        var saga = (await gateway.SearchAsync(Gcd, _db.LibraryId, "Saga", 1, startYear: 2012)).Hits.ToDictionary(h => h.ExternalId, h => h.Record!);
        Assert.Equal(ComicsShape.Issues, saga["63051"].Shape);
        Assert.Equal(72, saga["63051"].TotalChapters);
        Assert.Equal(ComicsShape.Collected, saga["69146"].Shape);
        Assert.Equal(12, saga["69146"].OriginVolumes);
        Assert.Equal(MetadataOrigin.Spanish, saga["69511"].Origin);

        var blacksad = (await gateway.SearchAsync(Gcd, _db.LibraryId, "Blacksad", 1)).Hits.ToDictionary(h => h.ExternalId, h => h.Record!);
        Assert.Equal(MetadataOrigin.French, blacksad["51161"].Origin);
        Assert.Equal("fr", blacksad["51161"].Language);
        Assert.Equal(7, blacksad["51161"].OriginVolumes); // 10 records, printings counted once.
        Assert.Equal(MetadataOrigin.EnglishOriginal, blacksad["51169"].Origin);
        Assert.Equal(MetadataOrigin.Dutch, blacksad["53934"].Origin); // Belgium, Dutch-language edition.
        Assert.Equal(MetadataOrigin.Nordic, blacksad["138839"].Origin);
        Assert.Equal(1, blacksad["49893"].OriginVolumes); // One unnumbered hardcover.
        // "Blacksad stories Weekly" is listed twice (glued + hardcover) with the same name, year, publisher and language: one candidate.
        Assert.Equal(37, blacksad.Count);

        var asterix = Assert.Single((await gateway.SearchAsync(Gcd, _db.LibraryId, "Asterix", 1, startYear: 1961)).Hits).Record!;
        Assert.Equal("Astérix", asterix.Title);
        Assert.Equal(ComicsShape.Collected, asterix.Shape); // "1 - Astérix le Gaulois": titled albums.
        Assert.Equal(24, asterix.OriginVolumes);
        Assert.Equal(MetadataOriginStatus.Complete, asterix.OriginStatus);
    }

    [Fact]
    public async Task Get_UnknownId_Null_AndANonNumericIdIsNeverSent()
    {
        Assert.Null(await _h.Gateway().GetSeriesAsync(Gcd, _db.LibraryId, "999999999"));
        Assert.Equal("/api/series/999999999/", Assert.Single(_h.Handler.Seen).Uri.AbsolutePath);

        _h.Handler.Reset();
        _h.Handler.Respond = _ => throw new InvalidOperationException("no request expected");
        Assert.Null(await _h.Gcd.GetSeriesAsync("12a", default));
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Details_PublisherOnceThenRemembered_FirstIssueCredits()
    {
        var details = _h.Details();
        var record = (await _h.Gateway().GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId))!;

        var withPublisher = await details.WithPublisherAsync(record, _db.LibraryId, null, default);
        Assert.Equal("Cartoon Books", Assert.Single(withPublisher.Publishers).Name);
        var issue = (await details.FirstIssueAsync(record, _db.LibraryId, null, default))!;
        Assert.Equal(28, issue.PageCount);
        Assert.Null(issue.CoverUrl); // The recorded issue has no cover scan.
        Assert.Contains(issue.Creators, c => c.Name == "Jeff Smith" && c.Role == "author");
        Assert.Contains(issue.Creators, c => c.Name == "Jeff Smith" && c.Role == "artist");
        Assert.Equal(["/api/series/4347/", "/api/publisher/672/", "/api/issue/49773/"], _h.Handler.Seen.Select(r => r.Uri.AbsolutePath));

        // The name is remembered: a second record of the same publisher costs no request.
        var again = await details.WithPublisherAsync(record, _db.LibraryId, null, default);
        Assert.Equal("Cartoon Books", again.Publishers[0].Name);
        Assert.Equal(3, _h.Handler.CallCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.OK)]
    public async Task CloudflareChallenge_IsABackoffOfGcdOnly(HttpStatusCode status)
    {
        _h.GcdOverride = _ => GcdFixtures.Challenge(status);
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().SearchAsync(Gcd, _db.LibraryId, "Bone", 1));
        Assert.Equal("provider_backoff", ex.Code);
        Assert.NotNull(await _h.Backoff().ActiveUntilAsync(Gcd));
        Assert.Null(await _h.Backoff().ActiveUntilAsync("mangaupdates")); // MangaUpdates keeps working.

        // While GCD backs off, nothing more is sent to it.
        var before = _h.Handler.CallCount;
        await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId));
        Assert.Equal(before, _h.Handler.CallCount);
    }

    [Fact]
    public async Task TooManyRequests_HonoursRetryAfter()
    {
        _h.GcdOverride = _ =>
        {
            var response = ScriptedHandler.Json("{\"detail\":\"Request was throttled.\"}", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(20));
            return response;
        };
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().SearchAsync(Gcd, _db.LibraryId, "Bone", 1));
        Assert.Equal("provider_backoff", ex.Code);
        var until = await _h.Backoff().ActiveUntilAsync(Gcd);
        Assert.Equal(_h.Time.GetUtcNow() + TimeSpan.FromMinutes(20), until);
    }

    [Fact]
    public async Task SlowBucket_AnAdminIsRefusedAtOnceWhenEmpty_NeverQueued()
    {
        var gateway = _h.Gateway();
        for (var i = 0; i < 3; i++)
            await gateway.GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId); // The burst of 3.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(429, ex.HttpStatus);
        Assert.Equal("provider_busy", ex.Code);
        Assert.NotNull(ex.RetryAt);
        Assert.Equal(3, _h.Handler.CallCount);
        Assert.Equal(3, (await _h.Budget().GetAsync()).Used); // A refused call costs no budget.
    }

    [Fact]
    public async Task SlowBucket_AutomaticWorkKeepsTwoTokensForAdmins()
    {
        var gateway = _h.Gateway();
        var call = MetadataCallContext.Automatic();
        await gateway.GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId, default, call); // 3 -> 2 left.
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => gateway.GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId, default, call));
        Assert.Equal("provider_busy", ex.Code);
        Assert.Equal(1, call.RequestsSent);

        // The two kept tokens serve an admin's search and preview.
        await gateway.SearchAsync(Gcd, _db.LibraryId, "Bone", 1, startYear: 1991);
        await gateway.GetSeriesAsync(Gcd, _db.LibraryId, GcdFixtures.BoneId);
        Assert.Equal(3, _h.Handler.CallCount);
    }

    [Fact]
    public async Task RemovedFromTheAllowlist_RefusedWithZeroCalls()
    {
        await _h.EnableAsync(removedProvidersJson: MetadataProviderAllowlist.Write([Gcd]));
        var ex = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().SearchAsync(Gcd, _db.LibraryId, "Bone", 1));
        Assert.Equal("provider_not_allowed", ex.Code);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Logs_NeverCarryTheSearchText()
    {
        await _h.Gateway().SearchAsync(Gcd, _db.LibraryId, "Blacksad", 1);
        Assert.DoesNotContain(_h.Logs.Lines, m => m.Contains("Blacksad", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>Pure parts of the GCD provider: reference parsing, the search path, origins, shapes, the challenge test.</summary>
public sealed class GcdMappingUnitTests
{
    [Theory]
    [InlineData("https://www.comics.org/series/4347/", GcdReferenceKind.Series, 4347)]
    [InlineData("http://comics.org/series/4347", GcdReferenceKind.Series, 4347)]
    [InlineData("www.comics.org/series/4347/covers/", GcdReferenceKind.Series, 4347)]
    [InlineData("https://www.comics.org/api/series/4347/?format=json", GcdReferenceKind.Series, 4347)]
    [InlineData("gcd:4347", GcdReferenceKind.Series, 4347)]
    [InlineData(" GCD: 4347 ", GcdReferenceKind.Series, 4347)]
    [InlineData("https://www.comics.org/issue/49773/", GcdReferenceKind.Issue, 0)]
    [InlineData("https://www.comics.org.evil.example/series/4347/", GcdReferenceKind.Invalid, 0)]
    [InlineData("https://www.mangaupdates.com/series/abc/x", GcdReferenceKind.Invalid, 0)]
    [InlineData("gcd:abc", GcdReferenceKind.Invalid, 0)]
    [InlineData("gcd:0", GcdReferenceKind.Invalid, 0)]
    [InlineData("", GcdReferenceKind.Invalid, 0)]
    public void Reference_Parse(string input, GcdReferenceKind kind, long id)
    {
        Assert.Equal((kind, id), GcdReference.Parse(input));
    }

    [Theory]
    [InlineData("Bone", null, 1, "series/name/Bone/?format=json")]
    [InlineData("Bone", 1991, 1, "series/name/Bone/year/1991/?format=json")]
    [InlineData("Maus", null, 2, "series/name/Maus/?format=json&page=2")]
    [InlineData("AC/DC", null, 1, "series/name/AC%20DC/?format=json")]
    [InlineData("Les Mémoires", null, 1, "series/name/Les%20M%C3%A9moires/?format=json")]
    [InlineData("What?#", null, 1, "series/name/What%3F%23/?format=json")]
    [InlineData("..", null, 1, null)]
    [InlineData(" / ", null, 1, null)]
    public void SearchPath_StaysOneSegmentUnderApi(string text, int? year, int page, string? expected)
    {
        Assert.Equal(expected, GcdProvider.SearchPath(text, year, page));
    }

    [Theory]
    [InlineData("us", "en", MetadataOrigin.EnglishOriginal)]
    [InlineData("gb", "en", MetadataOrigin.EnglishOriginal)]
    [InlineData("fr", "fr", MetadataOrigin.French)]
    [InlineData("be", "fr", MetadataOrigin.French)]
    [InlineData("be", "nl", MetadataOrigin.Dutch)]
    [InlineData("ch", "de", MetadataOrigin.German)]
    [InlineData("ch", "it", MetadataOrigin.Italian)]
    [InlineData("ar", "es", MetadataOrigin.Spanish)]
    [InlineData("at", "de", MetadataOrigin.German)]
    [InlineData("it", "it", MetadataOrigin.Italian)]
    [InlineData("nl", "nl", MetadataOrigin.Dutch)]
    [InlineData("dk", "da", MetadataOrigin.Nordic)]
    [InlineData("jp", "ja", MetadataOrigin.Japan)]
    [InlineData("kr", "ko", MetadataOrigin.Korea)]
    [InlineData("tw", "zh", MetadataOrigin.ChinaTaiwan)]
    [InlineData("br", "pt", MetadataOrigin.Other)]
    public void Origin_FromTheEditionsCountry(string country, string language, MetadataOrigin origin)
    {
        Assert.Equal(origin, GcdMapping.OriginOf(country, language));
    }

    [Theory]
    [InlineData("saddle-stitched", "ongoing series", ComicsShape.Issues)]
    [InlineData("geheftet", "", ComicsShape.Issues)]
    [InlineData("Squarebound; Trade Paperback", "Collected Series", ComicsShape.Collected)]
    [InlineData("dos carré [squarebound]", "série continue en cours", ComicsShape.Collected)]
    [InlineData("Tapa Dura", "Serie Regular", ComicsShape.Collected)]
    [InlineData("", "graphic novel", ComicsShape.Collected)]
    [InlineData("", "", ComicsShape.Unknown)]
    [InlineData("", "limited series", ComicsShape.Unknown)]
    public void Shape_FromBindingAndFormat(string binding, string format, ComicsShape shape)
    {
        Assert.Equal(shape, GcdMapping.ShapeOf(binding, format, ["1", "2"]));
    }

    [Fact]
    public void Shape_TitledNumbersAreAlbums()
    {
        Assert.Equal(ComicsShape.Collected, GcdMapping.ShapeOf("", "", ["1 - Astérix le Gaulois", "2 - La serpe d'or"]));
    }

    [Fact]
    public void Challenge_IsAnyHtmlAnswer()
    {
        Assert.True(GcdProvider.IsChallenge(GcdFixtures.Challenge()));
        Assert.False(GcdProvider.IsChallenge(ScriptedHandler.Json("{}")));
    }

    [Fact]
    public void CreditNames_DropNotesAndUnknowns()
    {
        Assert.Equal(["Jeff Smith"], GcdMapping.CreditNames("Jeff Smith (credited)"));
        Assert.Equal(["Juan Díaz Canales", "Juanjo Guarnido"], GcdMapping.CreditNames("Juan Díaz Canales; Juanjo Guarnido (painted art)"));
        Assert.Empty(GcdMapping.CreditNames("None"));
        Assert.Empty(GcdMapping.CreditNames("?"));
    }
}
