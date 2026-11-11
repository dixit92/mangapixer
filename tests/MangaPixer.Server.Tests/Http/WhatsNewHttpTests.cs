namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.WhatsNew;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Tests.Features.WhatsNew;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.53.0 lane W, through the real pipeline on a synthetic CHANGELOG (the Testing-only <c>WhatsNew:ChangelogPath</c> /
/// <c>WhatsNew:RunningVersion</c> seam): <c>GET /api/v1/admin/whats-new</c> gives an admin the sections since their last seen version
/// (null = the running version's only), <c>?current=true</c> the running version's section, <c>POST .../seen</c> persists; a reader (and
/// their Incognito session) gets 403, anonymous 401; every new account starts with <c>LastSeenVersion</c> = the running version; a
/// malformed or missing file answers 200 with no sections, never 500. One test runs on the embedded file and the assembly version.
/// </summary>
public sealed class WhatsNewHttpTests : IDisposable
{
    private const string Running = "2.2.0";
    private readonly string _dir = Directory.CreateTempSubdirectory("mp-whatsnew-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private MangaPixerWebApplicationFactory Factory(string? changelog = null, string running = Running) =>
        MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
        {
            ["WhatsNew:ChangelogPath"] = WhatsNewFixture.WriteTo(_dir, changelog),
            ["WhatsNew:RunningVersion"] = running,
        });

    private static async Task<WhatsNewDto> GetAsync(HttpClient client, bool current = false)
    {
        using var response = await client.GetAsync("/api/v1/admin/whats-new" + (current ? "?current=true" : ""));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WhatsNewDto>(TestJson.Web))!;
    }

    private static async Task<string?> LastSeenAsync(MangaPixerWebApplicationFactory factory, string username)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).Select(u => u.LastSeenVersion).SingleAsync();
    }

    private static async Task SetLastSeenAsync(MangaPixerWebApplicationFactory factory, string username, string? version)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        await db.Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenVersion, version));
    }

    private static string Text(WhatsNewItemDto item) => string.Concat(item.Parts.Select(p => p.Text));

    [Fact]
    public async Task Admin_GetsEverySectionSinceTheLastSeenVersion_NewestFirst_WithTheInlineParts()
    {
        using var factory = Factory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await SetLastSeenAsync(factory, "admin", "2.0.1");

        var dto = await GetAsync(admin);

        Assert.Equal(Running, dto.Current);
        Assert.Equal(["2.2.0", "2.1.0"], dto.Sections.Select(s => s.Version));
        var top = dto.Sections[0];
        Assert.Equal("2030-03-01", top.Date);
        Assert.Equal(["Added", "Fixed"], top.Groups.Select(g => g.Name));
        var parts = top.Groups[0].Items[0].Parts;
        Assert.Contains(parts, p => p is { Text: "Shared lanterns", Bold: true });
        Assert.Contains(parts, p => p is { Text: "Administration", Link: "/admin?tab=general" });
        Assert.Contains(parts, p => p is { Text: "lantern:", Code: true });
        Assert.All(top.Groups[1].Items[0].Parts, p => Assert.Null(p.Link));
        Assert.DoesNotContain(dto.Sections.SelectMany(s => s.Groups).SelectMany(g => g.Items), i => Text(i).Contains("UNRELEASED", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NeverSeen_IsTheRunningVersionsSectionOnly_AndCurrentTrue_IsItSeenOrNot()
    {
        using var factory = Factory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await SetLastSeenAsync(factory, "admin", null);   // an admin from before 1.53.0

        Assert.Equal(["2.2.0"], (await GetAsync(admin)).Sections.Select(s => s.Version));

        await SetLastSeenAsync(factory, "admin", "2.0.0");
        Assert.Equal(["2.2.0"], (await GetAsync(admin, current: true)).Sections.Select(s => s.Version));
        await SetLastSeenAsync(factory, "admin", Running);
        Assert.Empty((await GetAsync(admin)).Sections);
        Assert.Equal(["2.2.0"], (await GetAsync(admin, current: true)).Sections.Select(s => s.Version));
    }

    [Fact]
    public async Task Seen_Persists_AndIsIdempotent()
    {
        using var factory = Factory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await SetLastSeenAsync(factory, "admin", "2.0.0");
        Assert.Equal(3, (await GetAsync(admin)).Sections.Count);

        using (var seen = await admin.PostAsync("/api/v1/admin/whats-new/seen", null))
            Assert.Equal(HttpStatusCode.NoContent, seen.StatusCode);

        Assert.Equal(Running, await LastSeenAsync(factory, "admin"));
        Assert.Empty((await GetAsync(admin)).Sections);
        // A new session of the same admin (the next login) sees nothing either: it is the account's, not the session's.
        var again = await AccessTestUsers.LoginAsync(factory, "admin", "TestPassword123!");
        Assert.Empty((await GetAsync(again)).Sections);
        using (var twice = await again.PostAsync("/api/v1/admin/whats-new/seen", null))
            Assert.Equal(HttpStatusCode.NoContent, twice.StatusCode);
        Assert.Equal(Running, await LastSeenAsync(factory, "admin"));
    }

    [Fact]
    public async Task Reader_Gets403_IncognitoToo_Anonymous401_AndNothingChanges()
    {
        using var factory = Factory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "wnreader", []);
        await SetLastSeenAsync(factory, "wnreader", "2.0.0");
        var anonymous = factory.CreateClient();

        foreach (var client in new[] { reader.Client, reader.Incognito })
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/whats-new")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/api/v1/admin/whats-new?current=true")).StatusCode);
            Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsync("/api/v1/admin/whats-new/seen", null)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/whats-new")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.PostAsync("/api/v1/admin/whats-new/seen", null)).StatusCode);
        Assert.Equal("2.0.0", await LastSeenAsync(factory, "wnreader"));

        // The admin under Incognito still reads its notes (Incognito only hides reading).
        var adminIncognito = await AccessTestUsers.LoginAsync(factory, "admin", "TestPassword123!");
        adminIncognito.DefaultRequestHeaders.Add(AccessSweepSupport.IncognitoHeader, "1");
        Assert.Equal(Running, (await GetAsync(adminIncognito, current: true)).Current);
    }

    [Fact]
    public async Task EveryNewAccount_StartsWithTheRunningVersionSeen()
    {
        using var factory = Factory();
        // The first admin (first-run setup) of a fresh install: no popup.
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        Assert.Equal(Running, await LastSeenAsync(factory, "admin"));
        Assert.Empty((await GetAsync(admin)).Sections);

        // An admin-created reader and an admin-created admin.
        await AccessTestUsers.ReaderAsync(factory, admin, "wnnewreader", []);
        Assert.Equal(Running, await LastSeenAsync(factory, "wnnewreader"));
        using var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "wnnewadmin", IsAdmin = true, Password = "NewAdminPass123!" });
        created.EnsureSuccessStatusCode();
        Assert.Equal(Running, await LastSeenAsync(factory, "wnnewadmin"));
    }

    [Theory]
    [InlineData("## [2.2.0] - 2030-03-01\n\n- a bullet before any group\n")]
    [InlineData("## Not a version\n")]
    [InlineData("")]
    public async Task AMalformedFile_Answers200_WithNoSections(string changelog)
    {
        using var factory = Factory(changelog);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        await SetLastSeenAsync(factory, "admin", null);

        var dto = await GetAsync(admin);

        Assert.Equal(Running, dto.Current);
        Assert.Empty(dto.Sections);
        Assert.Empty((await GetAsync(admin, current: true)).Sections);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PostAsync("/api/v1/admin/whats-new/seen", null)).StatusCode);
    }

    [Fact]
    public async Task AMissingFile_Answers200_WithNoSections()
    {
        using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
        {
            ["WhatsNew:ChangelogPath"] = Path.Combine(_dir, "no-such-file.md"),
            ["WhatsNew:RunningVersion"] = Running,
        });
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Empty((await GetAsync(admin, current: true)).Sections);
    }

    /// <summary>Without the seam: the embedded CHANGELOG and the assembly's version (content not asserted - it changes every release).</summary>
    [Fact]
    public async Task TheEmbeddedNotes_AndTheAssemblyVersion_AreWhatAProductionHostServes()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var informational = typeof(WhatsNewNotes).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;

        var dto = await GetAsync(admin, current: true);

        Assert.Equal(WhatsNewSelection.CoreText(informational), dto.Current);
        Assert.True(dto.Sections.Count <= 1);
        Assert.All(dto.Sections, s => Assert.Equal(dto.Current, WhatsNewSelection.CoreText(s.Version)));
        Assert.Equal(dto.Current, await LastSeenAsync(factory, "admin"));
        Assert.NotEmpty(factory.Services.GetRequiredService<WhatsNewNotes>().Sections);
    }
}
