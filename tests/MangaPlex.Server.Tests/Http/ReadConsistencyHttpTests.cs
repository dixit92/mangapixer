namespace com.lifepixer.mangaplex.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP (WebApplicationFactory) tests for the 1.9.0 read-state-consistency lane
/// through the public API: manual mark-read records read-at-the-end (rule 3), clearing
/// a mark is a full reset (rule 1), the open-position rule surfaces as
/// <c>ReadingProgressDto.OpenPageIndex</c> and toggles with the preference (rule 2), and
/// the <c>AlwaysOpenReadFromStart</c> preference round-trips.
/// </summary>
[Collection("HttpSerial")]
public sealed class ReadConsistencyHttpTests : IClassFixture<MangaPlexWebApplicationFactory>
{
    private readonly MangaPlexWebApplicationFactory _factory;

    public ReadConsistencyHttpTests(MangaPlexWebApplicationFactory factory) => _factory = factory;

    private Task<HttpClient> ClientAsync() => _factory.LoginAsAdminWithChangedPasswordAsync();

    private async Task<string> SeedItemAsync(string publicId, int pageCount = 10)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();

        var existing = await db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == publicId);
        if (existing is not null) return existing.PublicId;

        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "rc-lib")
            ?? new LibraryEntity { PublicId = "rc-lib", DisplayName = "RC", RootPath = "/tmp/rc", CreatedAt = DateTimeOffset.UtcNow };
        if (library.Id == 0) { db.Libraries.Add(library); await db.SaveChangesAsync(); }

        var node = new CatalogNodeEntity
        {
            PublicId = publicId, LibraryId = library.Id, Kind = 1, DisplayName = $"{publicId}.cbz",
            RelativePath = $"{publicId}.cbz", PathKey = $"{publicId}.cbz", SortKey = $"1{publicId}",
            Availability = 0, CreatedAt = DateTimeOffset.UtcNow,
        };
        db.CatalogNodes.Add(node);
        await db.SaveChangesAsync();

        db.ArchiveItems.Add(new ArchiveItemEntity
        {
            NodeId = node.Id, ArchiveFormat = 0, ByteLength = 100, ModificationTicks = 0,
            ContentVersion = 1, AnalysisState = 0, PageCount = pageCount,
        });
        await db.SaveChangesAsync();
        return node.PublicId;
    }

    private static async Task<ReadingProgressDto> GetProgressAsync(HttpClient client, string itemId)
    {
        var resp = await client.GetAsync($"/api/v1/reading/progress/{itemId}");
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<ReadingProgressDto>(TestJson.Web))!;
    }

    private static async Task PutProgressAsync(HttpClient client, string itemId, int page, long? ifMatch, string mutationId)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/reading/progress/{itemId}")
        {
            Content = JsonContent.Create(new UpdateProgressRequest
            {
                PageIndex = page, ExpectedContentVersion = 1, MutationId = mutationId,
            }),
        };
        if (ifMatch is null) req.Headers.TryAddWithoutValidation("If-None-Match", "*");
        else req.Headers.TryAddWithoutValidation("If-Match", $"\"{ifMatch}\"");
        var resp = await client.SendAsync(req);
        resp.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task ManualMarkRead_RecordsCompletedAtLastPage_OpensAtStart()
    {
        var id = await SeedItemAsync("rc-manual-mark");
        var client = await ClientAsync();

        var before = await GetProgressAsync(client, id);
        Assert.Equal(ReadingState.Unread, before.State);
        Assert.Equal(0, before.OpenPageIndex);

        var mark = await client.PutAsync($"/api/v1/reading/{id}/read", null);
        Assert.Equal(HttpStatusCode.OK, mark.StatusCode);

        var after = await GetProgressAsync(client, id);
        Assert.Equal(ReadingState.Completed, after.State);
        Assert.Equal(9, after.PageIndex);       // read-at-the-end
        Assert.Equal(0, after.OpenPageIndex);    // so it opens at page 1
    }

    [Fact]
    public async Task ClearMark_IsFullReset_ReturnsToUnread()
    {
        var id = await SeedItemAsync("rc-clear-mark");
        var client = await ClientAsync();

        await client.PutAsync($"/api/v1/reading/{id}/read", null); // mark (Completed at end)

        var clear = await client.DeleteAsync($"/api/v1/reading/{id}/read");
        Assert.Equal(HttpStatusCode.OK, clear.StatusCode);

        var after = await GetProgressAsync(client, id);
        Assert.Equal(ReadingState.Unread, after.State);
        Assert.Equal(0, after.PageIndex);
        Assert.Equal(0, after.OpenPageIndex);

        var readMark = await client.GetFromJsonAsync<ReadMarkDto>($"/api/v1/reading/{id}/read");
        Assert.False(readMark!.IsRead);
    }

    [Fact]
    public async Task OpenRule_ReadMidArchive_TogglesWithPreference_NonDestructively()
    {
        var id = await SeedItemAsync("rc-open-rule");
        var client = await ClientAsync();

        // Finish (auto-marks read), then re-read back to page 4 (mark persists, mid pos).
        await PutProgressAsync(client, id, 9, ifMatch: null, mutationId: "rc-9");
        var completed = await GetProgressAsync(client, id);
        await PutProgressAsync(client, id, 4, ifMatch: completed.Revision, mutationId: "rc-4");

        // Ensure the preference starts off for this assertion.
        await SetPreferenceAsync(client, false);
        var off = await GetProgressAsync(client, id);
        Assert.Equal(4, off.PageIndex);
        Assert.Equal(4, off.OpenPageIndex);  // option off -> resume the re-read spot

        await SetPreferenceAsync(client, true);
        var on = await GetProgressAsync(client, id);
        Assert.Equal(4, on.PageIndex);        // stored Ordinal untouched (non-destructive)
        Assert.Equal(0, on.OpenPageIndex);     // option on -> page 1

        await SetPreferenceAsync(client, false); // restore shared-user state for other tests
    }

    [Fact]
    public async Task Preferences_AlwaysOpenReadFromStart_RoundTrips()
    {
        var client = await ClientAsync();
        try
        {
            await SetPreferenceAsync(client, true);
            var prefs = await client.GetFromJsonAsync<UserPreferencesDto>("/api/v1/reading/preferences", TestJson.Web);
            Assert.True(prefs!.AlwaysOpenReadFromStart);
        }
        finally { await SetPreferenceAsync(client, false); }
    }

    private static async Task SetPreferenceAsync(HttpClient client, bool on)
    {
        var current = await client.GetFromJsonAsync<UserPreferencesDto>("/api/v1/reading/preferences", TestJson.Web);
        var body = current! with { AlwaysOpenReadFromStart = on };
        var resp = await client.PutAsJsonAsync("/api/v1/reading/preferences", body);
        resp.EnsureSuccessStatusCode();
    }
}
