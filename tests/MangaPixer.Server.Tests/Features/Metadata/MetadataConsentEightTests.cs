namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.54.0 (owner, 2026-11-23: "Bump, ship all three"): community scores add new kinds of request (MangaDex statistics, the MyAnimeList
/// score), so the main consent moves 7 -> 8 AND the automatic consent 5 -> 6. An instance updated from 1.53 (main 7, automatic 5, both
/// switches on) sends NOTHING - no manual and no automatic request - until an admin accepts the current main text; that resumes manual
/// lookups, while automatic work (matching, refresh, the score passes) waits until the automatic text is accepted too
/// (service-with-DB, the real gateway, the scripted handler).
/// </summary>
public sealed class MetadataConsentEightTests : IAsyncLifetime
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

    [Fact]
    public async Task AnInstanceThatAcceptedVersions7And5_IsPaused_UntilAnAdminAcceptsBothCurrentTexts()
    {
        Assert.Equal((8, 6), (MetadataConsent.CurrentVersion, MetadataAutoConsent.CurrentVersion));
        await _h.EnableAsync(consentVersion: 7);
        _t.Db.ChangeTracker.Clear();
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataAutoMatchEnabled = true;
        row.MetadataAutoConsentVersion = 5; // what 1.53 asked for
        await _t.Db.SaveChangesAsync();
        var id = MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var before = await _h.Settings().GetAsync();
        Assert.True(before.ConsentRenewalNeeded);
        Assert.True(before.AutoConsentRenewalNeeded);
        Assert.Equal((7, 8, 6), (before.AcceptedConsentVersion, before.CurrentConsentVersion, before.CurrentAutoConsentVersion));
        foreach (var origin in new[] { MetadataCallOrigin.Interactive, MetadataCallOrigin.Automatic })
            Assert.NotNull(await _h.Gateway().CheckSwitchesAsync(_t.LibraryId, origin));
        var refused = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, id));
        Assert.Equal("metadata_disabled", refused.Code);
        Assert.Empty(_h.Handler.Seen);

        // Re-sending version 7 is refused; accepting the current main text resumes manual lookups only.
        Assert.Equal("consent_required", await _h.Settings().UpdateAsync(
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = 7 }, "admin"));
        Assert.Null(await _h.Settings().UpdateAsync(
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion }, "admin"));
        var main = await _h.Settings().GetAsync();
        Assert.False(main.ConsentRenewalNeeded);
        Assert.True(main.AutoConsentRenewalNeeded);
        Assert.NotNull(await _h.Gateway().CheckSwitchesAsync(_t.LibraryId, MetadataCallOrigin.Automatic));
        Assert.NotNull(await _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, id));
        Assert.Single(_h.Handler.Seen);

        // Accepting the automatic text too resumes automatic work.
        Assert.Null(await _h.Settings().UpdateAsync(new UpdateMetadataSettingsRequest
        {
            FetchEnabled = true,
            AcceptedConsentVersion = MetadataConsent.CurrentVersion,
            AutoMatchEnabled = true,
            AcceptedAutoConsentVersion = MetadataAutoConsent.CurrentVersion,
        }, "admin"));
        var after = await _h.Settings().GetAsync();
        Assert.False(after.AutoConsentRenewalNeeded);
        Assert.Null(await _h.Gateway().CheckSwitchesAsync(_t.LibraryId, MetadataCallOrigin.Automatic));
    }
}
