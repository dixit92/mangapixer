namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Gcd;

using System.Net;
using System.Text;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.Gcd;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Test support for the Grand Comics Database provider (1.32.0, lane B). Every GCD answer is a recorded fixture of a public title
// (Fixtures/Gcd/README.md); every MangaUpdates answer is synthetic JSON. No test touches the real network.

/// <summary>Recorded GCD responses (embedded resources) and the router that serves them by path.</summary>
public static class GcdFixtures
{
    public const string BoneId = "4347";

    public static string Load(string name)
    {
        using var stream = typeof(GcdFixtures).Assembly.GetManifestResourceStream($"Gcd.{name}.json")
            ?? throw new InvalidOperationException("Missing fixture " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>The fixtures by API path (query string ignored).</summary>
    public static readonly IReadOnlyDictionary<string, string> ByPath = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["/api/series/name/Bone/year/1991/"] = "search-bone-1991",
        ["/api/series/4347/"] = "series-4347",
        ["/api/issue/49773/"] = "issue-49773",
        ["/api/publisher/672/"] = "publisher-672",
        ["/api/series/name/Saga/year/2012/"] = "search-saga-2012",
        ["/api/series/name/Blacksad/"] = "search-blacksad",
        ["/api/series/name/Asterix/year/1961/"] = "search-asterix-1961",
        ["/api/series/name/Maus/"] = "search-maus",
    };

    /// <summary>A GCD answer for <paramref name="request"/>, or null when it is not a GCD request.</summary>
    public static HttpResponseMessage? Respond(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host == MetadataHttp.GcdImageHost)
            return ScriptedHandler.Bytes(MuFixtures.Png);
        if (uri.Host != MetadataHttp.GcdApiHost)
            return null;
        if (ByPath.TryGetValue(uri.AbsolutePath, out var name))
            return ScriptedHandler.Json(Load(name));
        if (uri.AbsolutePath.StartsWith("/api/series/name/", StringComparison.Ordinal))
            return ScriptedHandler.Json("{\"count\":0,\"next\":null,\"previous\":null,\"results\":[]}");
        return ScriptedHandler.Json(Load("not-found"), HttpStatusCode.NotFound);
    }

    /// <summary>A Cloudflare challenge page (HTML, 403).</summary>
    public static HttpResponseMessage Challenge(HttpStatusCode status = HttpStatusCode.Forbidden) =>
        new(status) { Content = new StringContent("<!DOCTYPE html><html><head><title>Just a moment...</title></head></html>", Encoding.UTF8, "text/html") };
}

/// <summary>
/// The metadata network with BOTH Identify providers (MangaUpdates + GCD), the real named-client registration and a
/// <see cref="ScriptedHandler"/> behind every client; GCD answers from <see cref="GcdFixtures"/>, MangaUpdates from
/// <see cref="MuSearch"/> / <see cref="MuRecords"/> (synthetic). Plus Identify, the GCD details and the automatic matching
/// service with the PRODUCTION matcher core.
/// </summary>
public sealed class GcdHarness : IDisposable
{
    private readonly ServiceProvider _http;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public GcdHarness(MetadataTestDb db, MetadataRateLimitOptions? rates = null)
    {
        Db = db;
        Handler = new ScriptedHandler { Respond = Route };
        Time = new ManualTime(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        Logs = new CapturingLoggerProvider();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        State = new MetadataGatewayState(rates ?? new MetadataRateLimitOptions { AutomaticInterval = TimeSpan.Zero });
        ImageRoot = Path.Combine(Path.GetTempPath(), "mangapixer-gcdimg-" + Guid.NewGuid().ToString("N")[..8]);
        Images = new MetadataImageStore(ImageRoot);

        var services = new ServiceCollection();
        services.AddSingleton(LoggerFactory);
        services.AddLogging();
        foreach (var (name, host, accept) in new[]
        {
            (MetadataHttp.MangaUpdatesApiClient, MetadataHttp.MangaUpdatesApiHost, "application/json"),
            (MetadataHttp.MangaUpdatesImageClient, MetadataHttp.MangaUpdatesImageHost, "image/*"),
            (MetadataHttp.GcdApiClient, MetadataHttp.GcdApiHost, "application/json"),
            (MetadataHttp.GcdImageClient, MetadataHttp.GcdImageHost, "image/*"),
        })
            Program.AddMetadataClient(services, name, host, accept).ConfigurePrimaryHttpMessageHandler(() => Handler);
        _http = services.BuildServiceProvider();
        HttpFactory = _http.GetRequiredService<IHttpClientFactory>();
        Mu = new MangaUpdatesProvider(HttpFactory);
        Gcd = new GcdProvider(HttpFactory);
        Registry = new MetadataProviderRegistry([Mu, Gcd]);
    }

    public MetadataTestDb Db { get; }
    public ScriptedHandler Handler { get; }
    public ManualTime Time { get; }
    public CapturingLoggerProvider Logs { get; }
    public ILoggerFactory LoggerFactory { get; }
    public IConfiguration Config { get; }
    public MetadataGatewayState State { get; }
    public IHttpClientFactory HttpFactory { get; }
    public string ImageRoot { get; }
    public MetadataImageStore Images { get; }
    internal MangaUpdatesProvider Mu { get; }
    internal GcdProvider Gcd { get; }
    public MetadataProviderRegistry Registry { get; }
    public MetadataAutoMatchState AutoState { get; } = new();

    /// <summary>MangaUpdates search answers by exact query text (synthetic); unknown queries return no hits.</summary>
    public Dictionary<string, MuJson.Hit[]> MuSearch { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>MangaUpdates GET answers by id (synthetic); unknown ids are 404.</summary>
    public Dictionary<long, string> MuRecords { get; } = [];

    /// <summary>Overrides the GCD answers when set (429, a challenge page ...); null falls back to the fixtures.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage?>? GcdOverride { get; set; }

    public IEnumerable<SeenRequest> GcdRequests => Handler.Seen.Where(r => r.Uri.Host == MetadataHttp.GcdApiHost);
    public IEnumerable<SeenRequest> MuRequests => Handler.Seen.Where(r => r.Uri.Host == MetadataHttp.MangaUpdatesApiHost);

    private HttpResponseMessage Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host is MetadataHttp.GcdApiHost or MetadataHttp.GcdImageHost)
            return GcdOverride?.Invoke(request) ?? GcdFixtures.Respond(request)!;
        if (uri.Host == MetadataHttp.MangaUpdatesImageHost)
            return ScriptedHandler.Bytes(MuFixtures.Png);
        if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/v1/series/search")
        {
            var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            var text = JsonDocument.Parse(body).RootElement.GetProperty("search").GetString()!;
            return ScriptedHandler.Json(MuJson.Search(MuSearch.TryGetValue(text, out var hits) ? hits : []));
        }
        if (request.Method == HttpMethod.Get && long.TryParse(uri.Segments[^1], out var id) && MuRecords.TryGetValue(id, out var json))
            return ScriptedHandler.Json(json);
        return ScriptedHandler.Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
    }

    public MetadataSettingsService Settings() => new(Db.Db, new AuditService(Db.Db), Config, Time, LoggerFactory.CreateLogger<MetadataSettingsService>());
    public MetadataBudget Budget() => new(Db.Db, State, Time);
    public MetadataBackoff Backoff() => new(Db.Db, State, Time, LoggerFactory.CreateLogger<MetadataBackoff>());

    public MetadataGateway Gateway() => new(Db.Db, Registry, State, Budget(), Backoff(), Settings(), HttpFactory, _cache,
        LoggerFactory.CreateLogger<MetadataGateway>());

    public GcdDetails Details() => new(Gateway(), Registry);

    public MetadataLinkService Links() => new(Db.Db, new AuditService(Db.Db), [Images], Time, LoggerFactory.CreateLogger<MetadataLinkService>());

    public SeriesInfoResolver Resolver() => new(Db.Db, Settings(), Registry);

    public MetadataIdentifyService Identify(com.lifepixer.mangapixer.Server.Features.Metadata.Declared.IDeclaredFactsReader? declared = null) => new(
        Db.Db, Gateway(), Budget(), Backoff(), Links(), Images, Registry, Resolver(), new AuditService(Db.Db), _cache, Time,
        LoggerFactory.CreateLogger<MetadataIdentifyService>(), declared: declared, autoState: AutoState, gcd: Details());

    /// <summary>The automatic matching service with the production matcher core and the GCD details.</summary>
    public MetadataAutoMatchService AutoMatch(com.lifepixer.mangapixer.Server.Features.Metadata.Declared.IDeclaredFactsReader? declared = null) => new(
        Db.Db, Gateway(), Budget(), Backoff(), Settings(), Identify(), AutoState, new AuditService(Db.Db), Time,
        LoggerFactory.CreateLogger<MetadataAutoMatchService>(), [new WorkDetector()], [new MatchQueryPlanner()], [new MatchScorer()],
        declared: declared, gcd: Details());

    /// <summary>Fetch on (current consent) for the library; Automatic matching on with the current automatic consent.</summary>
    public async Task EnableAsync(bool automatic = true, string? removedProvidersJson = null)
    {
        var row = await Db.Db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
        if (row is null)
        {
            row = new AppSettingsEntity();
            Db.Db.AppSettings.Add(row);
        }
        row.MetadataEnabled = true;
        row.MetadataConsentVersion = MetadataConsent.CurrentVersion;
        row.MetadataConsentAt = Time.GetUtcNow();
        row.MetadataAutoMatchEnabled = automatic;
        row.MetadataAutoConsentVersion = MetadataAutoConsent.CurrentVersion;
        row.MetadataAutoConsentAt = Time.GetUtcNow();
        row.MetadataProvidersJson = removedProvidersJson;
        var lib = await Db.Db.Libraries.FirstAsync(l => l.Id == Db.LibraryId);
        lib.MetadataEnabled = true;
        await Db.Db.SaveChangesAsync();
    }

    /// <summary>Queues every detected work of the library ("Match this library now") and processes rows like one worker pass.</summary>
    public async Task<int> MatchLibraryAsync(int max = 50)
    {
        var service = AutoMatch();
        var (error, _) = await service.StartBulkAsync(Db.LibraryPublicId, new com.lifepixer.mangapixer.Core.Api.MetadataMatchLibraryRequest(), "tester");
        Assert.Null(error);
        return await DrainAsync(max);
    }

    public async Task<int> DrainAsync(int max = 50)
    {
        var processed = 0;
        for (var i = 0; i < max; i++)
        {
            Db.Db.ChangeTracker.Clear();
            var service = AutoMatch();
            if (await service.CheckGlobalGateAsync() is not null)
                break;
            var row = await service.LeaseNextAsync("test");
            if (row is null)
                break;
            try
            {
                await service.ProcessAsync(row);
            }
            catch (MetadataGatewayException ex) when (MetadataAutoMatchService.IsRefusal(ex))
            {
                break;
            }
            processed++;
        }
        Db.Db.ChangeTracker.Clear();
        return processed;
    }

    /// <summary>Rates whose GCD bucket never has a token to spare for automatic work (2 tokens = the admin reserve).</summary>
    public static MetadataRateLimitOptions GcdReserveOnlyRates() => new()
    {
        AutomaticInterval = TimeSpan.Zero,
        Gcd = new System.Threading.RateLimiting.TokenBucketRateLimiterOptions
        {
            TokenLimit = MetadataHttp.GcdAutomaticReserve,
            TokensPerPeriod = 1,
            ReplenishmentPeriod = TimeSpan.FromHours(1),
            QueueLimit = 0,
            AutoReplenishment = true,
        },
    };

    public void Dispose()
    {
        _http.Dispose();
        _cache.Dispose();
        State.Dispose();
        LoggerFactory.Dispose();
        try { Directory.Delete(ImageRoot, true); } catch { /* best effort */ }
    }
}
