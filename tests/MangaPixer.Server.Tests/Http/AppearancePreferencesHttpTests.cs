namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using Xunit;

/// <summary>
/// HTTP tests (1.40.0 theming) for the per-user appearance preference: <c>PUT /api/v1/reading/preferences/appearance</c>
/// stores a base theme and an accent, validated against the appearance vocabulary; <c>GET /api/v1/reading/preferences</c>
/// returns them (dark / violet until the user chooses); the general <c>PUT /api/v1/reading/preferences</c> - which clients
/// send with the WHOLE DTO they loaded earlier - never resets them; and one user's choice never changes another's.
/// Bodies are plain JSON on purpose: the tests describe the wire contract, not the C# types.
/// </summary>
public sealed class AppearancePreferencesHttpTests : IDisposable
{
    private const string Preferences = "/api/v1/reading/preferences";
    private const string Appearance = "/api/v1/reading/preferences/appearance";

    private readonly MangaPixerWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<(string Theme, string Accent)> ReadAppearanceAsync(HttpClient client)
    {
        var response = await client.GetAsync(Preferences);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var theme = body.TryGetProperty("theme", out var t) ? t.GetString() : null;
        var accent = body.TryGetProperty("accent", out var a) ? a.GetString() : null;
        return (theme ?? "<missing>", accent ?? "<missing>");
    }

    [Fact]
    public async Task Get_ForAUserWhoNeverChose_ReturnsDarkAndViolet()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Equal(("dark", "violet"), await ReadAppearanceAsync(admin));
    }

    [Fact]
    public async Task PutAppearance_StoresBothValues_AndGetReturnsThem()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await admin.PutAsJsonAsync(Appearance, new { theme = "light", accent = "teal" });

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal(("light", "teal"), await ReadAppearanceAsync(admin));
    }

    [Fact]
    public async Task PutAppearance_WithOneField_KeepsTheOther()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync(Appearance, new { theme = "sepia", accent = "amber" })).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Appearance, new { accent = "rose" })).StatusCode);
        Assert.Equal(("sepia", "rose"), await ReadAppearanceAsync(admin));

        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Appearance, new { theme = "system" })).StatusCode);
        Assert.Equal(("system", "rose"), await ReadAppearanceAsync(admin));
    }

    [Theory]
    [InlineData("{\"theme\":\"neon\"}")]
    [InlineData("{\"theme\":\"Light\"}")]
    [InlineData("{\"theme\":\"\"}")]
    [InlineData("{\"accent\":\"purple\"}")]
    [InlineData("{\"theme\":\"black\",\"accent\":\"VIOLET\"}")]
    public async Task PutAppearance_WithAnUnknownValue_Is400_AndChangesNothing(string json)
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PutAsJsonAsync(Appearance, new { theme = "light", accent = "blue" })).EnsureSuccessStatusCode();

        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var response = await admin.PutAsync(Appearance, content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("invalid_appearance", error.GetProperty("error").GetString());
        Assert.Equal(("light", "blue"), await ReadAppearanceAsync(admin));
    }

    [Fact]
    public async Task GeneralPut_WithAStaleCopy_OrWithoutTheFields_NeverResetsTheAppearance()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();

        // The Reading card loads the whole DTO first (dark / violet at that time) ...
        var loaded = await (await admin.GetAsync(Preferences)).Content.ReadFromJsonAsync<JsonElement>();
        // ... the Appearance card then changes the theme ...
        (await admin.PutAsJsonAsync(Appearance, new { theme = "light", accent = "green" })).EnsureSuccessStatusCode();

        // ... and the Reading card echoes its stale copy with one toggle changed.
        var stale = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(loaded.GetRawText())!;
        stale["alwaysOpenReadFromStart"] = JsonSerializer.SerializeToElement(true);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsJsonAsync(Preferences, stale)).StatusCode);
        Assert.Equal(("light", "green"), await ReadAppearanceAsync(admin));

        // An older client that knows neither field (and a client sending an unknown value there) changes nothing either.
        using var older = new StringContent(
            "{\"defaultReaderMode\":0,\"preferDoubleSpread\":false,\"reducedMotion\":false,\"alwaysOpenReadFromStart\":false}",
            System.Text.Encoding.UTF8,
            "application/json");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync(Preferences, older)).StatusCode);
        using var odd = new StringContent(
            "{\"defaultReaderMode\":0,\"theme\":\"neon\",\"accent\":\"purple\"}",
            System.Text.Encoding.UTF8,
            "application/json");
        Assert.Equal(HttpStatusCode.NoContent, (await admin.PutAsync(Preferences, odd)).StatusCode);

        Assert.Equal(("light", "green"), await ReadAppearanceAsync(admin));
        var after = await (await admin.GetAsync(Preferences)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(after.GetProperty("alwaysOpenReadFromStart").GetBoolean());
    }

    [Fact]
    public async Task OneUsersChoice_NeverChangesAnothers()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await CreateAndLoginReaderAsync(admin, "themereader");

        (await admin.PutAsJsonAsync(Appearance, new { theme = "light", accent = "amber" })).EnsureSuccessStatusCode();
        Assert.Equal(("dark", "violet"), await ReadAppearanceAsync(reader));

        (await reader.PutAsJsonAsync(Appearance, new { theme = "black", accent = "teal" })).EnsureSuccessStatusCode();
        Assert.Equal(("black", "teal"), await ReadAppearanceAsync(reader));
        Assert.Equal(("light", "amber"), await ReadAppearanceAsync(admin));
    }

    private async Task<HttpClient> CreateAndLoginReaderAsync(HttpClient admin, string username)
    {
        (await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = username,
            Password = "TargetPass123!",
            IsAdmin = false,
        })).EnsureSuccessStatusCode();

        var first = _factory.CreateClient();
        await first.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = "TargetPass123!" });
        await AddCsrfAsync(first);
        await first.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "TargetPass123!",
            NewPassword = "TargetPassNew123!",
        });

        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = "TargetPassNew123!" }))
            .EnsureSuccessStatusCode();
        await AddCsrfAsync(client);
        return client;
    }

    private static async Task AddCsrfAsync(HttpClient client)
    {
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        Assert.NotNull(csrf);
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
    }
}
