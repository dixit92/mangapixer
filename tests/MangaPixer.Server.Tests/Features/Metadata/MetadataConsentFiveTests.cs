namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// 1.38.0 (lane U): a new kind of MangaUpdates request (author records, when an admin asks for artists' other names) bumps the main
/// metadata consent 4 -> 5; the automatic-matching consent stays 4. An instance that accepted 4 sends NOTHING - no search, no record,
/// no author - until an admin accepts 5 (service-with-DB, the real gateway, the scripted handler).
/// </summary>
public sealed class MetadataConsentFiveTests : IAsyncLifetime
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
    public void TheMainConsentIsVersion5_AndTheAutomaticConsentStays4()
    {
        Assert.Equal(5, MetadataConsent.CurrentVersion);
        Assert.Equal(4, MetadataAutoConsent.CurrentVersion);
    }

    [Fact]
    public async Task AnInstanceThatAcceptedVersion4_SendsNothing_UntilAnAdminAccepts5()
    {
        await _h.EnableAsync(consentVersion: 4);

        var refused = await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal("metadata_disabled", refused.Code);
        await Assert.ThrowsAsync<MetadataGatewayException>(() =>
            _h.Gateway().DetailCallAsync("mangaupdates", "author", _t.LibraryId, _ => Task.FromResult(0), null));
        Assert.Empty(_h.Handler.Seen);

        var before = await _h.Settings().GetAsync();
        Assert.True(before.ConsentRenewalNeeded);
        Assert.Equal(4, before.AcceptedConsentVersion);

        // Re-sending the old version is not enough; accepting 5 turns fetching back on.
        Assert.Equal("consent_required", await _h.Settings().UpdateAsync(
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = 4 }, "admin"));
        Assert.Null(await _h.Settings().UpdateAsync(
            new UpdateMetadataSettingsRequest { FetchEnabled = true, AcceptedConsentVersion = 5 }, "admin"));
        Assert.False((await _h.Settings().GetAsync()).ConsentRenewalNeeded);

        var record = await _h.Gateway().GetSeriesAsync("mangaupdates", _t.LibraryId, MuFixtures.BerserkId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Assert.NotNull(record);
        Assert.Single(_h.Handler.Seen);
    }
}
