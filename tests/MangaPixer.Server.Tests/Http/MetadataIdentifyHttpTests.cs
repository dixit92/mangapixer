namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Updates;
using com.lifepixer.mangapixer.Server.Hosting;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using com.lifepixer.mangapixer.Tests.Server.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Xunit;

/// <summary>
/// HTTP tests (WebApplicationFactory) for the metadata network half (1.24.0, lane
/// B2). Every outbound request of the THREE server-side named clients (MangaUpdates
/// API, MangaUpdates images, Update Checker) ends in a <see cref="ScriptedHandler"/>
/// installed with <c>ConfigureTestServices</c> - the real network is never used.
/// Covers: a fresh install makes no outbound call; non-admins get 403 on every
/// identify endpoint; switched-off refusals make zero calls; the full identify ->
/// link -> poster -> image endpoint round trip; 404 (never 403) on the image for a
/// non-member; budget 429 and backoff 503 through the API; and a sentinel query
/// never reaching any log line.
/// </summary>
[Collection("HttpSerial")]
public sealed class MetadataIdentifyHttpTests
{
    private const string LibPub = "mdnlib1";
    private const string BerserkId = "51239621230";

    // Node public ids: mdnSeries (folder "Berserk"), mdnArc (its archive), mdnPlain (folder).
    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        if (await db.Libraries.AnyAsync(l => l.PublicId == LibPub))
            return;
        var lib = new LibraryEntity { PublicId = LibPub, DisplayName = "Net Lib", RootPath = "/synthetic/mdn", CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(lib);
        await db.SaveChangesAsync();
        var series = Node("mdnSeries", lib.Id, null, 0, "Berserk");
        var plain = Node("mdnPlain", lib.Id, null, 0, "Plain Folder");
        db.CatalogNodes.AddRange(series, plain);
        await db.SaveChangesAsync();
        var arc = Node("mdnArc", lib.Id, series.Id, 1, "Berserk v01");
        db.CatalogNodes.Add(arc);
        await db.SaveChangesAsync();
        db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = arc.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 2 });
        await db.SaveChangesAsync();
    }

    private static CatalogNodeEntity Node(string pub, long libId, long? parentId, int kind, string name) => new()
    {
        PublicId = pub,
        LibraryId = libId,
        ParentId = parentId,
        Kind = kind,
        DisplayName = name,
        RelativePath = pub,
        PathKey = pub,
        SortKey = (kind == 0 ? "0" : "1") + name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static async Task EnableAsync(HttpClient admin, bool library = true)
    {
        var global = await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings",
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion });
        global.EnsureSuccessStatusCode();
        var lib = await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { FetchEnabled = library });
        lib.EnsureSuccessStatusCode();
    }

    private static async Task<ApiError> ErrorAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!;

    // --- A fresh install makes no outbound call ---

    [Fact]
    public async Task FreshInstall_StartupBrowseSeriesInfoAndSettings_MakeNoOutboundCall()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        foreach (var url in new[]
        {
            "/health",
            "/api/v1/libraries",
            $"/api/v1/libraries/{LibPub}/browse",
            "/api/v1/nodes/mdnSeries",
            "/api/v1/nodes/mdnSeries/series-info",
            "/api/v1/nodes/mdnArc/series-info?includeItems=true",
            "/api/v1/admin/metadata/settings",
            "/api/v1/admin/metadata/nodes/mdnSeries/identify",
            "/api/v1/operations/update-check",
        })
        {
            var response = await admin.GetAsync(url);
            Assert.True(response.IsSuccessStatusCode, $"{url} -> {(int)response.StatusCode}");
        }
        // Identify actions while switched off: refused before any call.
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Berserk" })).StatusCode);

        Assert.Equal(0, factory.Handler.CallCount);

        // The provider is registered now, but only the gateway ever resolves it.
        var registry = factory.Services.GetRequiredService<com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MetadataProviderRegistry>();
        Assert.Equal(["mangaupdates"], registry.All.Select(p => p.Id).ToArray());
    }

    // --- Authorization ---

    [Fact]
    public async Task IdentifyEndpoints_NonAdmin_403()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var reader = await factory.CreateReaderClientAsync("mdnreader", LibPub);

        var calls = new List<HttpResponseMessage>
        {
            await reader.GetAsync("/api/v1/admin/metadata/nodes/mdnSeries/identify"),
            await reader.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Berserk" }),
            await reader.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/lookup", new IdentifyLookupRequest { Reference = "mu:1" }),
            await reader.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/preview", new IdentifyPreviewRequest { Provider = "mangaupdates", ExternalId = BerserkId }),
            await reader.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId }),
            await reader.PostAsync("/api/v1/admin/metadata/nodes/mdnSeries/refresh", null),
            await reader.GetAsync("/api/v1/admin/metadata/candidates/abc/image"),
        };
        Assert.All(calls, r => Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    // --- Switched-off refusals ---

    [Fact]
    public async Task Disabled_GlobalOrLibrary_Or_ConfigKill_409_ZeroCalls()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var search = new IdentifySearchRequest { Query = "Berserk" };

        var off = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", search);
        Assert.Equal(HttpStatusCode.Conflict, off.StatusCode);
        Assert.Equal("metadata_disabled", (await ErrorAsync(off)).Error);

        await EnableAsync(admin, library: false);
        var libOff = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", search);
        Assert.Equal("library_metadata_disabled", (await ErrorAsync(libOff)).Error);
        var linkOff = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId });
        Assert.Equal(HttpStatusCode.Conflict, linkOff.StatusCode);
        Assert.Equal(0, factory.Handler.CallCount);

        using var killed = new MetadataNetworkWebApplicationFactory(networkDisabled: true);
        await SeedAsync(killed);
        var killedAdmin = await killed.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(killedAdmin);
        var kill = await killedAdmin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", search);
        Assert.Equal("metadata_network_disabled", (await ErrorAsync(kill)).Error);
        Assert.True((await killedAdmin.GetFromJsonAsync<MetadataSettingsDto>("/api/v1/admin/metadata/settings", TestJson.Web))!.NetworkDisabledByConfig);
        Assert.Equal(0, killed.Handler.CallCount);
    }

    // --- The round trip ---

    [Fact]
    public async Task Identify_Search_Preview_Link_Poster_Unlink_RoundTrip()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        // Enabling makes no call.
        await EnableAsync(admin);
        Assert.Equal(0, factory.Handler.CallCount);

        var ctx = await admin.GetFromJsonAsync<IdentifyContextDto>("/api/v1/admin/metadata/nodes/mdnSeries/identify", TestJson.Web);
        Assert.True(ctx!.FetchAvailable);
        Assert.Equal(["Berserk"], ctx.Suggestions);

        var searchResponse = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Berserk" });
        searchResponse.EnsureSuccessStatusCode();
        var search = await searchResponse.Content.ReadFromJsonAsync<IdentifySearchResultDto>(TestJson.Web);
        var top = search!.Candidates[0];
        Assert.Equal(BerserkId, top.ExternalId);
        Assert.Equal(MatchStrength.Strong, top.Strength);

        var thumb = await admin.GetAsync($"/api/v1/admin/metadata/candidates/{top.ImageToken}/image");
        Assert.Equal(HttpStatusCode.OK, thumb.StatusCode);
        Assert.Equal("image/png", thumb.Content.Headers.ContentType!.MediaType);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/admin/metadata/candidates/nope/image")).StatusCode);

        var lookup = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/lookup", new IdentifyLookupRequest { Reference = "mu:njeqwry" });
        lookup.EnsureSuccessStatusCode();
        var preview = await lookup.Content.ReadFromJsonAsync<IdentifyPreviewDto>(TestJson.Web);
        Assert.Equal("Berserk", preview!.Title);
        Assert.Equal(MetadataOrigin.Japan, preview.Origin);

        var legacy = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/lookup", new IdentifyLookupRequest { Reference = "https://www.mangaupdates.com/series.html?id=1" });
        Assert.Equal(HttpStatusCode.BadRequest, legacy.StatusCode);
        Assert.Equal("legacy_url", (await ErrorAsync(legacy)).Error);

        var calls = factory.Handler.CallCount; // search + thumb + get
        Assert.Equal(3, calls);
        var link = await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/link",
            new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId, MatchMethod = MetadataMatchMethod.Search, MatchScore = top.Score });
        link.EnsureSuccessStatusCode();
        Assert.Equal(calls + 1, factory.Handler.CallCount); // record reused (previewed seconds ago); only the poster

        // The archive inherits the link; its poster URL is node-scoped and versioned.
        var info = await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/mdnArc/series-info", TestJson.Web);
        Assert.Equal("Berserk", info!.Title);
        Assert.True(info.Web!.HasImage);
        Assert.StartsWith("/api/v1/nodes/mdnArc/series-info/image?v=", info.Web.ImageUrl);
        Assert.Equal("https://www.mangaupdates.com/series/njeqwry/berserk", info.Web.SiteUrl);

        var image = await admin.GetAsync(info.Web.ImageUrl);
        Assert.Equal(HttpStatusCode.OK, image.StatusCode);
        Assert.Equal("image/png", image.Content.Headers.ContentType!.MediaType);
        Assert.Equal(MuFixtures.Png, await image.Content.ReadAsByteArrayAsync());
        Assert.Contains("immutable", image.Headers.CacheControl!.ToString());
        Assert.True(image.Headers.CacheControl.Private);
        Assert.Equal("nosniff", image.Headers.GetValues("X-Content-Type-Options").Single());

        // Refresh: one GET.
        var refresh = await admin.PostAsync("/api/v1/admin/metadata/nodes/mdnArc/refresh", null);
        refresh.EnsureSuccessStatusCode();
        Assert.Equal("Ok", (await refresh.Content.ReadFromJsonAsync<MetadataRefreshResultDto>(TestJson.Web))!.State);

        // Unlink: record + poster go, the image endpoint answers 404.
        (await admin.DeleteAsync("/api/v1/admin/metadata/nodes/mdnSeries/link")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync(info.Web.ImageUrl)).StatusCode);
        Assert.Empty(Directory.GetFiles(Path.Combine(factory.DataRoot, "metadata-images")));

        // Nothing but the two approved hosts was ever contacted.
        Assert.All(factory.Handler.Seen, s => Assert.Contains(s.Uri.Host, new[] { "api.mangaupdates.com", "cdn.mangaupdates.com" }));
        Assert.All(factory.Handler.Seen, s => Assert.Equal("MangaPixer-Metadata", s.Headers["User-Agent"]));
    }

    [Fact]
    public async Task SeriesImage_NonMember404_MemberOk_HiddenIs404()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId })).EnsureSuccessStatusCode();
        var url = (await admin.GetFromJsonAsync<SeriesInfoDto>("/api/v1/nodes/mdnSeries/series-info", TestJson.Web))!.Web!.ImageUrl!;

        var outsider = await factory.CreateReaderClientAsync("mdnoutsider", grantLibrary: null);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await outsider.GetAsync("/api/v1/nodes/nosuchnode/series-info/image")).StatusCode);

        var member = await factory.CreateReaderClientAsync("mdnmember", LibPub);
        Assert.Equal(HttpStatusCode.OK, (await member.GetAsync(url)).StatusCode);

        (await admin.PutAsJsonAsync($"/api/v1/admin/metadata/libraries/{LibPub}", new UpdateMetadataLibraryRequest { ShowSeriesInfo = false })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.NotFound, (await member.GetAsync(url)).StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await admin.GetAsync("/api/v1/nodes/mdnPlain/series-info/image")).StatusCode);
    }

    // --- Budget and backoff through the API ---

    [Fact]
    public async Task Budget_429_Then_Backoff_503_WithRetryAfter()
    {
        using var factory = new MetadataNetworkWebApplicationFactory();
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { DailyBudget = 1 })).EnsureSuccessStatusCode();

        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Berserk" })).EnsureSuccessStatusCode();
        var spent = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Solo Leveling" });
        Assert.Equal(HttpStatusCode.TooManyRequests, spent.StatusCode);
        Assert.Equal("budget_exhausted", (await ErrorAsync(spent)).Error);
        Assert.Equal(1, factory.Handler.CallCount);
        var settings = await admin.GetFromJsonAsync<MetadataSettingsDto>("/api/v1/admin/metadata/settings", TestJson.Web);
        Assert.Equal(1, settings!.BudgetUsedToday);

        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/settings", new UpdateMetadataSettingsRequest { DailyBudget = 100 })).EnsureSuccessStatusCode();
        factory.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(120)) },
        };
        var limited = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Solo Leveling" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, limited.StatusCode);
        Assert.Equal("provider_backoff", (await ErrorAsync(limited)).Error);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.FromSeconds(100));

        var during = await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = "Something Else" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, during.StatusCode);
        Assert.Equal(2, factory.Handler.CallCount);
        settings = await admin.GetFromJsonAsync<MetadataSettingsDto>("/api/v1/admin/metadata/settings", TestJson.Web);
        Assert.NotNull(settings!.BackoffUntil);
        Assert.Equal("rate_limited", settings.LastErrorCode);
    }

    // --- Privacy: the query never reaches a log line ---

    [Fact]
    public async Task SearchQuery_NeverAppearsInAnyLogLine()
    {
        const string sentinel = "Zq9Sentinel Private Shelf Title";
        var sink = new CollectingSink();
        using var factory = new MetadataNetworkWebApplicationFactory(sink: sink);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await EnableAsync(admin);

        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = sentinel })).EnsureSuccessStatusCode();
        factory.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.InternalServerError);
        await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/search", new IdentifySearchRequest { Query = sentinel + " two" });
        factory.Handler.Respond = null;
        (await admin.PostAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/lookup",
            new IdentifyLookupRequest { Reference = "https://www.mangaupdates.com/series/njeqwry/zq9sentinel-slug" })).EnsureSuccessStatusCode();
        (await admin.PutAsJsonAsync("/api/v1/admin/metadata/nodes/mdnSeries/link", new LinkSeriesRequest { Provider = "mangaupdates", ExternalId = BerserkId })).EnsureSuccessStatusCode();

        var events = sink.Events;
        Assert.Contains(events, e => e.MessageTemplate.Text.Contains("Metadata {Provider} {Operation}", StringComparison.Ordinal));
        foreach (var e in events)
        {
            var rendered = e.RenderMessage() + " " + string.Join(" ", e.Properties.Select(p => p.Value.ToString())) + " " + e.Exception;
            Assert.False(rendered.Contains("Zq9Sentinel", StringComparison.Ordinal), "Query leaked into a log line: " + e.MessageTemplate.Text);
            Assert.DoesNotContain("zq9sentinel-slug", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("cdn.mangaupdates.com", rendered, StringComparison.Ordinal);
            Assert.DoesNotContain("api.mangaupdates.com", rendered, StringComparison.Ordinal);
        }
    }
}

/// <summary>
/// A test host whose three server-side named clients end in one
/// <see cref="ScriptedHandler"/> (the real network is never reachable), with
/// optional <c>Metadata:NetworkDisabled</c> and a Serilog collecting sink.
/// </summary>
public sealed class MetadataNetworkWebApplicationFactory : WebApplicationFactory<com.lifepixer.mangapixer.Server.Program>
{
    private readonly string _tempRoot = Path.Combine(Path.GetTempPath(), "mangapixer-mdnet-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly CollectingSink? _sink;
    private ILogger? _originalLogger;
    private HttpClient? _admin;

    public MetadataNetworkWebApplicationFactory(bool failOnAnyRequest = false, bool networkDisabled = false, CollectingSink? sink = null)
    {
        _sink = sink;
        Handler.FailOnAnyRequest = failOnAnyRequest;
        foreach (var dir in new[] { DataRoot, Path.Combine(_tempRoot, "cache"), Path.Combine(_tempRoot, "scratch") })
            Directory.CreateDirectory(dir);

        var storage = new StorageRootOverride(
            DataRoot: DataRoot,
            CacheRoot: Path.Combine(_tempRoot, "cache"),
            ScratchRoot: Path.Combine(_tempRoot, "scratch"),
            WorkerExecutablePath: "",
            RateLimitDisabled: true,
            ExtraConfiguration: new Dictionary<string, string?>
            {
                ["MangaPixer:Scanning:Scheduler:Enabled"] = "false",
                ["Metadata:NetworkDisabled"] = networkDisabled ? "true" : "false",
            });
        using (TestHostStorageOverride.Push(storage))
        {
            using var boot = CreateClient();
        }
    }

    public ScriptedHandler Handler { get; } = new();

    public string DataRoot => Path.Combine(_tempRoot, "data");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureTestServices(services =>
        {
            foreach (var type in new[] { typeof(MediaWorkerHostedService), typeof(ThumbnailBackfillHostedService), typeof(ComicInfoBackfillHostedService) })
            {
                var descriptor = services.FirstOrDefault(d => d.ImplementationType == type);
                if (descriptor is not null)
                    services.Remove(descriptor);
            }

            foreach (var name in new[] { MetadataHttp.MangaUpdatesApiClient, MetadataHttp.MangaUpdatesImageClient, UpdateCheckService.HttpClientName })
                services.AddHttpClient(name).ConfigurePrimaryHttpMessageHandler(() => Handler);

            if (_sink is not null)
            {
                _originalLogger = Log.Logger;
                Log.Logger = new LoggerConfiguration()
                    .MinimumLevel.Verbose()
                    .WriteTo.Sink(_sink)
                    .WriteTo.Logger(_originalLogger)
                    .CreateLogger();
            }
        });
    }

    /// <summary>First-run setup + password change, like the shared factory; cached per instance.</summary>
    public async Task<HttpClient> LoginAsAdminWithChangedPasswordAsync()
    {
        if (_admin is not null)
            return _admin;
        var setup = CreateClient();
        (await setup.PostAsJsonAsync("/api/v1/auth/setup", new SetupRequest { Username = "admin", Password = "MangaPixer-Change-Me-Now!" })).EnsureSuccessStatusCode();
        await AddCsrfAsync(setup);
        (await setup.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "MangaPixer-Change-Me-Now!",
            NewPassword = "TestPassword123!",
        })).EnsureSuccessStatusCode();

        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "admin", Password = "TestPassword123!" })).EnsureSuccessStatusCode();
        await AddCsrfAsync(client);
        return _admin = client;
    }

    /// <summary>An activated reader, granted <paramref name="grantLibrary"/> when given.</summary>
    public async Task<HttpClient> CreateReaderClientAsync(string username, string? grantLibrary)
    {
        var admin = await LoginAsAdminWithChangedPasswordAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = username, IsAdmin = false });
        created.EnsureSuccessStatusCode();
        var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        (await CreateClient().PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();

        if (grantLibrary is not null)
        {
            using var scope = Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var userId = await db.Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).Select(u => u.Id).SingleAsync();
            var libId = await db.Libraries.Where(l => l.PublicId == grantLibrary).Select(l => l.Id).SingleAsync();
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = userId, LibraryId = libId, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        await AddCsrfAsync(client);
        return client;
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (_originalLogger is not null)
                Log.Logger = _originalLogger;
            try { Directory.Delete(_tempRoot, true); } catch { /* best effort */ }
        }
        base.Dispose(disposing);
    }
}
