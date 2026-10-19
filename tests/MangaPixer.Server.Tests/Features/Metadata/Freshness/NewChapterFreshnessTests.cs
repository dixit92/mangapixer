namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Freshness;

using System.Net;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaDex;
using com.lifepixer.mangapixer.Server.Features.Metadata.Providers.MangaUpdates;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata.Volumes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

/// <summary>
/// Service-with-DB tests of the 1.45.0 new-chapter freshness (lane R): the MangaDex latest-upload sweep (exactly what it sends, what it
/// stores, the cross-trigger, the library confirmation and what stops it), the export contract MangaList reads (the item changes only
/// when the chapter id does; the page envelope's sweeps), MangaUpdates' releases feed (fixed parameters, the local intersection, the
/// nudged refresh), the MangaUpdates -> MangaDex cross-trigger, the reading fast lane and the readable backfill. Synthetic ids, fake
/// HTTP handlers only.
/// </summary>
[Trait("Category", "ServiceDb")]
public sealed class NewChapterFreshnessTests : IAsyncLifetime
{
    private const string MdA = "00000000-0000-4000-8000-0000000000a1";
    private const string MdB = "00000000-0000-4000-8000-0000000000b1";
    private const string ChapterA1 = "10000000-0000-4000-8000-0000000000a1";
    private const string ChapterA2 = "10000000-0000-4000-8000-0000000000a2";
    private const string ChapterB1 = "10000000-0000-4000-8000-0000000000b1";

    private MetadataTestDb _t = null!;
    private VolumePassHarness _h = null!;

    /// <summary>MangaDex's latestUploadedChapter by manga id (an id missing here is not returned at all).</summary>
    private readonly Dictionary<string, string?> _latest = new(StringComparer.Ordinal);

    /// <summary>MangaUpdates' releases list, page by page: (series id, chapter text).</summary>
    private readonly List<(long Series, string? Chapter, string Title)[]> _releases = [];

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new VolumePassHarness(_t);
        _h.Override = Route;
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private HttpResponseMessage? Route(HttpRequestMessage request)
    {
        var uri = request.RequestUri!;
        if (uri.Host == MetadataHttp.MangaDexApiHost && uri.AbsolutePath == "/manga" && uri.Query.Contains("ids%5B%5D=", StringComparison.Ordinal))
        {
            var ids = Uri.UnescapeDataString(uri.Query).TrimStart('?').Split('&').Where(p => p.StartsWith("ids[]=", StringComparison.Ordinal))
                .Select(p => p["ids[]=".Length..]).ToList();
            var data = ids.Where(_latest.ContainsKey).Select(id => new
            {
                id,
                type = "manga",
                attributes = new { title = new { en = "Synthetic" }, latestUploadedChapter = _latest[id] },
                relationships = Array.Empty<object>(),
            });
            return ScriptedHandler.Json(JsonSerializer.Serialize(new { result = "ok", response = "collection", data, limit = 100, offset = 0, total = ids.Count }));
        }
        if (uri.Host == MetadataHttp.MangaUpdatesApiHost && uri.AbsolutePath == "/v1/releases/days")
        {
            var page = int.Parse(System.Web.HttpUtility.ParseQueryString(uri.Query)["page"]!, System.Globalization.CultureInfo.InvariantCulture);
            var rows = page <= _releases.Count ? _releases[page - 1] : [];
            return ScriptedHandler.Json(JsonSerializer.Serialize(new
            {
                total_hits = _releases.Sum(r => r.Length),
                page,
                per_page = 2,
                results = rows.Select(r => new
                {
                    record = new { id = 1, title = r.Title, volume = (string?)null, chapter = r.Chapter, release_date = "2026-09-25" },
                    metadata = new { series = new { series_id = r.Series, title = r.Title } },
                }),
            }));
        }
        return null;
    }

    private DateTimeOffset Now => _h.Time.GetUtcNow();

    private ILogger<T> Log<T>() => _h.Auto.Net.LoggerFactory.CreateLogger<T>();

    private LatestUploadSweep Sweep() => new(_t.Db, _h.Auto.Net.Gateway(), new MangaDexProvider(_h.Auto.Net.HttpFactory), _h.Auto.Service(), _h.Time,
        Log<LatestUploadSweep>());

    private ReleasesFeedPoll Feed() => new(_t.Db, _h.Auto.Net.Gateway(), new MangaUpdatesReleasesFeed(_h.Auto.Net.HttpFactory), _h.Auto.Service(),
        _h.Time, Log<ReleasesFeedPoll>());

    private ExportService Export() => new(_t.Db,
        new ExportRebuildService(_t.Db, new ExportItemBuilder(_t.Db), new ExportRebuildGate(), new ExportOptions(TimeSpan.Zero), _h.Time,
            NullLogger<ExportRebuildService>.Instance), _h.Time);

    private sealed record Series(CatalogNodeEntity Folder, MetadataRecordEntity Record, MetadataRecordEntity MangaDex);

    /// <summary>A folder linked Confirmed to a MangaUpdates record whose MangaDex companion is <paramref name="mangaId"/>, with an Ok volume list.</summary>
    private async Task<Series> SeriesAsync(string muId, string mangaId, long? libraryId = null)
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Shelf " + muId, libraryId);
        await _t.AddArchiveAsync(folder, $"Synthetic Shelf {muId} c001.cbz");
        var record = await _t.AddRecordAsync(muId, "Synthetic Series " + muId);
        record.FetchedAt = Now - TimeSpan.FromDays(1);
        record.LatestChapter = 30;
        var md = new MetadataRecordEntity
        {
            PublicId = "md" + muId,
            Provider = MetadataProviderAllowlist.MangaDex,
            ExternalId = mangaId,
            Title = "Synthetic Series " + muId,
            FetchedAt = Now - TimeSpan.FromDays(1),
        };
        _t.Db.MetadataRecords.Add(md);
        await _t.Db.SaveChangesAsync();
        _t.Db.MetadataCompanions.Add(new MetadataCompanionEntity
        {
            RecordId = record.Id,
            Provider = MetadataProviderAllowlist.MangaDex,
            CompanionRecordId = md.Id,
            State = (int)CompanionState.Auto,
            Method = (int)CompanionMethod.CrossLink,
            CheckedAt = Now - TimeSpan.FromDays(1),
            NextCheckAt = Now + TimeSpan.FromDays(20),
        });
        _t.Db.SeriesVolumeMaps.Add(new SeriesVolumeMapEntity
        {
            RecordId = record.Id,
            Source = (int)VolumeMapSource.MangaDexAggregate,
            State = (int)VolumeMapState.Ok,
            VolumesJson = "[{\"v\":\"1\",\"c\":[\"1\",\"2\"]},{\"v\":\"2\",\"c\":[\"3\",\"4\"]}]",
            ContentHash = "synthetic",
            Version = 1,
            FetchedAt = Now - TimeSpan.FromDays(1),
            NextCheckAt = Now + TimeSpan.FromDays(20),
        });
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record, SeriesLinkState.Confirmed);
        return new Series(folder, record, md);
    }

    private async Task<DateTimeOffset?> MapNextCheckAsync(MetadataRecordEntity record)
    {
        _t.Db.ChangeTracker.Clear();
        return await _t.Db.SeriesVolumeMaps.AsNoTracking().Where(m => m.RecordId == record.Id).Select(m => m.NextCheckAt).SingleAsync();
    }

    private async Task<MangaDexLatestUploadEntity> UploadAsync(MetadataRecordEntity md)
    {
        _t.Db.ChangeTracker.Clear();
        return await _t.Db.MangaDexLatestUploads.AsNoTracking().SingleAsync(u => u.RecordId == md.Id);
    }

    private IReadOnlyList<Uri> SweepRequests() =>
        _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaDexApiHost && s.Uri.AbsolutePath == "/manga").Select(s => s.Uri).ToList();

    // --- The latest-upload sweep ---

    [Fact]
    public async Task Sweep_SendsOnlyIdsLimitAndEveryRating_StoresTheFirstSeenValue_AndOnlyAChangeMakesTheListsDue()
    {
        await _h.Auto.EnableAutomaticAsync();
        var a = await SeriesAsync("701", MdA);
        var b = await SeriesAsync("702", MdB);
        _latest[MdA] = ChapterA1;
        _latest[MdB] = ChapterB1;
        var t0 = Now;
        var listsDueAt = await MapNextCheckAsync(a.Record);

        var first = await Sweep().RunAsync();

        Assert.Equal(new LatestUploadSweepResult(1, 2, 0, 1, null), first);
        var sent = Assert.Single(SweepRequests());
        var query = Uri.UnescapeDataString(sent.Query).TrimStart('?').Split('&').Select(p => p.Split('=', 2)).ToList();
        // Exactly the ids, the limit and all four content ratings - nothing else.
        Assert.Equal(["ids[]", "ids[]", "limit", "contentRating[]", "contentRating[]", "contentRating[]", "contentRating[]"], query.Select(p => p[0]));
        Assert.Equal([MdA, MdB], query.Where(p => p[0] == "ids[]").Select(p => p[1]).Order(StringComparer.Ordinal));
        Assert.Equal("100", query.Single(p => p[0] == "limit")[1]);
        Assert.Equal(["safe", "suggestive", "erotica", "pornographic"], query.Where(p => p[0] == "contentRating[]").Select(p => p[1]));
        var seen = _h.Handler.Seen.Single(s => s.Uri == sent);
        Assert.Equal(MetadataHttp.UserAgent, seen.Headers["User-Agent"]);
        Assert.False(seen.Headers.ContainsKey("Via"));
        Assert.Equal((ChapterA1, t0, t0), ((await UploadAsync(a.MangaDex)).ChapterId, (await UploadAsync(a.MangaDex)).SeenAt, (await UploadAsync(a.MangaDex)).CheckedAt));
        Assert.Equal(listsDueAt, await MapNextCheckAsync(a.Record)); // a first read is not a change
        Assert.Equal(t0, (await _t.Db.LibraryFreshness.AsNoTracking().SingleAsync(f => f.LibraryId == _t.LibraryId)).MangaDexSweepAt);

        // The same value again: only the check time moves.
        _h.Time.Advance(TimeSpan.FromHours(6));
        Assert.Equal(0, (await Sweep().RunAsync())!.Changed);
        var confirmed = await UploadAsync(a.MangaDex);
        Assert.Equal((ChapterA1, t0, Now), (confirmed.ChapterId, confirmed.SeenAt, confirmed.CheckedAt));
        Assert.Equal(listsDueAt, await MapNextCheckAsync(a.Record));

        // A new upload: the value and its first-seen time change, and that series' MangaDex volume lists are due now (recommendation 5).
        _h.Time.Advance(TimeSpan.FromHours(6));
        _latest[MdA] = ChapterA2;
        var third = await Sweep().RunAsync();
        Assert.Equal((2, 1), (third!.Records, third.Changed));
        Assert.Equal((ChapterA2, Now), ((await UploadAsync(a.MangaDex)).ChapterId, (await UploadAsync(a.MangaDex)).SeenAt));
        Assert.Equal(Now, await MapNextCheckAsync(a.Record));
        Assert.Equal(listsDueAt, await MapNextCheckAsync(b.Record));
        // Logs: ids and counts only.
        Assert.DoesNotContain(_h.Auto.Net.Logs.Lines, l => l.Contains("Synthetic", StringComparison.Ordinal) || l.Contains(MdA, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Sweep_NoChapterIsStoredAsNull_AndARecordMangaDexDoesNotReturnKeepsItsValue()
    {
        await _h.Auto.EnableAutomaticAsync();
        var a = await SeriesAsync("703", MdA);
        var b = await SeriesAsync("704", MdB);
        _latest[MdA] = null;
        _latest[MdB] = ChapterB1;
        await Sweep().RunAsync();
        Assert.Null((await UploadAsync(a.MangaDex)).ChapterId);

        _latest.Remove(MdB); // taken down on MangaDex: not returned
        _h.Time.Advance(TimeSpan.FromHours(6));
        var result = await Sweep().RunAsync();
        Assert.Equal((1, null), (result!.Records, result.StoppedCode));
        Assert.Equal(ChapterB1, (await UploadAsync(b.MangaDex)).ChapterId);
    }

    [Fact]
    public async Task Sweep_AndFeed_WaitWhileNothingIsLinked()
    {
        await _h.Auto.EnableAutomaticAsync();
        Assert.Null(await Sweep().RunAsync());
        Assert.Null(await Feed().RunAsync());
        Assert.Empty(_h.Handler.Seen);
        Assert.Null(await NewChapterChecks.PeekAsync(_t.Db, default)); // the first linked series is looked at on the next tick
    }

    [Fact]
    public async Task Sweep_IsDueOnTheAdminsInterval_1To6Hours_AndRetriedSoonAfterAStop()
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync("705", MdA);
        _latest[MdA] = ChapterA1;
        Assert.True(await Sweep().IsDueAsync());
        await Sweep().RunAsync();
        Assert.False(await Sweep().IsDueAsync());
        _h.Time.Advance(TimeSpan.FromHours(5));
        Assert.False(await Sweep().IsDueAsync());
        _h.Time.Advance(TimeSpan.FromHours(1));
        Assert.True(await Sweep().IsDueAsync()); // default 6 h

        var state = await NewChapterChecks.StateAsync(_t.Db, default);
        state.CheckHours = 1;
        state.SweepStartedAt = Now;
        await _t.Db.SaveChangesAsync();
        Assert.False(await Sweep().IsDueAsync());
        _h.Time.Advance(TimeSpan.FromHours(1));
        Assert.True(await Sweep().IsDueAsync());

        state.SweepStartedAt = Now;
        state.SweepOutcome = "budget_exhausted";
        await _t.Db.SaveChangesAsync();
        _h.Time.Advance(NewChapterChecks.RetryAfter);
        Assert.True(await Sweep().IsDueAsync());
        Assert.Equal(6, NewChapterChecks.HoursOf(null));
        Assert.Equal(6, NewChapterChecks.HoursOf(7));
        Assert.Equal(3, NewChapterChecks.HoursOf(3));
    }

    [Theory]
    [InlineData("consent")]
    [InlineData("automatic")]
    [InlineData("allowlist")]
    public async Task Sweep_AndFeed_SendNothing_WithoutTheCurrentConsentOrAutomaticMatching(string closed)
    {
        await _h.Auto.EnableAutomaticAsync();
        await SeriesAsync("706", MdA);
        _latest[MdA] = ChapterA1;
        var row = await _t.Db.AppSettings.FirstAsync();
        switch (closed)
        {
            case "consent": row.MetadataConsentVersion = MetadataConsent.CurrentVersion - 1; break; // an instance on the previous consent
            case "automatic": row.MetadataAutoMatchEnabled = false; break;
        }
        await _t.Db.SaveChangesAsync();
        if (closed == "allowlist")
        {
            Assert.Null(await _h.Auto.Net.Settings().UpdateAsync(new com.lifepixer.mangapixer.Core.Api.UpdateMetadataSettingsRequest
            {
                RemovedProviders = [MetadataProviderAllowlist.MangaDex, MetadataProviderAllowlist.MangaUpdates],
            }, "admin"));
        }

        Assert.Null(await Sweep().RunAsync());
        Assert.Null(await Feed().RunAsync());
        Assert.Empty(_h.Handler.Seen);
        Assert.Null(await NewChapterChecks.PeekAsync(_t.Db, default)); // nothing recorded either: it waits for the gate
    }

    [Fact]
    public async Task Sweep_StoppedByTheBudget_AdvancesNoLibrary_AndALibraryWithFetchOffIsNeitherAskedNorConfirmed()
    {
        var other = await _t.AddLibraryAsync("lib0other", "Other");
        var off = await _t.AddLibraryAsync("lib0off", "Off");
        await _h.Auto.EnableAutomaticAsync();
        await _h.Auto.Net.EnableAsync(other.Id);
        var a = await SeriesAsync("707", MdA);
        await SeriesAsync("708", MdB, other.Id);
        await SeriesAsync("709", "00000000-0000-4000-8000-0000000000c1", off.Id); // "Fetch from the web" off there
        _latest[MdA] = ChapterA1;
        _latest[MdB] = ChapterB1;
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataDailyBudget = 1; // the first library's request fits, the second's does not
        await _t.Db.SaveChangesAsync();

        var stopped = await Sweep().RunAsync();

        Assert.Equal(("budget_exhausted", 1, 0), (stopped!.StoppedCode, stopped.Requests, stopped.Libraries));
        Assert.Equal(ChapterA1, (await UploadAsync(a.MangaDex)).ChapterId); // what was read is kept
        Assert.False(await _t.Db.LibraryFreshness.AnyAsync(f => f.MangaDexSweepAt != null));
        Assert.Equal("budget_exhausted", (await NewChapterChecks.PeekAsync(_t.Db, default))!.SweepOutcome);

        // The next budget day: the sweep completes and confirms the libraries whose fetching is on - not the one with it off.
        _h.Time.Advance(TimeSpan.FromDays(1));
        _h.ResetRequests();
        var done = await Sweep().RunAsync();
        Assert.Equal((null, 2, 2), (done!.StoppedCode, done.Requests, done.Libraries));
        Assert.DoesNotContain(SweepRequests(), u => u.Query.Contains("0000000000c1", StringComparison.Ordinal));
        _t.Db.ChangeTracker.Clear();
        var confirmedLibraries = await _t.Db.LibraryFreshness.AsNoTracking().Where(f => f.MangaDexSweepAt == Now).Select(f => f.LibraryId).ToListAsync();
        Assert.Equal([_t.LibraryId, other.Id], confirmedLibraries.Order());
    }

    [Fact]
    public async Task Sweep_BatchesAHundredIdsPerRequest()
    {
        await _h.Auto.EnableAutomaticAsync();
        for (var i = 0; i < 101; i++)
        {
            var id = $"00000000-0000-4000-8000-{i:D12}";
            await SeriesAsync((800 + i).ToString(System.Globalization.CultureInfo.InvariantCulture), id);
            _latest[id] = ChapterA1;
        }
        var result = await Sweep().RunAsync();
        Assert.Equal((2, 101, null), (result!.Requests, result.Records, result.StoppedCode));
        Assert.Equal([100, 1], SweepRequests().Select(u => u.Query.Split("ids%5B%5D=").Length - 1));
    }

    // --- The export contract (MangaList's shared chapter signal) ---

    [Fact]
    public async Task Export_ASecondSweepWithTheSameChapter_LeavesUpdatedSinceEmpty_AChangedChapterReturnsTheItemOnce()
    {
        await _h.Auto.EnableAutomaticAsync();
        var a = await SeriesAsync("711", MdA);
        _latest[MdA] = ChapterA1;
        var sweptAt = Now;
        await Sweep().RunAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(1));

        var full = await PageAsync(null);
        var item = full.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("nodeId").GetString() == a.Folder.PublicId);
        var block = item.GetProperty("companions").GetProperty("mangadexLatestUpload");
        Assert.Equal(ChapterA1, block.GetProperty("chapterId").GetString());
        Assert.Equal(ExportJson.Format(sweptAt), block.GetProperty("fetchedAt").GetString());
        Assert.Equal(ExportJson.Format(sweptAt), full.GetProperty("sweeps").GetProperty("mangadexLatestUpload").GetString());
        var since = full.GetProperty("serverTime").GetString()!;

        // The same chapter id again (a later sweep): no item changed; only the envelope's confirmation moved.
        _h.Time.Advance(TimeSpan.FromHours(6));
        var secondSweep = Now;
        await Sweep().RunAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        var unchanged = await PageAsync(since);
        Assert.Empty(unchanged.GetProperty("items").EnumerateArray());
        Assert.Equal(ExportJson.Format(secondSweep), unchanged.GetProperty("sweeps").GetProperty("mangadexLatestUpload").GetString());
        since = unchanged.GetProperty("serverTime").GetString()!;

        // A new chapter id: the item comes once, with the new value and when it was first seen.
        _h.Time.Advance(TimeSpan.FromHours(6));
        _latest[MdA] = ChapterA2;
        var changedAt = Now;
        await Sweep().RunAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        var changed = await PageAsync(since);
        var changedItem = Assert.Single(changed.GetProperty("items").EnumerateArray());
        Assert.Equal(a.Folder.PublicId, changedItem.GetProperty("nodeId").GetString());
        var newBlock = changedItem.GetProperty("companions").GetProperty("mangadexLatestUpload");
        Assert.Equal((ChapterA2, ExportJson.Format(changedAt)), (newBlock.GetProperty("chapterId").GetString(), newBlock.GetProperty("fetchedAt").GetString()));
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Empty((await PageAsync(changed.GetProperty("serverTime").GetString())).GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Export_TheBlockIsLeftOutWhenNeverRead_ChapterIdNullWhenMangaDexListsNone_AndSweepsIsTheSameOnEveryPage()
    {
        await _h.Auto.EnableAutomaticAsync();
        var a = await SeriesAsync("712", MdA);
        var b = await SeriesAsync("713", MdB);
        var before = await PageAsync(null);
        Assert.Equal(JsonValueKind.Null, before.GetProperty("sweeps").GetProperty("mangadexLatestUpload").ValueKind);
        Assert.All(before.GetProperty("items").EnumerateArray(), i => Assert.False(i.GetProperty("companions").TryGetProperty("mangadexLatestUpload", out _)));

        _latest[MdA] = null;
        _latest[MdB] = ChapterB1;
        _h.Time.Advance(TimeSpan.FromMinutes(1));
        var sweptAt = Now;
        await Sweep().RunAsync();
        _h.Time.Advance(TimeSpan.FromMinutes(1));

        var first = await PageAsync(null, limit: 1);
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);
        // A sweep that completes between two pages of one call does not change what the call's pages say.
        _h.Time.Advance(TimeSpan.FromHours(6));
        await Sweep().RunAsync();
        var second = await PageAsync(null, limit: 1, cursor: cursor);
        Assert.Equal(ExportJson.Format(sweptAt), first.GetProperty("sweeps").GetProperty("mangadexLatestUpload").GetString());
        Assert.Equal(first.GetProperty("sweeps").GetRawText(), second.GetProperty("sweeps").GetRawText());
        var items = first.GetProperty("items").EnumerateArray().Concat(second.GetProperty("items").EnumerateArray())
            .ToDictionary(i => i.GetProperty("nodeId").GetString()!, i => i.GetProperty("companions").GetProperty("mangadexLatestUpload"));
        Assert.Equal(JsonValueKind.Null, items[a.Folder.PublicId].GetProperty("chapterId").ValueKind);
        Assert.Equal(ChapterB1, items[b.Folder.PublicId].GetProperty("chapterId").GetString());
    }

    private async Task<JsonElement> PageAsync(string? updatedSince, int? limit = 500, string? cursor = null)
    {
        _t.Db.ChangeTracker.Clear();
        var answer = await Export().PageAsync(_t.LibraryPublicId, updatedSince, cursor, limit, null);
        Assert.True(answer.Json is not null, answer.Error);
        return JsonDocument.Parse(answer.Json!).RootElement.Clone();
    }

    // --- MangaUpdates' releases feed ---

    [Fact]
    public async Task Feed_SendsOnlyThePageAndIncludeMetadata_MakesLinkedSeriesWithANewerReleaseDue_AndTheRefreshReadsThemNow()
    {
        await _h.Auto.EnableAutomaticAsync();
        var rising = await SeriesAsync("721", MdA);       // stored latest 30, release 31 -> due
        var same = await SeriesAsync("722", MdB);         // release 30 -> not
        var fresh = await SeriesAsync("723", "00000000-0000-4000-8000-0000000000d1"); // read an hour ago -> not
        var noChapter = await SeriesAsync("724", "00000000-0000-4000-8000-0000000000e1"); // release without a chapter -> due
        fresh.Record.FetchedAt = Now - TimeSpan.FromHours(1);
        await _t.Db.SaveChangesAsync();
        _releases.Add([(721, "31", "Feed Title One"), (722, "29-30", "Feed Title Two")]);
        _releases.Add([(723, "c.40", "Feed Title Three"), (724, null, "Feed Title Four")]);
        _releases.Add([(999999, "5", "Feed Title Not Here")]);

        var result = await Feed().RunAsync();

        Assert.Equal(new ReleasesFeedResult(3, 5, 2, null), result);
        var sent = _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaUpdatesApiHost).ToList();
        Assert.Equal(["?page=1&include_metadata=true", "?page=2&include_metadata=true", "?page=3&include_metadata=true"], sent.Select(s => s.Uri.Query));
        Assert.All(sent, s => Assert.Equal(HttpMethod.Get, s.Method));
        Assert.All(sent, s => Assert.Null(s.Body));
        _t.Db.ChangeTracker.Clear();
        var nudged = await _t.Db.MetadataRefreshNudges.AsNoTracking().Select(n => n.RecordId).OrderBy(id => id).ToListAsync();
        Assert.Equal(new[] { rising.Record.Id, noChapter.Record.Id }.Order(), nudged);
        // Nothing of the feed is stored or logged but those ids: no title.
        Assert.DoesNotContain(_h.Auto.Net.Logs.Lines, l => l.Contains("Feed Title", StringComparison.Ordinal));
        Assert.False(await _t.Db.MetadataRecords.AnyAsync(r => r.Title.Contains("Feed Title")));

        // The refresh pass reads the nudged records now (not due by their cadence), and the nudge ends with the refresh.
        _h.Auto.Records[721] = MuJson.Get(721, "Synthetic Series 721");
        _h.Auto.Records[724] = MuJson.Get(724, "Synthetic Series 724");
        _h.ResetRequests();
        var pass = await _h.Auto.Refresh().RunPassAsync();
        Assert.Equal(2, pass.Refreshed);
        Assert.Equal(["/v1/series/721", "/v1/series/724"],
            _h.Handler.Seen.Where(s => s.Uri.Host == MetadataHttp.MangaUpdatesApiHost).Select(s => s.Uri.AbsolutePath).Order(StringComparer.Ordinal));
        _t.Db.ChangeTracker.Clear();
        Assert.False(await _t.Db.MetadataRefreshNudges.AnyAsync());
        // Recommendation 5 the other way: the refresh saw MangaUpdates' latest chapter rise (30 -> 40), so the MangaDex list is due now.
        Assert.Equal(Now, await MapNextCheckAsync(rising.Record));
        Assert.NotEqual(Now, await MapNextCheckAsync(same.Record));
    }

    [Theory]
    [InlineData("12", 12.0)]
    [InlineData("12.5", 12.5)]
    [InlineData("12-14", 14.0)]
    [InlineData("c.40", 40.0)]
    [InlineData("Extra", null)]
    [InlineData(null, null)]
    public void Feed_ChapterOf_ReadsTheHighestNumber(string? text, double? expected) =>
        Assert.Equal(expected, MangaUpdatesReleasesFeed.ChapterOf(text));

    // --- An admin's Refresh (recommendation 6) ---

    [Fact]
    public async Task ManualRefresh_WritesTheObservationAndTheCadence_EndsTheNudge_AndMakesTheMangaDexListDueOnARise()
    {
        await _h.Auto.Net.EnableAsync(); // Fetch on; Automatic matching is not needed for an admin's Refresh
        var s = await SeriesAsync("731", MdA);
        _t.Db.MetadataRefreshNudges.Add(new MetadataRefreshNudgeEntity { RecordId = s.Record.Id, At = Now, Reason = 1 });
        await _t.Db.SaveChangesAsync();
        _h.Auto.Records[731] = MuJson.Get(731, "Synthetic Series 731"); // latest chapter 40 (stored 30)

        var result = await _h.Auto.Net.Identify().RefreshAsync(s.Folder.PublicId, "admin");

        Assert.Equal("Ok", result!.State);
        _t.Db.ChangeTracker.Clear();
        var observation = await _t.Db.MetadataRecordObservations.AsNoTracking().SingleAsync(o => o.RecordId == s.Record.Id);
        Assert.Equal((40.0, Now), (observation.LatestChapter, observation.ObservedAt));
        Assert.NotNull((await _t.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Id == s.Record.Id)).RefreshCadenceDays);
        Assert.False(await _t.Db.MetadataRefreshNudges.AnyAsync());
        Assert.Equal(Now, await MapNextCheckAsync(s.Record));
    }

    // --- The reading fast lane (recommendation 3) ---

    [Fact]
    public async Task ReadingFastLane_ARecentReadMarkOrATrackerEntryReading_IsCheckedEveryTwoDays()
    {
        await _h.Auto.EnableAutomaticAsync();
        var read = await SeriesAsync("741", MdA);
        var tracked = await SeriesAsync("742", MdB);
        var stale = await SeriesAsync("743", "00000000-0000-4000-8000-0000000000f1");
        var user = await _t.AddUserAsync("reader", false);
        var chapter = await _t.Db.CatalogNodes.FirstAsync(n => n.ParentId == read.Folder.Id);
        var staleChapter = await _t.Db.CatalogNodes.FirstAsync(n => n.ParentId == stale.Folder.Id);
        _t.Db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = chapter.Id, MarkedAt = Now - TimeSpan.FromDays(3) });
        _t.Db.ReadMarks.Add(new ReadMarkEntity { UserId = user.Id, ItemId = staleChapter.Id, MarkedAt = Now - TimeSpan.FromDays(20) });
        _t.Db.TrackerEntries.Add(new TrackerEntryEntity
        {
            UserId = user.Id, Tracker = "mal", RecordId = tracked.Record.Id, RemoteStatus = (int)com.lifepixer.mangapixer.Core.Trackers.TrackerEntryStatus.Reading,
            UpdatedAt = Now,
        });
        await _t.Db.SaveChangesAsync();

        var cadences = await _h.Auto.Refresh().RecomputeCadencesAsync();

        Assert.Equal(RefreshCadence.ReadingDays, cadences[read.Record.Id]);
        Assert.Equal(RefreshCadence.ReadingDays, cadences[tracked.Record.Id]);
        Assert.NotEqual(RefreshCadence.ReadingDays, cadences[stale.Record.Id]);
        Assert.Equal(RefreshCadenceReason.Reading,
            RefreshCadence.For(RefreshCadencePolicy.Default, await _h.Auto.Refresh().EvidenceAsync(read.Record), Now).Reason);
    }

    // --- The readable backfill (recommendation 8) ---

    [Fact]
    public async Task ReadableBackfill_MakesListsWithoutTheReadableChaptersDue_OnceADay_UntilNoneIsLeft()
    {
        var a = await SeriesAsync("751", MdA);
        var b = await SeriesAsync("752", MdB);
        await _t.Db.SeriesVolumeMaps.ExecuteUpdateAsync(u => u.SetProperty(m => m.ReleasedLanguage, "en").SetProperty(m => m.ReleasedChaptersJson, "[\"1\"]"));
        await _t.Db.SeriesVolumeMaps.Where(m => m.RecordId == b.Record.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadableChaptersJson, "[\"1\"]"));
        var backfill = new ReadableBackfill(_t.Db, _h.Time, Log<ReadableBackfill>());

        Assert.Equal(1, await backfill.RunIfDueAsync());
        Assert.Equal(Now, await MapNextCheckAsync(a.Record));
        Assert.NotEqual(Now, await MapNextCheckAsync(b.Record));
        Assert.Equal(0, await backfill.RunIfDueAsync()); // once a day

        // The volume-cover pass re-read it (the readable list is written with every read): the backfill is done for good.
        await _t.Db.SeriesVolumeMaps.Where(m => m.RecordId == a.Record.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadableChaptersJson, "[]"));
        _h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await backfill.RunIfDueAsync());
        Assert.NotNull((await NewChapterChecks.PeekAsync(_t.Db, default))!.BackfillDoneAt);
        await _t.Db.SeriesVolumeMaps.Where(m => m.RecordId == a.Record.Id).ExecuteUpdateAsync(u => u.SetProperty(m => m.ReadableChaptersJson, (string?)null));
        _h.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await backfill.RunIfDueAsync());
        Assert.Empty(_h.Handler.Seen); // never a request of its own
    }

    // --- The refresh more than once a day (recommendation 7) ---

    [Fact]
    public async Task Runner_RefreshesDueRecordsEveryFourHoursBetweenTheDailyPasses()
    {
        await _h.Auto.EnableAutomaticAsync();
        var s = await SeriesAsync("761", MdA);
        _latest[MdA] = ChapterA1;
        _h.Auto.Records[761] = MuJson.Get(761, "Synthetic Series 761");
        _t.Db.JobRuns.Add(new JobRunEntity { Key = "metadata-refresh", LastStartedAt = Now - TimeSpan.FromHours(3), LastOutcome = "ok" });
        await _t.Db.SaveChangesAsync();
        NewChapterChecksRunner Runner() => new(_t.Db, Sweep(), Feed(), new ReadableBackfill(_t.Db, _h.Time, Log<ReadableBackfill>()), _h.Auto.Refresh(), _h.Time,
            Log<NewChapterChecksRunner>());

        var early = await Runner().RunTickAsync();
        Assert.Null(early.DuePass); // the daily pass ran 3 hours ago
        Assert.NotNull(early.Sweep);

        _h.Time.Advance(TimeSpan.FromHours(1));
        s.Record.FetchedAt = Now - TimeSpan.FromDays(400); // due by any cadence
        _t.Db.MetadataRecords.Update(s.Record);
        await _t.Db.SaveChangesAsync();
        var later = await Runner().RunTickAsync();
        Assert.Equal(1, later.DuePass!.Value.Refreshed);
        Assert.Null(later.Sweep); // not due again yet
        Assert.Null((await Runner().RunTickAsync()).DuePass); // the next one 4 hours after this one
    }
}
