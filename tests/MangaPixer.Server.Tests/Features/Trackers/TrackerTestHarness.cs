namespace com.lifepixer.mangapixer.Tests.Server.Features.Trackers;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Trackers;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>One synthetic linked series: a library, its series folder, chapter archives and the linked MangaUpdates record.</summary>
internal sealed record TrackerSeriesSeed(
    long LibraryId, string LibraryPublicId, long FolderId, string FolderPublicId, long RecordId, string RecordPublicId, string MangaUpdatesId,
    string MalId, IReadOnlyList<(long Id, string PublicId)> Archives);

/// <summary>
/// A host for the tracker engine's HTTP tests (1.43.0): <see cref="ApiTokenTestFactory"/> with two FAKE tracker clients (no network),
/// helpers to allow a tracker, connect the admin's account, seed a synthetic linked series and wait for the relay. Names are made up.
/// </summary>
internal sealed class TrackerTestHarness : IAsyncDisposable
{
    public const string RedirectUri = "https://reader.invalid/trackers/callback/mal";

    private static int s_next;

    public TrackerTestHarness(IReadOnlyDictionary<string, string?>? configuration = null, Action<IServiceCollection>? configureServices = null)
    {
        Factory = new ApiTokenTestFactory(configuration, configureServices: services =>
        {
            // The real adapters (AddTrackerAdapters) must never answer a test: drop them, then add the fakes (1.43.0 merge of lanes E + A).
            services.RemoveAll<ITrackerClient>();
            services.AddSingleton<ITrackerClient>(Mal);
            services.AddSingleton<ITrackerClient>(MangaUpdates);
            // 1.44.0: the system clock until a test moves it (rate-limited retries without a race on a loaded box).
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Time);
            configureServices?.Invoke(services);
        });
    }

    public FakeTrackerClient Mal { get; } = new(TrackerSlugs.MyAnimeList, TrackerConnectKind.OAuthPkce);
    public FakeTrackerClient MangaUpdates { get; } = new(TrackerSlugs.MangaUpdates, TrackerConnectKind.Password);
    public ApiTokenTestFactory Factory { get; }

    /// <summary>The host's clock: the system clock until <see cref="ShiftableTimeProvider.Advance"/> moves it.</summary>
    public ShiftableTimeProvider Time { get; } = new();

    public Task<HttpClient> AdminAsync() => Factory.AdminAsync();

    public async Task<long> UserIdAsync(string userName) =>
        await DbAsync(db => db.Users.Where(u => u.NormalizedUserName == userName.ToUpperInvariant()).Select(u => u.Id).SingleAsync());

    /// <summary>An admin allows <paramref name="tracker"/> (current consent; MyAnimeList with a client id and redirect URL).</summary>
    public async Task AllowAsync(string tracker)
    {
        var admin = await AdminAsync();
        var response = await admin.PutAsJsonAsync($"/api/v1/admin/trackers/{tracker}", new UpdateTrackerSettingsRequest
        {
            Enabled = true,
            AcceptedConsentVersion = TrackerConsent.AdminVersion(tracker),
            ClientId = tracker == TrackerSlugs.MyAnimeList ? "synthetic-client" : null,
            RedirectUri = tracker == TrackerSlugs.MyAnimeList ? RedirectUri : null,
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    public async Task ConnectMangaUpdatesAsync(HttpClient client, string password = "PW-SECRET-synthetic")
    {
        var response = await client.PostAsJsonAsync("/api/v1/trackers/mangaupdates/connect/password", new TrackerConnectPasswordRequest
        {
            Username = "synthetic-reader",
            Password = password,
            AcceptedConsentVersion = TrackerConsent.UserVersion(TrackerSlugs.MangaUpdates),
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Starts a MyAnimeList sign-in and returns the <c>state</c> and code challenge the authorize URL carries.</summary>
    public static async Task<(string State, string Challenge)> StartMalAsync(HttpClient client)
    {
        var response = await client.PostAsJsonAsync("/api/v1/trackers/mal/connect/start",
            new TrackerConnectStartRequest { AcceptedConsentVersion = TrackerConsent.UserVersion(TrackerSlugs.MyAnimeList) });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var url = new Uri((await response.Content.ReadFromJsonAsync<TrackerConnectStartResponse>(TestJson.Web))!.AuthorizeUrl);
        var query = System.Web.HttpUtility.ParseQueryString(url.Query);
        return (query["state"]!, query["code_challenge"]!);
    }

    public static Task<HttpResponseMessage> FinishMalAsync(HttpClient client, string state, string code = "synthetic-code") =>
        client.PostAsJsonAsync("/api/v1/trackers/mal/connect", new TrackerConnectFinishRequest { Code = code, State = state });

    public async Task ConnectMalAsync(HttpClient client)
    {
        var (state, _) = await StartMalAsync(client);
        Assert.Equal(HttpStatusCode.OK, (await FinishMalAsync(client, state)).StatusCode);
    }

    /// <summary>
    /// A library with a series folder of chapter archives <c>Chapter 001</c>..<c>Chapter {chapters:000}</c> (or <paramref name="names"/>),
    /// linked (Confirmed) to a MangaUpdates record, with a MangaDex companion naming a MyAnimeList id. <paramref name="anchorFirstArchive"/>:
    /// the link is on the first archive instead (an archive linked as its own series; the folder stays unlinked).
    /// </summary>
    public async Task<TrackerSeriesSeed> SeedSeriesAsync(int chapters = 10, MetadataFormat? format = null,
        MetadataOriginStatus status = MetadataOriginStatus.Ongoing, IReadOnlyList<string>? names = null, bool anchorFirstArchive = false)
    {
        if (names is not null)
            chapters = names.Count;
        var n = Interlocked.Increment(ref s_next);
        return await DbAsync(async db =>
        {
            var library = new LibraryEntity { PublicId = $"trk-lib-{n}", DisplayName = $"Tracker Library {n}", RootPath = $"/synthetic/trk-{n}", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
            var folder = Node(library.Id, null, CatalogNodeKind.Folder, $"trk-folder-{n}", "Synthetic Saga");
            db.CatalogNodes.Add(folder);
            await db.SaveChangesAsync();
            var archives = new List<(long, string)>();
            for (var c = 1; c <= chapters; c++)
            {
                var archive = Node(library.Id, folder.Id, CatalogNodeKind.Archive, $"trk-{n}-c{c}", names?[c - 1] ?? $"Chapter {c:000}");
                db.CatalogNodes.Add(archive);
                await db.SaveChangesAsync();
                db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = archive.Id, ContentVersion = 1, AnalysisState = 0, PageCount = 10, ByteLength = 1024 });
                archives.Add((archive.Id, archive.PublicId));
            }
            var muId = (90000 + n).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var malId = (70000 + n).ToString(System.Globalization.CultureInfo.InvariantCulture);
            var record = new MetadataRecordEntity
            {
                PublicId = $"trk-rec-{n}",
                Provider = "mangaupdates",
                ExternalId = muId,
                Title = "Synthetic Saga",
                Format = (int?)format,
                OriginStatus = (int)status,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            var mangadex = new MetadataRecordEntity
            {
                PublicId = $"trk-mdx-{n}",
                Provider = "mangadex",
                ExternalId = Guid.NewGuid().ToString(),
                Title = "Synthetic Saga",
                CrossIdsJson = $"{{\"mal\":\"{malId}\"}}",
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.AddRange(record, mangadex);
            await db.SaveChangesAsync();
            db.MetadataCompanions.Add(new MetadataCompanionEntity
            {
                RecordId = record.Id,
                Provider = "mangadex",
                CompanionRecordId = mangadex.Id,
                State = (int)CompanionState.Auto,
            });
            // 1.43.0 (one-shot): the first archive linked as its own series inside an unlinked folder (an archive anchor).
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = anchorFirstArchive ? archives[0].Item1 : folder.Id,
                LibraryId = library.Id,
                State = (int)SeriesLinkState.Confirmed,
                RecordId = record.Id,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return new TrackerSeriesSeed(library.Id, library.PublicId, folder.Id, folder.PublicId, record.Id, record.PublicId, muId, malId, archives);
        });
    }

    private static CatalogNodeEntity Node(long libraryId, long? parentId, CatalogNodeKind kind, string publicId, string name) => new()
    {
        PublicId = publicId,
        LibraryId = libraryId,
        ParentId = parentId,
        Kind = (int)kind,
        DisplayName = name,
        RelativePath = publicId,
        PathKey = publicId,
        SortKey = SortKey.ForNode(kind, name),
        Availability = (int)CatalogNodeAvailability.Available,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>Waits until the relay has processed every read-mark change enqueued so far.</summary>
    public Task IdleAsync() => Factory.Services.GetRequiredService<TrackerRelay>().WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

    public async Task<T> DbAsync<T>(Func<MangaPixerDbContext, Task<T>> action)
    {
        using var scope = Factory.Services.CreateScope();
        return await action(scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>());
    }

    public async Task<TrackerEntryEntity?> EntryAsync(long userId, string tracker, long recordId) =>
        await DbAsync(db => db.TrackerEntries.AsNoTracking().FirstOrDefaultAsync(e => e.UserId == userId && e.Tracker == tracker && e.RecordId == recordId));

    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}
