namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Globalization;
using System.Net;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

/// <summary>
/// Artists' other names (1.38.0, lane U) - unit + service-with-DB tests over a migrated SQLite database and the REAL gateway, whose
/// MangaUpdates client ends in the scripted handler (recorded author records, no network):
/// - the request: <c>GET /v1/authors/{id}</c> with the numeric id only and the fixed User-Agent; the names mapping and its caps;
/// - eligibility: ids only from creators on stored MangaUpdates records linked (Confirmed / Auto / Collection about) in a library whose
///   Fetch is on - never a name, never another record;
/// - the run: one request per second on the injected clock, stops on budget / backoff / a switch turned off (the late answer is
///   dropped), stores ok / not found / failed, resumes where it stopped, asks again after 180 days;
/// - the alias source answers from the stored rows; "Delete fetched web data" removes them.
/// </summary>
public sealed class AuthorAliasLookupTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;
    private GatewayHarness _h = null!;
    private SteppingTime _clock = null!;

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new GatewayHarness(_t);
        _clock = new SteppingTime(_h.Time.Now);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private AuthorAliasLookupService Service() => new(_t.Db, _h.Gateway(), _h.Registry, _h.Budget(), _h.Backoff(), _clock,
        _h.LoggerFactory.CreateLogger<AuthorAliasLookupService>());

    private static string Creators(params (string Name, string Role, long? Id)[] creators) =>
        MetadataJson.WriteList(creators.Select(c => new MetadataJson.Creator(c.Name, c.Role, c.Id?.ToString(CultureInfo.InvariantCulture))).ToList())!;

    /// <summary>A stored MangaUpdates record naming the given creators, linked to a new folder of <paramref name="libraryId"/>.</summary>
    private async Task<MetadataRecordEntity> LinkedRecordAsync(string externalId, string creatorsJson, SeriesLinkState state = SeriesLinkState.Confirmed,
        long? libraryId = null, string provider = "mangaupdates")
    {
        var record = await _t.AddRecordAsync(externalId, "Synthetic " + externalId);
        record.CreatorsJson = creatorsJson;
        record.Provider = provider;
        await _t.Db.SaveChangesAsync();
        var folder = await _t.AddFolderAsync(null, "Folder " + externalId, libraryId);
        await _t.AddLinkAsync(folder, state is SeriesLinkState.DontMatch or SeriesLinkState.ArtistFolder ? null : record, state);
        return record;
    }

    /// <summary>The Berserk record's creators (Miura as author and artist, Mori, Studio Gaga), linked in the test library.</summary>
    private Task<MetadataRecordEntity> BerserkAsync() => LinkedRecordAsync(MuFixtures.BerserkId.ToString(CultureInfo.InvariantCulture), Creators(
        ("MIURA Kentaro", "author", MuFixtures.MiuraId), ("MORI Kouji", "author", MuFixtures.MoriId),
        ("MIURA Kentaro", "artist", MuFixtures.MiuraId), ("Studio Gaga", "artist", MuFixtures.StudioGagaId)));

    private async Task<AuthorAliasRunDto> RunAsync()
    {
        var service = Service();
        var plan = await service.PlanAsync(CancellationToken.None);
        var state = new AuthorAliasRunState(plan.ToFetch.Count, _clock.GetUtcNow());
        await service.RunAsync(plan.ToFetch, state, CancellationToken.None);
        return state.Snapshot();
    }

    private List<SeenRequest> AuthorRequests() => _h.Handler.Seen.Where(r => r.Uri.AbsolutePath.StartsWith("/v1/authors/", StringComparison.Ordinal)).ToList();

    private Task<List<MetadataAuthorEntity>> RowsAsync() => _t.Db.MetadataAuthors.AsNoTracking().OrderBy(a => a.ExternalId).ToListAsync();

    // --- The request and the mapping (unit, over the real named client) ---

    [Fact]
    public async Task GetAuthor_SendsOnlyTheNumericId_AndTheFixedUserAgent_AndReadsTheNames()
    {
        var author = await _h.Provider.GetAuthorAsync(MuFixtures.MiuraId, CancellationToken.None);

        var seen = Assert.Single(_h.Handler.Seen);
        Assert.Equal(HttpMethod.Get, seen.Method);
        Assert.Equal("https://api.mangaupdates.com/v1/authors/22635311083", seen.Uri.AbsoluteUri);
        Assert.Null(seen.Body);
        Assert.Equal(MetadataHttp.UserAgent, seen.Headers["User-Agent"]);
        Assert.False(seen.Headers.ContainsKey("Cookie"));
        Assert.False(seen.Headers.ContainsKey("Authorization"));
        Assert.False(seen.Headers.ContainsKey("Referer"));

        Assert.NotNull(author);
        Assert.Equal("22635311083", author.ExternalId);
        Assert.Equal("MIURA Kentaro", author.Name);
        // The associated names as recorded, then the name in its own script (only in `actualname`).
        Assert.Equal(["Kentaro Miura", "MIURA Kentarou", "Кентаро Міура", "Миура Кэнтаро", "Міура Кентаро", "केन्तारो मिउरा", "미우라 켄타로", "三浦建太郎"],
            author.OtherNames);
    }

    [Fact]
    public async Task GetAuthor_TheActualNameIsNotRepeated_WhenItIsAlreadyAnAssociatedName()
    {
        var author = await _h.Provider.GetAuthorAsync(MuFixtures.MoriId, CancellationToken.None);

        Assert.Equal("MORI Kouji", author!.Name);
        Assert.Equal(["MORI Koji", "森恒二"], author.OtherNames);
    }

    [Fact]
    public async Task GetAuthor_UnknownId_IsNull()
    {
        Assert.Null(await _h.Provider.GetAuthorAsync(1, CancellationToken.None));
        Assert.Single(_h.Handler.Seen);
    }

    [Fact]
    public void Mapping_CleansCapsAndDeduplicates_AndDropsPlaceholders()
    {
        var many = Enumerable.Range(1, 80).Select(i => new MuAuthorName { Name = "Pen  Name\n" + i }).ToList();
        many.Insert(0, new MuAuthorName { Name = "main NAME" });          // the main name, other case
        many.Insert(1, new MuAuthorName { Name = "pen name 1" });          // a duplicate of "Pen Name 1", other case
        many.Insert(2, new MuAuthorName { Name = new string('x', 400) });  // cut to 256
        var author = MangaUpdatesMapping.ToAuthor(new MuAuthorRecord { Name = " Main\tName ", Associated = many, ActualName = "N/A" }, 42);

        Assert.Equal("42", author.ExternalId);
        Assert.Equal("Main Name", author.Name);
        Assert.Equal(MangaUpdatesMapping.MaxAuthorOtherNames, author.OtherNames.Count);
        Assert.Equal("pen name 1", author.OtherNames[0]); // the first spelling wins; "Pen  Name\n1" later is the same name
        Assert.Equal(new string('x', 256), author.OtherNames[1]);
        Assert.Equal("Pen Name 2", author.OtherNames[2]);
        Assert.DoesNotContain(author.OtherNames, n => n.Equals("Main Name", StringComparison.OrdinalIgnoreCase) || n == "N/A");
        Assert.Equal(author.OtherNames.Count, author.OtherNames.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        Assert.Throws<MetadataResponseInvalidException>(() => MangaUpdatesMapping.ToAuthor(new MuAuthorRecord { Name = "  " }, 7));
    }

    // --- Eligibility: ids only from stored, linked MangaUpdates records in libraries whose Fetch is on ---

    [Fact]
    public async Task Plan_TakesIdsOnlyFromCreatorsOfLinkedRecords_InLibrariesWhoseFetchIsOn()
    {
        var other = await _t.AddLibraryAsync("otherlib", "Other Lib");
        await _h.EnableAsync(); // the test library on; "otherlib" stays off
        await LinkedRecordAsync("1001", Creators(("Confirmed Author", "author", 11), ("No Id Artist", "artist", null)));
        await LinkedRecordAsync("1002", Creators(("Auto Author", "author", 12)), SeriesLinkState.Auto);
        await LinkedRecordAsync("1003", Creators(("Collection Author", "author", 13)), SeriesLinkState.CollectionAbout);
        await LinkedRecordAsync("1004", Creators(("Review Author", "author", 14)), SeriesLinkState.NeedsReview);
        await LinkedRecordAsync("1005", Creators(("Off Library Author", "author", 15)), libraryId: other.Id);
        await LinkedRecordAsync("1006", Creators(("Gcd Author", "author", 16)), provider: "gcd");
        await LinkedRecordAsync("1007", """[{"name":"Odd Id","role":"author","providerId":"12ab"},{"name":"Zero","role":"author","providerId":"0"}]""");
        // A stored record nobody links (previewed only).
        var unlinked = await _t.AddRecordAsync("1008", "Unlinked");
        unlinked.CreatorsJson = Creators(("Unlinked Author", "author", 18));
        await _t.Db.SaveChangesAsync();

        var plan = await Service().PlanAsync(CancellationToken.None);

        Assert.Equal(["11", "12", "13"], plan.ToFetch.Select(t => t.AuthorId).ToArray());
        Assert.All(plan.ToFetch, t => Assert.Equal(_t.LibraryId, t.LibraryId));
        Assert.Equal(3, plan.Eligible);
        Assert.Equal(0, plan.Fetched);
        Assert.Empty(_h.Handler.Seen);
    }

    [Fact]
    public async Task Plan_RequestsEachIdForTheSmallestLibraryThatLinksIt()
    {
        var second = await _t.AddLibraryAsync("secondlib", "Second Lib");
        await _h.EnableAsync();
        await _h.EnableAsync(second.Id);
        await LinkedRecordAsync("2001", Creators(("Shared", "author", 21)), libraryId: second.Id);
        await LinkedRecordAsync("2002", Creators(("Shared", "author", 21), ("Only Second", "artist", 22)), libraryId: _t.LibraryId);

        var plan = await Service().PlanAsync(CancellationToken.None);

        Assert.Equal(2, plan.Eligible);
        Assert.Equal(_t.LibraryId, plan.ToFetch.Single(t => t.AuthorId == "21").LibraryId);
        Assert.Equal(_t.LibraryId, plan.ToFetch.Single(t => t.AuthorId == "22").LibraryId);
    }

    // --- The run ---

    [Fact]
    public async Task Run_StoresNamesAndNotFound_PacedOnePerSecondOnTheInjectedClock()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        var sentAt = new List<DateTimeOffset>();
        _h.Handler.Respond = _ =>
        {
            sentAt.Add(_clock.GetUtcNow());
            return null!; // the recorded fixtures answer
        };

        var run = await RunAsync();

        Assert.Equal(["/v1/authors/22635311083", "/v1/authors/38824888050", "/v1/authors/58953789514"],
            AuthorRequests().Select(r => r.Uri.AbsolutePath).ToArray());
        Assert.All(AuthorRequests(), r => Assert.Null(r.Body));
        Assert.Equal(3, sentAt.Count);
        Assert.True(sentAt[1] - sentAt[0] >= TimeSpan.FromSeconds(1), $"gap {sentAt[1] - sentAt[0]}");
        Assert.True(sentAt[2] - sentAt[1] >= TimeSpan.FromSeconds(1), $"gap {sentAt[2] - sentAt[1]}");
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1)], _clock.Delays);

        Assert.Equal(("completed", 3, 3, 2, 1, 0), (run.Outcome, run.Total, run.Requests, run.Stored, run.NotFound, run.Failed));
        var rows = await RowsAsync();
        Assert.Equal(["22635311083", "38824888050", "58953789514"], rows.Select(r => r.ExternalId).ToArray());
        Assert.Equal((int)MetadataAuthorStatus.Ok, rows[0].Status);
        Assert.Equal("MIURA Kentaro", rows[0].Name);
        Assert.Contains("三浦建太郎", MetadataJson.ReadList<string>(rows[0].OtherNamesJson));
        Assert.Equal((int)MetadataAuthorStatus.NotFound, rows[2].Status);
        Assert.Null(rows[2].Name);
        Assert.Equal(3, (await _h.Budget().GetAsync()).Used); // the ONE daily budget

        // Done: nothing left to request, and nothing is requested again.
        Assert.Empty((await Service().PlanAsync(CancellationToken.None)).ToFetch);
        Assert.Equal(3, (await Service().PlanAsync(CancellationToken.None)).Fetched);
    }

    [Fact]
    public async Task Run_DoesNotNeedTheAutomaticConsent()
    {
        await _h.EnableAsync(); // Automatic matching stays off, its consent never given
        await BerserkAsync();

        var run = await RunAsync();

        Assert.Equal("completed", run.Outcome);
        Assert.Equal(3, AuthorRequests().Count);
    }

    [Fact]
    public async Task Run_StopsWhenTheBudgetIsSpent_AndTheNextRunResumesThere()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        var settings = await _t.Db.AppSettings.FirstAsync();
        settings.MetadataDailyBudget = 2;
        await _t.Db.SaveChangesAsync();

        var run = await RunAsync();

        Assert.Equal(("budget_exhausted", 2), (run.Outcome, run.Requests));
        Assert.Equal(2, AuthorRequests().Count);
        Assert.Equal(2, (await RowsAsync()).Count);
        var left = await Service().PlanAsync(CancellationToken.None);
        Assert.Equal(["58953789514"], left.ToFetch.Select(t => t.AuthorId).ToArray());
        Assert.Equal("budget_exhausted", (await Service().BlockedAsync(left, CancellationToken.None))?.Code);

        // A new day: the next run requests only the id it did not reach.
        _h.Time.Advance(TimeSpan.FromDays(1));
        _clock.Advance(TimeSpan.FromDays(1));
        var resumed = await RunAsync();
        Assert.Equal(("completed", 1), (resumed.Outcome, resumed.Requests));
        Assert.Equal("/v1/authors/58953789514", AuthorRequests()[^1].Uri.AbsolutePath);
    }

    [Fact]
    public async Task Run_StopsOnAProviderBackoff_WithoutStoringTheId()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        _h.Handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);

        var run = await RunAsync();

        Assert.Equal(("provider_backoff", 1), (run.Outcome, run.Requests));
        Assert.NotNull(run.RetryAt);
        Assert.Single(AuthorRequests());
        Assert.Empty(await RowsAsync());
        Assert.Equal("provider_backoff", (await Service().BlockedAsync(await Service().PlanAsync(CancellationToken.None), CancellationToken.None))?.Code);
    }

    [Fact]
    public async Task Run_StopsWhenFetchIsTurnedOffDuringARequest_AndDropsTheLateAnswer()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        _h.Handler.Respond = _ =>
        {
            // The admin turns "Fetch from the web" off while the first request is out.
            _t.Db.AppSettings.ExecuteUpdate(s => s.SetProperty(x => x.MetadataEnabled, false));
            return null!;
        };

        var run = await RunAsync();

        Assert.Equal(("switched_off", 1), (run.Outcome, run.Requests));
        Assert.Single(AuthorRequests());
        Assert.Empty(await RowsAsync());
    }

    [Fact]
    public async Task Run_WithFetchOff_OrMangaUpdatesRemoved_SendsNothing()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        var settings = await _t.Db.AppSettings.FirstAsync();
        settings.MetadataProvidersJson = MetadataProviderAllowlist.Write(["mangaupdates"]);
        await _t.Db.SaveChangesAsync();

        var removed = await RunAsync();
        Assert.Equal(("switched_off", 0), (removed.Outcome, removed.Requests));
        Assert.Equal("provider_not_allowed", (await Service().BlockedAsync(await Service().PlanAsync(CancellationToken.None), CancellationToken.None))?.Code);

        settings.MetadataProvidersJson = null;
        settings.MetadataEnabled = false;
        await _t.Db.SaveChangesAsync();
        var off = await RunAsync();
        // "Fetch from the web" off: refused before any request.
        Assert.Equal(("switched_off", 0), (off.Outcome, off.Requests));
        Assert.Empty(_h.Handler.Seen);
    }

    [Fact]
    public async Task Run_AnUnreadableAnswer_IsStoredAsFailed_AndAskedAgainNextTime()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        _h.Handler.Respond = r => r.RequestUri!.AbsolutePath.EndsWith("/38824888050", StringComparison.Ordinal)
            ? ScriptedHandler.Json("not json")
            : null!;

        var run = await RunAsync();

        Assert.Equal(("completed", 3, 1, 1, 1), (run.Outcome, run.Requests, run.Stored, run.NotFound, run.Failed));
        Assert.Equal((int)MetadataAuthorStatus.Failed, (await RowsAsync()).Single(r => r.ExternalId == "38824888050").Status);
        _h.Handler.Respond = null;
        Assert.Equal(["38824888050"], (await Service().PlanAsync(CancellationToken.None)).ToFetch.Select(t => t.AuthorId).ToArray());
    }

    [Fact]
    public async Task Run_Cancelled_StopsBeforeTheNextRequest()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        using var cts = new CancellationTokenSource();
        _h.Handler.Respond = _ =>
        {
            cts.Cancel(); // the admin presses Cancel while the first request is out
            return null!;
        };

        var service = Service();
        var plan = await service.PlanAsync(CancellationToken.None);
        var state = new AuthorAliasRunState(plan.ToFetch.Count, _clock.GetUtcNow());
        await service.RunAsync(plan.ToFetch, state, cts.Token);

        Assert.Equal("cancelled", state.Snapshot().Outcome);
        Assert.Single(AuthorRequests());
    }

    [Fact]
    public async Task Plan_AsksAgain_OnlyAfter180Days()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        await RunAsync();

        _clock.Advance(TimeSpan.FromDays(179));
        Assert.Empty((await Service().PlanAsync(CancellationToken.None)).ToFetch);
        _clock.Advance(TimeSpan.FromDays(2));
        var plan = await Service().PlanAsync(CancellationToken.None);
        Assert.Equal(3, plan.ToFetch.Count);
        Assert.Equal(0, plan.Fetched);
    }

    [Fact]
    public async Task AFailedRecheck_KeepsTheNamesReadBefore()
    {
        var first = new ProviderAuthorRecord("77", "Some Artist", ["Pen Name"]);
        await MetadataAuthorStore.SaveAsync(_t.Db, "77", MetadataAuthorStatus.Ok, first, _clock.GetUtcNow(), CancellationToken.None);
        await MetadataAuthorStore.SaveAsync(_t.Db, "77", MetadataAuthorStatus.Failed, null, _clock.GetUtcNow(), CancellationToken.None);

        var found = await new StoredAuthorAliases(_t.Db).GetAsync(["77"], CancellationToken.None);
        Assert.Equal(["Pen Name"], found["77"].OtherNames);
        Assert.Equal((int)MetadataAuthorStatus.Failed, (await RowsAsync()).Single().Status);
    }

    // --- The seam and the purge ---

    [Fact]
    public async Task AliasSource_AnswersStoredAuthors_AndLeavesOutUnknownAndNotFoundIds()
    {
        await _h.EnableAsync();
        await BerserkAsync();
        await RunAsync();

        IAuthorAliasSource source = new StoredAuthorAliases(_t.Db);
        var found = await source.GetAsync(["22635311083", "38824888050", "58953789514", "999"], CancellationToken.None);

        Assert.Equal(["22635311083", "38824888050"], found.Keys.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(("38824888050", "MORI Kouji"), (found["38824888050"].AuthorId, found["38824888050"].Name));
        Assert.Equal(["MORI Koji", "森恒二"], found["38824888050"].OtherNames);
        Assert.Empty(await source.GetAsync([], CancellationToken.None));
        Assert.Equal(3, AuthorRequests().Count); // reading the store sends nothing
    }

    [Fact]
    public async Task Purge_Global_RemovesEveryAuthor_PerLibrary_OnlyTheUnreferenced()
    {
        var other = await _t.AddLibraryAsync("purgelib", "Purge Lib");
        var keep = await LinkedRecordAsync("3001", Creators(("Kept", "author", 31)), libraryId: other.Id);
        await LinkedRecordAsync("3002", Creators(("Gone", "author", 32), ("Kept", "author", 31)));
        foreach (var id in new[] { "31", "32" })
            await MetadataAuthorStore.SaveAsync(_t.Db, id, MetadataAuthorStatus.Ok, new ProviderAuthorRecord(id, "Name " + id, []), _clock.GetUtcNow(), CancellationToken.None);

        var (code, _) = await _h.Links().PurgeAsync(_t.LibraryPublicId, "admin");
        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(["31"], (await RowsAsync()).Select(r => r.ExternalId).ToArray());
        Assert.NotNull(await _t.Db.MetadataRecords.FindAsync(keep.Id));

        await _h.Links().PurgeAsync(null, "admin");
        Assert.Empty(await RowsAsync());
    }
}

/// <summary>
/// A clock whose timers fire at once and move the clock forward by their due time (so a paced run takes no real time), recording
/// every delay asked for.
/// </summary>
public sealed class SteppingTime : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _now;

    public SteppingTime(DateTimeOffset now) => _now = now;

    public List<TimeSpan> Delays { get; } = [];

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public void Advance(TimeSpan by)
    {
        lock (_gate)
            _now += by;
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        lock (_gate)
        {
            Delays.Add(dueTime);
            _now += dueTime;
        }
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        return new NoTimer();
    }

    private sealed class NoTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
