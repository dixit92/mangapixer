namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP integration tests for the 1.16.0 session-lifecycle hardening fixes.
/// Each defect is exercised through its real route (WebApplicationFactory) and
/// its effect verified against the persisted session state — either through the
/// HTTP auth surface (a follow-up request is now rejected) or by re-validating
/// the server session row via <see cref="SessionService"/>. Every test uses its
/// own factory so session/rate-limiter state never leaks between cases.
/// </summary>
[Collection("HttpSerial")]
public sealed class SessionLifecycleHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public SessionLifecycleHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    /// <summary>
    /// Fetches the single non-revoked session ticket for a user by name, in a
    /// fresh DI scope reading the shared SQLite file the running app writes to.
    /// </summary>
    private async Task<(long userId, string ticketId)> GetActiveSessionAsync(string userName)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var user = await db.Users.SingleAsync(u => u.UserName == userName);
        var session = await db.Sessions.SingleAsync(s => s.UserId == user.Id && !s.IsRevoked);
        return (user.Id, session.TicketId);
    }

    // --- Fix 1: logout revokes the server session row ---

    [Fact]
    public async Task Logout_RevokesServerSessionRow_TicketNoLongerValidates()
    {
        var client = await _factory.LoginAsAdminAsync();
        var (_, ticket) = await GetActiveSessionAsync("admin");

        // Sanity: the ticket validates before logout.
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
            Assert.NotNull(await svc.ValidateSessionAsync(ticket));
        }

        var logout = await client.PostAsync("/api/v1/auth/logout", null);
        Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);

        // The DB row is actually marked revoked (the pre-fix code revoked by the
        // encrypted cookie value, which never matches TicketId — a silent no-op),
        // and the ticket no longer validates.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = await db.Sessions.SingleAsync(s => s.TicketId == ticket);
            Assert.True(row.IsRevoked);

            var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
            Assert.Null(await svc.ValidateSessionAsync(ticket));
        }
    }

    // --- Fix 2: password change kills ALL sessions including the current one ---

    [Fact]
    public async Task ChangePassword_InvalidatesCurrentSession()
    {
        var client = await _factory.LoginAsAdminAsync();

        // /me works before the change.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/v1/auth/me")).StatusCode);

        var change = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "MangaPixer-Change-Me-Now!",
            NewPassword = "BrandNewPass123!",
        });
        Assert.Equal(HttpStatusCode.NoContent, change.StatusCode);

        // The SAME cookie is now invalid: kill-all revokes the current session
        // too (the security-stamp bump alone would already reject it).
        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    // --- Fix 3: throttled sliding session row ---

    [Fact]
    public async Task ValidateSession_PastHalfway_ExtendsExpiry()
    {
        var client = await _factory.LoginAsAdminAsync();
        var (_, ticket) = await GetActiveSessionAsync("admin");

        // Force the session into the second half of its lifetime: leave only an
        // hour of remaining lifetime (default lifetime is 7 days, so half is
        // 3.5 days — one hour remaining is well past halfway) while keeping it
        // unexpired so it still validates.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = await db.Sessions.SingleAsync(s => s.TicketId == ticket);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
            await db.SaveChangesAsync();
        }

        // An authenticated request runs OnValidatePrincipal -> ValidateSessionAsync,
        // which extends the expiry because the session is past halfway.
        var me = await client.GetAsync("/api/v1/auth/me");
        me.EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var row = await db.Sessions.SingleAsync(s => s.TicketId == ticket);
            // Pushed forward to roughly now + 7 days, far beyond the 1-hour mark.
            Assert.True(row.ExpiresAt > DateTimeOffset.UtcNow.AddDays(6),
                $"Expected extended expiry, got {row.ExpiresAt:o}");
        }
    }

    [Fact]
    public async Task ValidateSession_FreshSession_DoesNotWriteEveryRequest()
    {
        var client = await _factory.LoginAsAdminAsync();
        var (_, ticket) = await GetActiveSessionAsync("admin");

        DateTimeOffset before;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            before = (await db.Sessions.SingleAsync(s => s.TicketId == ticket)).ExpiresAt;
        }

        // A fresh session is in the FIRST half of its lifetime, so the throttle
        // must leave ExpiresAt untouched (no per-request DB write).
        (await client.GetAsync("/api/v1/auth/me")).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var after = (await db.Sessions.SingleAsync(s => s.TicketId == ticket)).ExpiresAt;
            Assert.Equal(before, after);
        }
    }

    // --- Fix 4: role change rotates the security stamp ---

    [Fact]
    public async Task AdminDemotion_InvalidatesDemotedUsersSession()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // Create a SECOND admin so demoting one is not blocked by last-admin
        // protection. A password-created user starts with ForcePasswordChange.
        var create = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "second",
            Password = "SecondPass123!",
            IsAdmin = true,
        });
        create.EnsureSuccessStatusCode();

        var second = await LoginAndClearForcedChangeAsync("second", "SecondPass123!", "SecondNew123!");

        // The second admin can reach an admin-only endpoint.
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/api/v1/admin/users")).StatusCode);

        // Primary admin demotes the second admin.
        var users = await admin.GetFromJsonAsync<List<AdminUserDto>>("/api/v1/admin/users");
        var secondDto = users!.Single(u => u.Username == "second");
        var demote = await admin.PostAsJsonAsync($"/api/v1/admin/users/{secondDto.Id}/update", new UpdateUserRequest
        {
            IsAdmin = false,
        });
        demote.EnsureSuccessStatusCode();

        // The demoted user's existing session lost admin access immediately: the
        // security-stamp rotation invalidates the session carrying the old admin
        // claim, so the admin endpoint is no longer reachable.
        var afterDemotion = await second.GetAsync("/api/v1/admin/users");
        Assert.NotEqual(HttpStatusCode.OK, afterDemotion.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, afterDemotion.StatusCode);
    }

    // --- Fix 5: restore actually invalidates sessions ---

    [Fact]
    public async Task RestoreFinalize_InvalidatesPriorSessions()
    {
        var client = await _factory.LoginAsAdminAsync();
        var (_, ticket) = await GetActiveSessionAsync("admin");

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
            Assert.NotNull(await svc.ValidateSessionAsync(ticket));
        }

        // Simulate the startup post-restore hook: Program.cs calls
        // DbRestoreService.AuditRestoreAsync against the newly-opened restored DB
        // once a restore has applied. That call now invalidates all sessions, so
        // the "sessions invalidated on next open" log line is finally true.
        using (var scope = _factory.Services.CreateScope())
        {
            var restore = scope.ServiceProvider.GetRequiredService<DbRestoreService>();
            await restore.AuditRestoreAsync("db_restore", "applied", "admin", correlationId: null);
        }

        // The prior session no longer validates, and the live cookie is rejected.
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<SessionService>();
            Assert.Null(await svc.ValidateSessionAsync(ticket));
        }

        var me = await client.GetAsync("/api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);
    }

    /// <summary>
    /// Logs a password-created user in, clears the forced password change, and
    /// returns a re-authenticated client (with CSRF header) whose session
    /// carries the user's current role claims.
    /// </summary>
    private async Task<HttpClient> LoginAndClearForcedChangeAsync(string username, string tempPassword, string newPassword)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = tempPassword,
        })).EnsureSuccessStatusCode();

        await AttachCsrfAsync(client);
        (await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = tempPassword,
            NewPassword = newPassword,
        })).EnsureSuccessStatusCode();

        // The change revoked the temp-password session; re-login on a fresh client.
        var fresh = _factory.CreateClient();
        (await fresh.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = username,
            Password = newPassword,
        })).EnsureSuccessStatusCode();
        await AttachCsrfAsync(fresh);
        return fresh;
    }

    private static async Task AttachCsrfAsync(HttpClient client)
    {
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf"))
            .Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
    }
}
