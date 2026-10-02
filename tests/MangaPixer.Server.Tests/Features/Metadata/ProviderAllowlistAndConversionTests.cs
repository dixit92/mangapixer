namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using System.Net;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Features.Metadata.Missing;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests (1.28.0) for the provider allowlist (removing a site stops every request to it at the
/// gateway, manual and automatic; add it back), consent renewal after an update (an upgraded row with the old
/// version: fetcher off, renewal flag on; re-accept: flag gone, fetcher on), and the AniList chapters-per-volume
/// lookup (what is sent, what is stored, the conversion in the report, no match, backoff, the kill switches).
/// Every request ends in <see cref="ScriptedHandler"/> - no live network. Public titles and synthetic names only.
/// </summary>
public sealed class ProviderAllowlistAndConversionTests : IAsyncLifetime
{
    private MetadataTestDb _t = null!;
    private GatewayHarness _h = null!;

    public async Task InitializeAsync()
    {
        _t = await MetadataTestDb.CreateAsync();
        _h = new GatewayHarness(_t);
    }

    public async Task DisposeAsync()
    {
        _h.Dispose();
        await _t.DisposeAsync();
    }

    private async Task RemoveAsync(params string[] providers) =>
        Assert.Null(await _h.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { RemovedProviders = providers }, "admin"));

    // --- allowlist ---

    [Fact]
    public void AllowlistJson_RoundTrips_AndIgnoresUnknownOrMalformed()
    {
        Assert.Null(MetadataProviderAllowlist.Write([]));
        Assert.Equal("{\"removed\":[\"anilist\"]}", MetadataProviderAllowlist.Write(["anilist", "anilist", "someone-else"]));
        Assert.Equal(["anilist"], MetadataProviderAllowlist.Removed("{\"removed\":[\"anilist\",\"nope\"]}"));
        Assert.Empty(MetadataProviderAllowlist.Removed("{not json"));
        Assert.True(MetadataProviderAllowlist.IsAllowed(null, "mangaupdates"));
        Assert.False(MetadataProviderAllowlist.IsAllowed(null, "unknown"));
    }

    [Theory]
    [InlineData("1.32.0+sha.858c7df", "MangaPixer/1.32.0 (+https://github.com/dixit92/mangapixer)")]
    [InlineData("1.32.0-rc.1", "MangaPixer/1.32.0-rc.1 (+https://github.com/dixit92/mangapixer)")]
    [InlineData(null, "MangaPixer/0.0.0 (+https://github.com/dixit92/mangapixer)")]
    [InlineData("1.32.0 (evil)", "MangaPixer/0.0.0 (+https://github.com/dixit92/mangapixer)")]
    public void UserAgent_NamesProductVersionAndProjectUrl_WithoutBuildMetadata(string? informational, string expected)
    {
        Assert.Equal(expected, MetadataHttp.UserAgentFor(informational));
        Assert.Matches(@"^MangaPixer/\d+\.\d+\.\d+[0-9A-Za-z.-]* \(\+https://github\.com/dixit92/mangapixer\)$", MetadataHttp.UserAgent);
    }

    [Fact]
    public async Task Settings_ListEveryApprovedSite_AllInByDefault_RemoveAndAddBack()
    {
        var fresh = await _h.Settings().GetAsync();
        Assert.Equal(["mangaupdates", "gcd", "mangadex", "anilist", "wikipedia"], fresh.Providers.Select(p => p.Id));
        Assert.All(fresh.Providers, p => Assert.True(p.Allowed));
        Assert.Equal(new[] { "api.mangaupdates.com", "cdn.mangaupdates.com" }, fresh.Providers[0].Hosts);
        Assert.Equal(new[] { "www.comics.org", "files1.comics.org" }, fresh.Providers[1].Hosts);
        Assert.Equal(new[] { "api.mangadex.org", "uploads.mangadex.org" }, fresh.Providers[2].Hosts);
        Assert.Contains("graphql.anilist.co", fresh.Providers[3].Hosts);
        Assert.Equal(new[] { "en.wikipedia.org", "www.wikidata.org" }, fresh.Providers[4].Hosts);

        await RemoveAsync("anilist");
        Assert.False((await _h.Settings().GetAsync()).Providers.Single(p => p.Id == "anilist").Allowed);
        Assert.True(await _t.Db.AuditEvents.AnyAsync(a => a.Action == "metadata.providers.change"));

        await RemoveAsync();
        Assert.All((await _h.Settings().GetAsync()).Providers, p => Assert.True(p.Allowed));
        Assert.Null((await _h.ReadSettingsAsync()).MetadataProvidersJson);

        Assert.Equal("invalid_provider", await _h.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { RemovedProviders = ["somewhere"] }, "admin"));
    }

    [Fact]
    public async Task RemovedMangaUpdates_StopsManualAndAutomaticRequests_AtTheGateway()
    {
        await _h.EnableAsync();
        _t.Db.ChangeTracker.Clear();
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataAutoMatchEnabled = true;
        row.MetadataAutoConsentVersion = MetadataAutoConsent.CurrentVersion;
        await _t.Db.SaveChangesAsync();
        await RemoveAsync("mangaupdates");
        _h.Handler.FailOnAnyRequest = true;

        var manual = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().SearchAsync("mangaupdates", _t.LibraryId, "Berserk", 1));
        Assert.Equal((409, "provider_not_allowed"), (manual.HttpStatus, manual.Code));
        var automatic = await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Gateway().SearchAutomaticAsync("mangaupdates", _t.LibraryId, "Berserk", false, MetadataCallContext.Automatic()));
        Assert.Equal("provider_not_allowed", automatic.Code);
        await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, MuFixtures.BerserkId.ToString()));
        Assert.Equal("provider_not_allowed", (await _h.Gateway().CheckSwitchesAsync(_t.LibraryId))!.Code);
        Assert.Equal(0, _h.Handler.CallCount);

        // Added back: allowed again.
        await RemoveAsync();
        _h.Handler.FailOnAnyRequest = false;
        Assert.NotEmpty((await _h.Gateway().SearchAsync("mangaupdates", _t.LibraryId, "Berserk", 1)).Hits);
    }

    // --- consent renewal ---

    [Fact]
    public async Task UpgradedRow_WithTheOldConsent_StopsTheFetcher_AndAsksForRenewal_UntilReaccepted()
    {
        // As an instance updated from 1.27.0: both switches stored on, accepted under the older versions.
        await _h.EnableAsync(consentVersion: MetadataConsent.CurrentVersion - 1);
        _t.Db.ChangeTracker.Clear();
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataAutoMatchEnabled = true;
        row.MetadataAutoConsentVersion = MetadataAutoConsent.CurrentVersion - 1;
        await _t.Db.SaveChangesAsync();
        _h.Handler.FailOnAnyRequest = true;

        var before = await _h.Settings().GetAsync();
        Assert.True(before.ConsentRenewalNeeded);
        Assert.True(before.AutoConsentRenewalNeeded);
        Assert.True(before.FetchEnabled); // stored on, but gated
        Assert.Equal("metadata_disabled", (await _h.Gateway().CheckSwitchesAsync(_t.LibraryId))!.Code);
        await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().SearchAsync("mangaupdates", _t.LibraryId, "Berserk", 1));
        Assert.Equal(0, _h.Handler.CallCount);

        Assert.Null(await _h.Settings().UpdateAsync(new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        }, "admin"));
        var after = await _h.Settings().GetAsync();
        Assert.False(after.ConsentRenewalNeeded);
        Assert.False(after.AutoConsentRenewalNeeded);
        Assert.Null(await _h.Gateway().CheckSwitchesAsync(_t.LibraryId, MetadataCallOrigin.Automatic));
    }

    [Fact]
    public async Task Renewal_IsNotAsked_WhenTheSwitchWasOff_OrOnceItIsTurnedOff()
    {
        _t.Db.AppSettings.Add(new AppSettingsEntity { MetadataEnabled = false, MetadataConsentVersion = MetadataConsent.CurrentVersion - 1 });
        await _t.Db.SaveChangesAsync();
        Assert.False((await _h.Settings().GetAsync()).ConsentRenewalNeeded);

        _t.Db.ChangeTracker.Clear();
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataEnabled = true;
        await _t.Db.SaveChangesAsync();
        Assert.True((await _h.Settings().GetAsync()).ConsentRenewalNeeded);
        Assert.Null(await _h.Settings().UpdateAsync(new UpdateMetadataSettingsRequest { FetchEnabled = false }, "admin"));
        Assert.False((await _h.Settings().GetAsync()).ConsentRenewalNeeded);
    }

    // --- AniList chapters per volume ---

    /// <summary>A folder "Synthetic Alchemy Shelf" with chapters 1-3, linked to a MangaUpdates record titled like the public work.</summary>
    private async Task<(CatalogNodeEntity Folder, MetadataRecordEntity Record)> LinkedSeriesAsync(string title = "Hagane no Renkinjutsushi", int? startYear = 2001)
    {
        var folder = await _t.AddFolderAsync(null, "Synthetic Alchemy Shelf");
        foreach (var n in new[] { 1, 2, 3 })
            await _t.AddArchiveAsync(folder, $"Synthetic Alchemy - Chapter {n:D3}");
        var record = await _t.AddRecordAsync("7001", title, startYear: startYear);
        record.AltTitlesJson = "[\"Fullmetal Alchemist\"]";
        record.PublishersJson = "[{\"name\":\"Print English\",\"kind\":\"english\",\"volumes\":27}]";
        record.LatestChapter = null;
        await _t.Db.SaveChangesAsync();
        await _t.AddLinkAsync(folder, record);
        return (folder, record);
    }

    [Fact]
    public async Task Lookup_SendsOnlyTheLinkedTitle_StoresAnAniListRow_AndTheReportConverts()
    {
        await _h.EnableAsync();
        var (folder, _) = await LinkedSeriesAsync();

        var (error, result) = await _h.Conversion().LookupAsync(folder.PublicId, "admin");

        Assert.Null(error);
        Assert.Equal(MissingConversionOutcome.Found, result!.Outcome);
        var request = Assert.Single(_h.Handler.Seen);
        Assert.Equal(("graphql.anilist.co", "POST"), (request.Uri.Host, request.Method.Method));
        Assert.Contains("\"search\":\"Hagane no Renkinjutsushi\"", request.Body);
        Assert.DoesNotContain("Synthetic Alchemy", request.Body); // never a folder or file name
        Assert.Equal(MetadataHttp.UserAgent, request.Headers["User-Agent"]);
        Assert.False(request.Headers.ContainsKey("Cookie"));

        var stored = await _t.Db.MetadataRecords.AsNoTracking().SingleAsync(r => r.Provider == "anilist");
        Assert.Equal(("30025", 27, 116.0), (stored.ExternalId, stored.OriginVolumes, stored.LatestChapter));
        Assert.Equal("{\"mangaupdates\":\"7001\"}", stored.CrossIdsJson);
        Assert.False(await _t.Db.NodeSeriesLinks.AnyAsync(l => l.RecordId == stored.Id)); // never linked to a folder

        // 27 English volumes x 116/27 chapters per volume: the chapter total is converted.
        var row = result.Row;
        Assert.Equal(4.3, row.Conversion!.ChaptersPerVolume);
        Assert.Equal((MissingTotalSource.Converted, 116), (row.Chapters!.Source, row.Chapters.Available));
        Assert.True(await _t.Db.AuditEvents.AnyAsync(a => a.Action == "metadata.conversion.lookup" && a.Result == "found"));

        // A second lookup asks by id, not by title.
        _h.Handler.Reset();
        Assert.Equal(MissingConversionOutcome.Found, (await _h.Conversion().LookupAsync(folder.PublicId, "admin")).Result!.Outcome);
        Assert.Contains("\"id\":30025", Assert.Single(_h.Handler.Seen).Body);
        Assert.Equal(1, await _t.Db.MetadataRecords.CountAsync(r => r.Provider == "anilist"));
    }

    [Fact]
    public async Task Lookup_WithoutAConfidentMatch_StoresNothing_AndARunningEntryGivesNoRatio()
    {
        await _h.EnableAsync();
        var (folder, record) = await LinkedSeriesAsync(title: "Synthetic Title With No Entry");
        Assert.Equal(MissingConversionOutcome.NoMatch, (await _h.Conversion().LookupAsync(folder.PublicId, "admin")).Result!.Outcome);
        Assert.False(await _t.Db.MetadataRecords.AnyAsync(r => r.Provider == "anilist"));

        // The record names its AniList id itself (a cross reference): asked by id; RELEASING = no final totals.
        record.CrossIdsJson = "{\"anilist\":\"30002\"}";
        await _t.Db.SaveChangesAsync();
        var result = (await _h.Conversion().LookupAsync(folder.PublicId, "admin")).Result!;
        Assert.Equal(MissingConversionOutcome.NoCounts, result.Outcome);
        Assert.Null(result.Row.Conversion!.ChaptersPerVolume);
        Assert.NotEqual(MissingTotalSource.Converted, result.Row.Chapters?.Source);
    }

    [Fact]
    public async Task Lookup_IsRefused_WithoutARequest_WhenAniListIsRemoved_OrTheNetworkIsOff()
    {
        var (folder, _) = await LinkedSeriesAsync();
        _h.Handler.FailOnAnyRequest = true;

        // Fetch from the web off.
        Assert.Equal("metadata_disabled", (await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Conversion().LookupAsync(folder.PublicId, "admin"))).Code);

        await _h.EnableAsync();
        await RemoveAsync("anilist");
        Assert.Equal("provider_not_allowed", (await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Conversion().LookupAsync(folder.PublicId, "admin"))).Code);

        await RemoveAsync();
        using var killed = new GatewayHarness(_t, config: new Dictionary<string, string?> { ["Metadata:NetworkDisabled"] = "true" });
        killed.Handler.FailOnAnyRequest = true;
        Assert.Equal("metadata_network_disabled",
            (await Assert.ThrowsAsync<MetadataGatewayException>(() => killed.Conversion().LookupAsync(folder.PublicId, "admin"))).Code);
        Assert.Equal(0, _h.Handler.CallCount + killed.Handler.CallCount);
        Assert.Equal("not_found", (await _h.Conversion().LookupAsync("no-such-node", "admin")).Error);
    }

    [Fact]
    public async Task RateLimited_StartsAniListsOwnBackoff_AndTheBatchStops()
    {
        await _h.EnableAsync();
        await LinkedSeriesAsync();
        var other = await _t.AddFolderAsync(null, "Synthetic Second Shelf");
        await _t.AddArchiveAsync(other, "Synthetic Second v01");
        await _t.AddLinkAsync(other, await _t.AddRecordAsync("7002", "Synthetic Second Title"));
        _h.Handler.Respond = _ =>
        {
            var r = ScriptedHandler.Json("{\"errors\":[{\"message\":\"Too Many Requests.\",\"status\":429}]}", HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(30));
            return r;
        };

        var (_, batch) = await _h.Conversion().LookupBatchAsync(null, "admin");

        Assert.Equal((0, "provider_backoff", 2), (batch!.Looked, batch.StoppedCode, batch.Remaining));
        Assert.Equal(1, _h.Handler.CallCount); // stopped at the first 429
        Assert.NotNull(await _h.Backoff().ActiveUntilAsync("anilist")); // AniList's own persisted backoff (1.29.0)
        Assert.Null((await _h.ReadSettingsAsync()).MetadataBackoffUntil); // MangaUpdates is not paused

        // While it lasts, nothing is sent.
        _h.Handler.Reset();
        _h.Handler.FailOnAnyRequest = true;
        Assert.Equal("provider_backoff", (await _h.Conversion().LookupBatchAsync(null, "admin")).Result!.StoppedCode);
        Assert.Equal(0, _h.Handler.CallCount);
    }

    [Fact]
    public async Task Batch_LooksUpSeriesWithoutASource_AndSkipsARecentMiss()
    {
        await _h.EnableAsync();
        await LinkedSeriesAsync();
        var other = await _t.AddFolderAsync(null, "Synthetic Second Shelf");
        await _t.AddArchiveAsync(other, "Synthetic Second v01");
        await _t.AddLinkAsync(other, await _t.AddRecordAsync("7002", "Synthetic Second Title"));

        var (_, first) = await _h.Conversion().LookupBatchAsync(_t.LibraryPublicId, "admin");
        Assert.Equal((2, 1, 1, 1), (first!.Looked, first.Found, first.NoMatch, first.Remaining));

        _h.Handler.Reset();
        var (_, second) = await _h.Conversion().LookupBatchAsync(_t.LibraryPublicId, "admin");
        Assert.Equal((0, 1), (second!.Looked, second.Remaining)); // the found one is covered, the miss is remembered
        Assert.Equal(0, _h.Handler.CallCount);
        Assert.Equal("library_not_found", (await _h.Conversion().LookupBatchAsync("nope", "admin")).Error);
    }

    [Fact]
    public void Ratio_OnlyForAFinishedEntryWithBothTotals()
    {
        Assert.Equal(4.3, MissingConversionService.Ratio((int)MetadataOriginStatus.Complete, 27, 116));
        Assert.Null(MissingConversionService.Ratio((int)MetadataOriginStatus.Ongoing, 27, 116));
        Assert.Null(MissingConversionService.Ratio((int)MetadataOriginStatus.Complete, null, 116));
        Assert.Null(MissingConversionService.Ratio((int)MetadataOriginStatus.Complete, 27, 10));
    }
}
