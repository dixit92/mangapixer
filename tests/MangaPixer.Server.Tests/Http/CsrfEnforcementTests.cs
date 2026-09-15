namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using Xunit;

/// <summary>
/// Tests that the global anti-forgery filter validates the X-MangaPixer-Csrf
/// header on unsafe methods, and that [IgnoreAntiforgeryToken] endpoints
/// (csrf, login) are exempt. Each test creates its own factory.
/// </summary>
[Collection("HttpSerial")]
public sealed class CsrfEnforcementTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public CsrfEnforcementTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task UnsafePostWithoutCsrfHeader_Returns400()
    {
        var client = await _factory.LoginAsAdminAsync();
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "MangaPixer-Change-Me-Now!",
            NewPassword = "NewTestPassword123!",
        });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task UnsafePostWithCsrfHeader_PassesCsrfValidation()
    {
        var client = await _factory.LoginAsAdminAsync();

        // Re-fetch CSRF to ensure cookie/header are in sync after login
        var csrfResponse = await client.GetAsync("/api/v1/auth/csrf");
        var csrf = await csrfResponse.Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Remove("X-MangaPixer-Csrf");
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);

        var response = await client.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest
        {
            CurrentPassword = "MangaPixer-Change-Me-Now!",
            NewPassword = "NewTestPassword123!",
        });
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task LoginWithoutCsrfHeader_StillSucceeds()
    {
        // Seed the admin via first-run setup, then verify login itself needs no
        // CSRF header ([IgnoreAntiforgeryToken]).
        await _factory.LoginAsAdminAsync();

        var client = _factory.CreateClient();
        var response = await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest
        {
            Username = "admin",
            Password = "MangaPixer-Change-Me-Now!",
        });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetCsrfWithoutCsrfHeader_Succeeds()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/auth/csrf");
        response.EnsureSuccessStatusCode();
    }
}
