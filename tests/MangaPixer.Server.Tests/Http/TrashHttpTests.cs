namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Features.Trash;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

/// <summary>
/// HTTP tests for the admin trash (1.31.0): <c>/api/v1/admin/trash</c> is admin-only; the overview previews what would go,
/// the settings validate the retention presets, "Empty trash now" purges eligible tombstones (and refuses a held library
/// until the admin confirms), "Clean bundles now" removes unreferenced data-root files, and the registered hosted service's
/// daily pass runs only while automatic cleaning is on.
/// </summary>
[Collection("HttpSerial")]
public sealed class TrashHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static async Task<T> OkAsync<T>(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    /// <summary>A library with <paramref name="present"/> present archives and <paramref name="old"/> tombstones from 40 days ago.</summary>
    private async Task<List<long>> SeedAsync(string publicId, int present, int old)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity { PublicId = publicId, DisplayName = "Trash " + publicId, RootPath = "/synthetic/" + publicId, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        var ids = new List<long>();
        for (var i = 0; i < present + old; i++)
        {
            var gone = i >= present;
            var node = new CatalogNodeEntity
            {
                PublicId = $"{publicId}n{i}",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = $"Item {i}",
                RelativePath = $"{publicId}/{i}.cbz",
                PathKey = $"{publicId}/{i}.cbz",
                SortKey = $"1item{i}",
                Availability = gone ? 5 : 0,
                TombstonedAt = gone ? DateTimeOffset.UtcNow.AddDays(-40) : null,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(new ArchiveItemEntity { NodeId = node.Id, ContentVersion = 1, AnalysisState = 0 });
            if (gone)
                ids.Add(node.Id);
        }
        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<bool> ExistsAsync(long id)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().CatalogNodes.AnyAsync(n => n.Id == id);
    }

    private async Task<HttpClient> SignInAsync(string username, string password)
    {
        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = password })).EnsureSuccessStatusCode();
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        return client;
    }

    [Fact]
    public async Task TheTrash_IsAdminOnly()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "trashreader", Password = "TargetPass123!", IsAdmin = false }))
            .EnsureSuccessStatusCode();
        // A new user signs in, changes the temporary password, and signs in again (as every reader does).
        var first = await SignInAsync("trashreader", "TargetPass123!");
        (await first.PostAsJsonAsync("/api/v1/auth/change-password", new ChangePasswordRequest { CurrentPassword = "TargetPass123!", NewPassword = "TargetPassNew123!" }))
            .EnsureSuccessStatusCode();
        var reader = await SignInAsync("trashreader", "TargetPassNew123!");

        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync("/api/v1/admin/trash")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync("/api/v1/admin/trash/empty", new EmptyTrashRequest())).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsync("/api/v1/admin/trash/clean-bundles", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { AutomaticCleaning = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _factory.CreateClient().GetAsync("/api/v1/admin/trash")).StatusCode);
    }

    [Fact]
    public async Task Overview_Settings_EmptyAndCleanBundles_ThroughTheApi()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var gone = await SeedAsync("tlib", present: 4, old: 2);
        await SeedAsync("tburst", present: 1, old: 2);

        var overview = await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"));
        Assert.Equal((false, 30, 4), (overview.Settings.AutomaticCleaning, overview.Settings.RetentionDays, overview.Settings.AutomaticHour));
        Assert.Equal([1, 7, 30, 90, 365], overview.Settings.AllowedRetentionDays);
        var rows = overview.Libraries.ToDictionary(l => l.LibraryId);
        Assert.Equal((2, (string?)null), (rows["tlib"].Eligible.Nodes, rows["tlib"].Hold));
        Assert.Equal((2, "burst", true), (rows["tburst"].Eligible.Nodes, rows["tburst"].Hold, rows["tburst"].HoldReleasable));
        Assert.Equal(2, overview.Total.Nodes); // the held library is not in the total

        // Settings: only the five presets.
        var bad = await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { RetentionDays = 14 });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
        Assert.Equal("invalid_retention", (await bad.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        var settings = await OkAsync<TrashSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { RetentionDays = 365 }));
        Assert.Equal(365, settings.RetentionDays);
        Assert.Equal(0, (await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"))).Total.Nodes); // 40 days < a year
        await OkAsync<TrashSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { RetentionDays = 30 }));

        // The automatic run's hour (owner, 1.31.0: scheduled job times are the admin's): 0-23, default 4.
        var badHour = await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { AutomaticHour = 24 });
        Assert.Equal(HttpStatusCode.BadRequest, badHour.StatusCode);
        Assert.Equal("invalid_hour", (await badHour.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        var hour = await OkAsync<TrashSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { AutomaticHour = 22 }));
        Assert.Equal((22, 30), (hour.AutomaticHour, hour.RetentionDays)); // a null field keeps its value
        Assert.Equal(22, (await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"))).Settings.AutomaticHour);
        await OkAsync<TrashSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { AutomaticHour = 4 }));

        // Empty all: the held library stays.
        var emptied = await OkAsync<EmptyTrashResultDto>(await admin.PostAsJsonAsync("/api/v1/admin/trash/empty", new EmptyTrashRequest()));
        Assert.Equal(2, emptied.Removed.Nodes);
        Assert.Equal(("tburst", "burst"), (Assert.Single(emptied.Held).LibraryId, emptied.Held[0].Hold));
        foreach (var id in gone)
            Assert.False(await ExistsAsync(id));

        // One held library: refused until the admin confirms.
        var refused = await admin.PostAsJsonAsync("/api/v1/admin/trash/empty", new EmptyTrashRequest { LibraryId = "tburst" });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal("trash_held", (await refused.Content.ReadFromJsonAsync<ApiError>(TestJson.Web))!.Error);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.PostAsJsonAsync("/api/v1/admin/trash/empty", new EmptyTrashRequest { LibraryId = "nope" })).StatusCode);
        var released = await OkAsync<EmptyTrashResultDto>(await admin.PostAsJsonAsync("/api/v1/admin/trash/empty",
            new EmptyTrashRequest { LibraryId = "tburst", ReleaseHold = true }));
        Assert.Equal(2, released.Removed.Nodes);

        // Clean bundles: an old thumbnail no row references.
        string orphan;
        using (var scope = _factory.Services.CreateScope())
        {
            orphan = scope.ServiceProvider.GetRequiredService<ThumbnailStore>().GetThumbnailPath(987_654, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(orphan)!);
            File.WriteAllBytes(orphan, new byte[64]);
            File.SetLastWriteTimeUtc(orphan, DateTime.UtcNow.AddDays(-2));
        }
        Assert.Equal(1, (await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"))).Bundles.Files);
        var cleaned = await OkAsync<TrashFilesDto>(await admin.PostAsync("/api/v1/admin/trash/clean-bundles", null));
        Assert.Equal((1, 64L), (cleaned.Files, cleaned.Bytes));
        Assert.False(File.Exists(orphan));

        var after = await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"));
        Assert.Equal((2, false), (after.LastEmpty!.Count, after.LastEmpty.Automatic));
        Assert.Equal(1, after.LastBundleClean!.Count);
    }

    [Fact]
    public async Task TheDailyPass_IsRegistered_AndRunsOnlyWhileAutomaticCleaningIsOn()
    {
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var gone = await SeedAsync("tauto", present: 4, old: 1);

        var service = _factory.Services.GetServices<IHostedService>().OfType<TrashHostedService>().Single();
        Assert.False(await service.RunPassAsync(default)); // off by default: nothing is purged without the admin's approval
        Assert.True(await ExistsAsync(gone[0]));

        await OkAsync<TrashSettingsDto>(await admin.PutAsJsonAsync("/api/v1/admin/trash/settings", new UpdateTrashSettingsRequest { AutomaticCleaning = true }));
        Assert.True(await service.RunPassAsync(default));
        Assert.False(await ExistsAsync(gone[0]));

        var overview = await OkAsync<TrashOverviewDto>(await admin.GetAsync("/api/v1/admin/trash"));
        Assert.True(overview.Settings.AutomaticCleaning);
        Assert.Equal((1, true), (overview.LastEmpty!.Count, overview.LastEmpty.Automatic));
        Assert.True(overview.LastBundleClean!.Automatic);
    }
}
