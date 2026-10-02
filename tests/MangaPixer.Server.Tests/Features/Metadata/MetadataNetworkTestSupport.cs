namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Threading.RateLimiting;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Test support for the metadata network half (1.24.0, lane B2). NO test ever
// touches the real network: every request ends in ScriptedHandler, which replays
// the recorded MangaUpdates fixtures (see Fixtures/MangaUpdates/README.md).

/// <summary>Recorded MangaUpdates responses (embedded resources).</summary>
public static class MuFixtures
{
    public const long BerserkId = 51239621230;
    public const long SoloLevelingId = 15180124327;

    /// <summary>A hand-written AniList GraphQL response (1.28.0).</summary>
    public static string LoadAniList(string name)
    {
        using var stream = typeof(MuFixtures).Assembly.GetManifestResourceStream($"AniList.{name}.json")
            ?? throw new InvalidOperationException("Missing fixture " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string Load(string name)
    {
        using var stream = typeof(MuFixtures).Assembly.GetManifestResourceStream($"MangaUpdates.{name}.json")
            ?? throw new InvalidOperationException("Missing fixture " + name);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    /// <summary>A minimal valid PNG signature + IHDR start (magic bytes are all the server checks).</summary>
    public static byte[] Png { get; } =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
        0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4,
    ];
}

/// <summary>One observed outbound request.</summary>
public sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// The primary handler every metadata test client ends in. Records each request
/// and answers from the fixtures (or <see cref="Respond"/> when set). With
/// <see cref="FailOnAnyRequest"/> it fails the test instead of answering.
/// </summary>
public sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly ConcurrentQueue<SeenRequest> _seen = new();

    public bool FailOnAnyRequest { get; set; }

    /// <summary>Overrides the fixture routing when set.</summary>
    public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }

    public IReadOnlyList<SeenRequest> Seen => _seen.ToList();
    public int CallCount => _seen.Count;

    public void Reset()
    {
        _seen.Clear();
        Respond = null;
        FailOnAnyRequest = false;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
        // Header values as sent on the wire: product tokens of the User-Agent are space-separated (1.32.0: "MangaPixer/x (+url)").
        var headers = request.Headers.ToDictionary(
            h => h.Key,
            h => string.Equals(h.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)
                ? request.Headers.UserAgent.ToString()
                : string.Join(",", h.Value),
            StringComparer.OrdinalIgnoreCase);
        _seen.Enqueue(new SeenRequest(request.Method, request.RequestUri!, body, headers));
        if (FailOnAnyRequest)
            throw new InvalidOperationException("Unexpected outbound request - a fresh install must make no call.");
        return Respond?.Invoke(request) ?? Route(request, body);
    }

    public static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static HttpResponseMessage Bytes(byte[] bytes, string type = "image/png") =>
        new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) { Headers = { ContentType = new(type) } } };

    private static HttpResponseMessage Route(HttpRequestMessage request, string? body)
    {
        var uri = request.RequestUri!;
        if (uri.Host == MetadataHttp.MangaUpdatesImageHost)
            return Bytes(MuFixtures.Png);
        if (uri.Host == MetadataHttp.AniListHost)
        {
            // 1.28.0: the chapters-per-volume lookup (hand-written fixtures, see Fixtures/AniList/README.md).
            if (body?.Contains("\"search\":\"Hagane no Renkinjutsushi\"", StringComparison.Ordinal) == true)
                return Json(MuFixtures.LoadAniList("search-fma"));
            if (body?.Contains("\"id\":30025", StringComparison.Ordinal) == true)
                return Json(MuFixtures.LoadAniList("get-fma"));
            if (body?.Contains("\"id\":30002", StringComparison.Ordinal) == true)
                return Json(MuFixtures.LoadAniList("get-running"));
            if (body?.Contains("\"search\"", StringComparison.Ordinal) == true)
                return Json("{\"data\":{\"Page\":{\"media\":[]}}}");
            return Json("{\"errors\":[{\"message\":\"Not Found.\",\"status\":404}],\"data\":{\"Media\":null}}", HttpStatusCode.NotFound);
        }
        if (request.Method == HttpMethod.Post && uri.AbsolutePath == "/v1/series/search")
        {
            if (body?.Contains("Solo Leveling", StringComparison.Ordinal) == true)
                return Json(MuFixtures.Load("solo-leveling-search"));
            if (body?.Contains("Berserk", StringComparison.Ordinal) == true)
                return Json(MuFixtures.Load("berserk-search"));
            return Json("{\"total_hits\":0,\"results\":[]}");
        }
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == $"/v1/series/{MuFixtures.BerserkId}")
            return Json(MuFixtures.Load("berserk-get"));
        if (request.Method == HttpMethod.Get && uri.AbsolutePath == $"/v1/series/{MuFixtures.SoloLevelingId}")
            return Json(MuFixtures.Load("solo-leveling-get"));
        return Json("{\"reason\":\"not found\"}", HttpStatusCode.NotFound);
    }
}

/// <summary>A settable clock.</summary>
public sealed class ManualTime : TimeProvider
{
    public ManualTime(DateTimeOffset now) => Now = now;

    public DateTimeOffset Now { get; set; }

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

/// <summary>Captures every log line at every level, rendered with its arguments.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyList<string> Lines => _lines.ToList();

    public ILogger CreateLogger(string categoryName) => new Capturing(this, categoryName);

    public void Dispose() { }

    private sealed class Capturing(CapturingLoggerProvider owner, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(" | ", pairs.Select(p => $"{p.Key}={p.Value}"))
                : string.Empty;
            owner._lines.Enqueue($"{logLevel} {category} {eventId.Id} {formatter(state, exception)} {values} {exception}");
        }
    }
}

/// <summary>
/// A gateway + identify service over a <see cref="MetadataTestDb"/>, with the REAL
/// named-client registration (allowlist handler, UA, timeout) whose primary
/// handler is replaced by <see cref="ScriptedHandler"/>.
/// </summary>
public sealed class GatewayHarness : IDisposable
{
    private readonly ServiceProvider _http;
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());

    public GatewayHarness(MetadataTestDb db, MetadataRateLimitOptions? rates = null, IDictionary<string, string?>? config = null, string? imageRoot = null)
    {
        Db = db;
        Handler = new ScriptedHandler();
        Time = new ManualTime(new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero));
        Logs = new CapturingLoggerProvider();
        LoggerFactory = Microsoft.Extensions.Logging.LoggerFactory.Create(b => b.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        Config = new ConfigurationBuilder().AddInMemoryCollection(config ?? new Dictionary<string, string?>()).Build();
        State = new MetadataGatewayState(rates ?? new MetadataRateLimitOptions());
        ImageRoot = imageRoot ?? Path.Combine(Path.GetTempPath(), "mangapixer-mdimg-" + Guid.NewGuid().ToString("N")[..8]);
        Images = new MetadataImageStore(ImageRoot);

        var services = new ServiceCollection();
        services.AddSingleton(LoggerFactory);
        services.AddLogging();
        Program.AddMetadataClient(services, MetadataHttp.MangaUpdatesApiClient, MetadataHttp.MangaUpdatesApiHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        Program.AddMetadataClient(services, MetadataHttp.MangaUpdatesImageClient, MetadataHttp.MangaUpdatesImageHost, "image/*")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        Program.AddMetadataClient(services, MetadataHttp.AniListClient, MetadataHttp.AniListHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        Program.AddMetadataClient(services, MetadataHttp.MangaDexApiClient, MetadataHttp.MangaDexApiHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        Program.AddMetadataClient(services, MetadataHttp.MangaDexImageClient, MetadataHttp.MangaDexImageHost, "image/*")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        // 1.32.0: Wikipedia + Wikidata (the volume-list companion).
        Program.AddMetadataClient(services, MetadataHttp.WikipediaClient, MetadataHttp.WikipediaHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        Program.AddMetadataClient(services, MetadataHttp.WikidataClient, MetadataHttp.WikidataHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        // 1.32.0: the Grand Comics Database clients end in the scripted handler too (a test may add a GcdProvider to Registry).
        Program.AddMetadataClient(services, MetadataHttp.GcdApiClient, MetadataHttp.GcdApiHost, "application/json")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        Program.AddMetadataClient(services, MetadataHttp.GcdImageClient, MetadataHttp.GcdImageHost, "image/*")
            .ConfigurePrimaryHttpMessageHandler(() => Handler);
        _http = services.BuildServiceProvider();
        HttpFactory = _http.GetRequiredService<IHttpClientFactory>();
        Provider = new MangaUpdatesProvider(HttpFactory);
        Registry = new MetadataProviderRegistry([Provider]);
        AniList = new global::com.lifepixer.mangapixer.Server.Features.Metadata.Providers.AniList.AniListProvider(HttpFactory);
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
    internal MangaUpdatesProvider Provider { get; }
    /// <summary>The providers the gateway resolves (MangaUpdates; a test may set a wider registry, e.g. with a GcdProvider).</summary>
    public MetadataProviderRegistry Registry { get; set; }
    internal global::com.lifepixer.mangapixer.Server.Features.Metadata.Providers.AniList.AniListProvider AniList { get; }

    /// <summary>The Missing report's chapters-per-volume service over this harness (1.28.0).</summary>
    public global::com.lifepixer.mangapixer.Server.Features.Metadata.Missing.MissingConversionService Conversion() => new(Db.Db, Gateway(), AniList, Report(),
        new AuditService(Db.Db), _cache, Time, LoggerFactory.CreateLogger<global::com.lifepixer.mangapixer.Server.Features.Metadata.Missing.MissingConversionService>());

    public global::com.lifepixer.mangapixer.Server.Features.Metadata.Missing.MissingReportService Report() =>
        new(Db.Db, LoggerFactory.CreateLogger<global::com.lifepixer.mangapixer.Server.Features.Metadata.Missing.MissingReportService>());

    public MetadataSettingsService Settings() => new(Db.Db, new AuditService(Db.Db), Config, Time, LoggerFactory.CreateLogger<MetadataSettingsService>());
    public MetadataBudget Budget() => new(Db.Db, State, Time);
    public MetadataBackoff Backoff() => new(Db.Db, State, Time, LoggerFactory.CreateLogger<MetadataBackoff>());

    public MetadataGateway Gateway() => new(Db.Db, Registry, State, Budget(), Backoff(), Settings(), HttpFactory, _cache,
        LoggerFactory.CreateLogger<MetadataGateway>());

    public MetadataLinkService Links() => new(Db.Db, new AuditService(Db.Db), [Images], Time, LoggerFactory.CreateLogger<MetadataLinkService>());

    public SeriesInfoResolver Resolver() => new(Db.Db, Settings(), Registry);

    public MetadataIdentifyService Identify() => new(Db.Db, Gateway(), Budget(), Backoff(), Links(), Images, Registry, Resolver(),
        new AuditService(Db.Db), _cache, Time, LoggerFactory.CreateLogger<MetadataIdentifyService>());

    /// <summary>Turns on the global switch (current consent) and the given library's switch.</summary>
    public async Task EnableAsync(long? libraryId = null, bool library = true, int? consentVersion = null)
    {
        var row = await Db.Db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
        if (row is null)
        {
            row = new AppSettingsEntity();
            Db.Db.AppSettings.Add(row);
        }
        row.MetadataEnabled = true;
        row.MetadataConsentVersion = consentVersion ?? MetadataConsent.CurrentVersion;
        row.MetadataConsentAt = Time.GetUtcNow();
        var lib = await Db.Db.Libraries.FirstAsync(l => l.Id == (libraryId ?? Db.LibraryId));
        lib.MetadataEnabled = library;
        await Db.Db.SaveChangesAsync();
    }

    /// <summary>Reads the singleton settings row fresh from the database.</summary>
    public Task<AppSettingsEntity> ReadSettingsAsync() =>
        Db.Db.AppSettings.AsNoTracking().FirstAsync(s => s.Id == AppSettingsEntity.SingletonId);

    /// <summary>A bucket that never refills and has no queue: the first call takes the only token.</summary>
    public static MetadataRateLimitOptions SingleTokenRates() => new()
    {
        Api = new TokenBucketRateLimiterOptions
        {
            TokenLimit = 1,
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
