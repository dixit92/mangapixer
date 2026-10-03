namespace com.lifepixer.mangapixer.Tests.Server.Features.Tokens;

using com.lifepixer.mangapixer.Server.Features.Tokens;
using com.lifepixer.mangapixer.Tests.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests of <see cref="ApiTokenService"/> (1.33.0) on a migrated SQLite database and a manual clock: create,
/// validate, revoke, expiry, the owner's account state, and the throttled last-used time.
/// </summary>
public sealed class ApiTokenServiceTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Start = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private MetadataTestDb _t = null!;
    private readonly ManualTime _clock = new(Start);

    public async Task InitializeAsync() => _t = await MetadataTestDb.CreateAsync();

    public async Task DisposeAsync() => await _t.DisposeAsync();

    private ApiTokenService Service() => new(_t.Db, _clock);

    [Fact]
    public async Task Create_StoresOnlyTheHash_AndTheListNeverCarriesTheSecret()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var created = await Service().CreateAsync(admin.Id, "MangaList", 365);

        Assert.StartsWith("mpx_", created.Secret, StringComparison.Ordinal);
        Assert.Equal(created.Secret[..8], created.Token.Prefix);
        Assert.Equal(["metadata:read"], created.Token.Scopes);
        Assert.Equal("alice", created.Token.OwnerUserName);
        Assert.Equal(Start.AddDays(365), created.Token.ExpiresAt);
        Assert.Equal("active", created.Token.Status);

        var row = await _t.Db.ApiTokens.AsNoTracking().SingleAsync();
        Assert.Equal(ApiTokenSecret.Hash(created.Secret), row.SecretHash);
        Assert.DoesNotContain(created.Secret, row.SecretHash + row.Prefix + row.Name + row.PublicId, StringComparison.Ordinal);

        var listed = Assert.Single(await Service().ListAsync());
        Assert.Equal(created.Token.Id, listed.Id);
        Assert.DoesNotContain(created.Secret, System.Text.Json.JsonSerializer.Serialize(listed), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Validate_AcceptsTheToken_AndRefusesUnknownAndMalformedValues()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var created = await Service().CreateAsync(admin.Id, "MangaList", null);

        var ok = await Service().ValidateAsync(created.Secret);
        Assert.True(ok.Succeeded);
        Assert.Equal(created.Token.Id, ok.TokenId);
        Assert.Equal(["metadata:read"], ok.Scopes);

        var (other, _, _) = ApiTokenSecret.Generate();
        Assert.Equal(ApiTokenRefusal.Unknown, (await Service().ValidateAsync(other)).Refusal);
        Assert.Equal(ApiTokenRefusal.Malformed, (await Service().ValidateAsync("not-a-token")).Refusal);
        Assert.Equal(ApiTokenRefusal.Malformed, (await Service().ValidateAsync(created.Secret + "x")).Refusal);
        // The hash is not a credential.
        Assert.Equal(ApiTokenRefusal.Malformed, (await Service().ValidateAsync(ApiTokenSecret.Hash(created.Secret))).Refusal);
    }

    [Fact]
    public async Task Revoke_StopsTheToken_AndKeepsTheFirstRevocationTime()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var created = await Service().CreateAsync(admin.Id, "MangaList", 90);

        Assert.NotNull(await Service().RevokeAsync(created.Token.Id));
        _clock.Advance(TimeSpan.FromHours(1));
        await Service().RevokeAsync(created.Token.Id);

        var refused = await Service().ValidateAsync(created.Secret);
        Assert.Equal(ApiTokenRefusal.Revoked, refused.Refusal);
        Assert.Equal(created.Token.Id, refused.TokenId);
        var listed = Assert.Single(await Service().ListAsync());
        Assert.Equal("revoked", listed.Status);
        Assert.Equal(Start, listed.RevokedAt);

        Assert.Null(await Service().RevokeAsync("no-such-token"));
    }

    [Fact]
    public async Task Expiry_EndsTheTokenAtItsTime_AndANeverExpiringTokenKeepsWorking()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var month = await Service().CreateAsync(admin.Id, "month", 30);
        var never = await Service().CreateAsync(admin.Id, "never", null);
        Assert.Null(never.Token.ExpiresAt);

        _clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromSeconds(1));
        Assert.True((await Service().ValidateAsync(month.Secret)).Succeeded);

        _clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(ApiTokenRefusal.Expired, (await Service().ValidateAsync(month.Secret)).Refusal);
        Assert.Equal("expired", (await Service().ListAsync()).Single(t => t.Name == "month").Status);

        _clock.Advance(TimeSpan.FromDays(3650));
        Assert.True((await Service().ValidateAsync(never.Secret)).Succeeded);
    }

    [Fact]
    public async Task OwnerState_ADemotedDisabledPendingOrForcedOwner_StopsTheToken_AndRestoringItRestoresIt()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var created = await Service().CreateAsync(admin.Id, "MangaList", 365);

        async Task Expect(ApiTokenRefusal refusal) =>
            Assert.Equal(refusal, (await Service().ValidateAsync(created.Secret)).Refusal);

        admin.IsAdmin = false;
        await _t.Db.SaveChangesAsync();
        await Expect(ApiTokenRefusal.OwnerNotAllowed);
        Assert.Equal("ownerInactive", Assert.Single(await Service().ListAsync()).Status);

        admin.IsAdmin = true;
        await _t.Db.SaveChangesAsync();
        await Expect(ApiTokenRefusal.None);

        admin.IsActive = false;
        await _t.Db.SaveChangesAsync();
        await Expect(ApiTokenRefusal.OwnerNotAllowed);
        admin.IsActive = true;

        admin.ForcePasswordChange = true;
        await _t.Db.SaveChangesAsync();
        await Expect(ApiTokenRefusal.OwnerNotAllowed);
        admin.ForcePasswordChange = false;

        admin.IsPendingActivation = true;
        await _t.Db.SaveChangesAsync();
        await Expect(ApiTokenRefusal.OwnerNotAllowed);
        admin.IsPendingActivation = false;

        // A lockout from failed password logins does not stop the token (see ApiTokenService remarks).
        admin.LockoutEnd = Start.AddHours(1);
        await _t.Db.SaveChangesAsync();
        await Expect(ApiTokenRefusal.None);
    }

    [Fact]
    public async Task OwnerDeleted_RemovesTheToken_AndItIsRefused()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var other = await _t.AddUserAsync("bob", isAdmin: true);
        var created = await Service().CreateAsync(admin.Id, "MangaList", 365);
        var kept = await Service().CreateAsync(other.Id, "Other", 365);

        await _t.Db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
        await _t.Db.Users.Where(u => u.Id == admin.Id).ExecuteDeleteAsync();

        Assert.Equal(ApiTokenRefusal.Unknown, (await Service().ValidateAsync(created.Secret)).Refusal);
        var left = Assert.Single(await _t.Db.ApiTokens.AsNoTracking().ToListAsync());
        Assert.Equal(kept.Token.Id, left.PublicId);
    }

    [Fact]
    public async Task LastUsed_IsWrittenAtMostOncePerMinute()
    {
        var admin = await _t.AddUserAsync("alice", isAdmin: true);
        var created = await Service().CreateAsync(admin.Id, "MangaList", 365);
        Assert.Null(created.Token.LastUsedAt);

        async Task<DateTimeOffset?> LastUsed() =>
            (await _t.Db.ApiTokens.AsNoTracking().SingleAsync()).LastUsedAt;

        Assert.True((await Service().ValidateAsync(created.Secret)).Succeeded);
        Assert.Equal(Start, await LastUsed());

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True((await Service().ValidateAsync(created.Secret)).Succeeded);
        Assert.Equal(Start, await LastUsed());

        _clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True((await Service().ValidateAsync(created.Secret)).Succeeded);
        Assert.Equal(Start.AddMinutes(1), await LastUsed());

        // A refused request never touches it.
        await Service().RevokeAsync(created.Token.Id);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Assert.False((await Service().ValidateAsync(created.Secret)).Succeeded);
        Assert.Equal(Start.AddMinutes(1), await LastUsed());
    }
}
