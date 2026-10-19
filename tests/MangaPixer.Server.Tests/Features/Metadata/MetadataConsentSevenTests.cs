namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.45.0 (seam I0, owner-approved 2026-10-19): author search by name is a new kind of MangaUpdates request, so the main consent moves
/// 6 -> 7 while the automatic consent stays 5. An instance updated from 1.44.0 (main 6, automatic 5, both switches on) sends NOTHING -
/// no manual and no automatic request - until an admin accepts the current main text; accepting it alone resumes automatic matching
/// too, because its own consent did not change (service-with-DB, the real gateway, the scripted handler).
/// </summary>
public sealed class MetadataConsentSevenTests : IAsyncLifetime
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
    public async Task AnInstanceThatAcceptedVersion6_IsPaused_UntilAnAdminAcceptsTheCurrentText_AndTheAutomaticConsentCarriesOver()
    {
        Assert.Equal(6, MetadataConsent.CurrentVersion - 1); // the version before this cycle's (read, never assumed elsewhere)
        await _h.EnableAsync(consentVersion: 6);
        _t.Db.ChangeTracker.Clear();
        var row = await _t.Db.AppSettings.FirstAsync();
        row.MetadataAutoMatchEnabled = true;
        row.MetadataAutoConsentVersion = MetadataAutoConsent.CurrentVersion; // 1.44.0 already asked for automatic 5
        await _t.Db.SaveChangesAsync();
        var id = MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var before = await _h.Settings().GetAsync();
        Assert.True(before.ConsentRenewalNeeded);
        Assert.False(before.AutoConsentRenewalNeeded);
        Assert.Equal(6, before.AcceptedConsentVersion);
        Assert.Equal(MetadataConsent.CurrentVersion, before.CurrentConsentVersion);
        foreach (var origin in new[] { MetadataCallOrigin.Interactive, MetadataCallOrigin.Automatic })
            Assert.Equal("metadata_disabled", (await _h.Gateway().CheckSwitchesAsync(_t.LibraryId, origin))!.Code);
        var refused = await Assert.ThrowsAsync<MetadataGatewayException>(() => _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, id));
        Assert.Equal("metadata_disabled", refused.Code);
        await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Gateway().DetailCallAsync("mangaupdates", "author", _t.LibraryId, _ => Task.FromResult(0), null));
        Assert.Empty(_h.Handler.Seen);

        // Re-sending version 6 is refused; accepting the current main text (and nothing else) resumes both.
        Assert.Equal("consent_required", await _h.Settings().UpdateAsync(
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = 6 }, "admin"));
        Assert.True((await _h.Settings().GetAsync()).ConsentRenewalNeeded);
        Assert.Null(await _h.Settings().UpdateAsync(
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = MetadataConsent.CurrentVersion }, "admin"));

        var after = await _h.Settings().GetAsync();
        Assert.False(after.ConsentRenewalNeeded);
        Assert.False(after.AutoConsentRenewalNeeded);
        Assert.Null(await _h.Gateway().CheckSwitchesAsync(_t.LibraryId, MetadataCallOrigin.Automatic));
        Assert.NotNull(await _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, id));
        Assert.Single(_h.Handler.Seen);
    }
}
