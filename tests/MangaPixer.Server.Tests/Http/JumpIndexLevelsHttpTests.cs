using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace com.lifepixer.mangapixer.Tests.Server.Http;

/// <summary>
/// HTTP tests for the 1.41.0 smart jump rail over the public surface: the rail of a FOLDER (not only the library root), the rail of the
/// name sort DESCENDING, and round number ranges for numbered names - each checked by passing every bucket's cursor to the browse
/// endpoint and reading the row it lands on. Plus the parent checks (unknown / other library -> 404) and the unchanged access rule.
/// Keys come from <see cref="SortKey.ForNode"/>, the scanner's encoder. Each test seeds its own library.
/// </summary>
public sealed class JumpIndexLevelsHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private readonly MangaPixerWebApplicationFactory _factory;

    public JumpIndexLevelsHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private sealed class Seeder(MangaPixerDbContext db, long libraryId)
    {
        public async Task<string> AddAsync(CatalogNodeKind kind, string name, string? parentPublicId = null)
        {
            long? parentId = null;
            if (parentPublicId is not null)
                parentId = db.CatalogNodes.Single(n => n.PublicId == parentPublicId).Id;
            var publicId = "jl" + Guid.NewGuid().ToString("N")[..12];
            db.CatalogNodes.Add(new CatalogNodeEntity
            {
                PublicId = publicId,
                LibraryId = libraryId,
                ParentId = parentId,
                Kind = (int)kind,
                DisplayName = name,
                RelativePath = publicId,
                PathKey = publicId,
                SortKey = SortKey.ForNode(kind, name),
                Availability = (int)CatalogNodeAvailability.Available,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            return publicId;
        }
    }

    /// <summary>A new library, seeded by <paramref name="seed"/>; returns its public id.</summary>
    private async Task<string> SeedLibraryAsync(Func<Seeder, Task> seed)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var publicId = "jlib" + Guid.NewGuid().ToString("N")[..10];
        var library = new LibraryEntity
        {
            PublicId = publicId,
            DisplayName = "Jump Levels " + publicId,
            RootPath = "/tmp/" + publicId,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        await seed(new Seeder(db, library.Id));
        return publicId;
    }

    private static async Task<JumpIndexDto> GetIndexAsync(HttpClient client, string libraryId, string query = "")
    {
        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/jump-index{query}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var index = await response.Content.ReadFromJsonAsync<JumpIndexDto>(JsonOptions);
        Assert.NotNull(index);
        return index!;
    }

    private static async Task<string> FirstLandedAsync(HttpClient client, string libraryId, string? parentId, string? cursor, string direction)
    {
        var url = $"/api/v1/libraries/{libraryId}/browse?sort=name&direction={direction}&pageSize=5";
        if (parentId is not null) url += $"&parentId={parentId}";
        if (cursor is not null) url += $"&cursor={Uri.EscapeDataString(cursor)}";
        var page = await client.GetFromJsonAsync<PageResponse<CatalogNodeDto>>(url, JsonOptions);
        Assert.NotNull(page);
        Assert.NotEmpty(page!.Items);
        return page.Items[0].DisplayName;
    }

    [Fact]
    public async Task FolderRail_ListsTheFoldersOwnChildren_AndEachCursorLandsInsideTheFolder()
    {
        string folder = "";
        var libraryId = await SeedLibraryAsync(async s =>
        {
            folder = await s.AddAsync(CatalogNodeKind.Folder, "Story Folder");
            await s.AddAsync(CatalogNodeKind.Folder, "Zed Folder");
            foreach (var name in new[] { "Gamma Tale.cbz", "Alpha Tale.cbz", "Delta Tale.cbz", "Beta Tale.cbz", "Bravo Tale.cbz" })
                await s.AddAsync(CatalogNodeKind.Archive, name, folder);
        });
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var index = await GetIndexAsync(client, libraryId, $"?parentId={folder}");

        Assert.Equal(["A", "B", "D", "G"], index.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([1, 2, 1, 1], index.Buckets.Select(b => b.Count).ToArray());
        string[] expected = ["Alpha Tale.cbz", "Beta Tale.cbz", "Delta Tale.cbz", "Gamma Tale.cbz"];
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], await FirstLandedAsync(client, libraryId, folder, index.Buckets[i].FirstCursor, "asc"));
    }

    [Fact]
    public async Task DescendingRail_IsReversed_AndEachCursorLandsOnTheBucketsFirstRowInDescendingOrder()
    {
        var libraryId = await SeedLibraryAsync(async s =>
        {
            foreach (var name in new[] { "Alpha", "Apple", "Beta", "Gamma", "Gold" })
                await s.AddAsync(CatalogNodeKind.Archive, name);
        });
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var index = await GetIndexAsync(client, libraryId, "?direction=desc");

        Assert.Equal(["G", "B", "A"], index.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([2, 1, 2], index.Buckets.Select(b => b.Count).ToArray());
        Assert.Null(index.Buckets[0].FirstCursor);
        string[] expected = ["Gold", "Beta", "Apple"];
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], await FirstLandedAsync(client, libraryId, null, index.Buckets[i].FirstCursor, "desc"));
    }

    [Fact]
    public async Task BareNumberedArchives_GetRoundRanges_AndEachRangeLandsOnItsFirstNumber()
    {
        // The owner's example: archives 0000..0321 -> 0, 50, 100 ... 300 (not one "#" chip).
        var libraryId = await SeedLibraryAsync(async s =>
        {
            for (var n = 0; n <= 321; n++)
                await s.AddAsync(CatalogNodeKind.Archive, $"{n:0000}.cbz");
        });
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var index = await GetIndexAsync(client, libraryId);

        Assert.Equal(["0", "50", "100", "150", "200", "250", "300"], index.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([50, 50, 50, 50, 50, 50, 22], index.Buckets.Select(b => b.Count).ToArray());
        for (var i = 0; i < index.Buckets.Count; i++)
            Assert.Equal($"{i * 50:0000}.cbz", await FirstLandedAsync(client, libraryId, null, index.Buckets[i].FirstCursor, "asc"));

        var desc = await GetIndexAsync(client, libraryId, "?direction=desc");
        Assert.Equal(["300", "250", "200", "150", "100", "50", "0"], desc.Buckets.Select(b => b.Label).ToArray());
        string[] expected = ["0321.cbz", "0299.cbz", "0249.cbz", "0199.cbz", "0149.cbz", "0099.cbz", "0049.cbz"];
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], await FirstLandedAsync(client, libraryId, null, desc.Buckets[i].FirstCursor, "desc"));
    }

    [Fact]
    public async Task PrefixedChapterFolder_IgnoresTheCommonPrefix_AndKeepsAnExtraAtItsPosition()
    {
        string folder = "";
        var libraryId = await SeedLibraryAsync(async s =>
        {
            folder = await s.AddAsync(CatalogNodeKind.Folder, "Ranged Saga");
            for (var n = 1; n <= 60; n++)
                await s.AddAsync(CatalogNodeKind.Archive, $"Ranged Saga - Ch. {n:000}.cbz", folder);
            await s.AddAsync(CatalogNodeKind.Archive, "Ranged Saga - Ch. 010.5.cbz", folder);
            await s.AddAsync(CatalogNodeKind.Archive, "Ranged Saga - Oneshot.cbz", folder);
        });
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var index = await GetIndexAsync(client, libraryId, $"?parentId={folder}");

        // 62 rows -> at most 12 ranges: step 10. The .5 chapter is in its whole number's range; the one-shot (no number) sorts after
        // the numbers and keeps its own chip there.
        Assert.Equal(["0", "10", "20", "30", "40", "50", "60", "Other"], index.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal([9, 11, 10, 10, 10, 10, 1, 1], index.Buckets.Select(b => b.Count).ToArray());
        string[] expected =
        [
            // "010.5" lists before "010" (natural order compares ".5.cbz" with ".cbz"), so it starts the range 10-19.
            "Ranged Saga - Ch. 001.cbz", "Ranged Saga - Ch. 010.5.cbz", "Ranged Saga - Ch. 020.cbz", "Ranged Saga - Ch. 030.cbz",
            "Ranged Saga - Ch. 040.cbz", "Ranged Saga - Ch. 050.cbz", "Ranged Saga - Ch. 060.cbz", "Ranged Saga - Oneshot.cbz",
        ];
        for (var i = 0; i < expected.Length; i++)
            Assert.Equal(expected[i], await FirstLandedAsync(client, libraryId, folder, index.Buckets[i].FirstCursor, "asc"));
    }

    [Fact]
    public async Task VolumesView_HidesTheRail_AndTheFlatListGetsOne()
    {
        // Chapters that state their volume: the folder opens in the Volumes view (on by default), which pages by volume.
        string folder = "";
        var libraryId = await SeedLibraryAsync(async s =>
        {
            folder = await s.AddAsync(CatalogNodeKind.Folder, "Stacked Saga");
            for (var c = 1; c <= 12; c++)
                await s.AddAsync(CatalogNodeKind.Archive, $"Stacked Saga v{(c - 1) / 4 + 1:00} c{c:000}.cbz", folder);
        });
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        Assert.Empty((await GetIndexAsync(client, libraryId, $"?parentId={folder}")).Buckets);
        var flat = await GetIndexAsync(client, libraryId, $"?parentId={folder}&group=flat");
        Assert.Equal(["0", "5", "10"], flat.Buckets.Select(b => b.Label).ToArray());
        Assert.Equal("Stacked Saga v03 c010.cbz", await FirstLandedAsync(client, libraryId, folder, flat.Buckets[2].FirstCursor, "asc&group=flat"));
    }

    [Fact]
    public async Task UnknownParent_Returns404()
    {
        var libraryId = await SeedLibraryAsync(s => s.AddAsync(CatalogNodeKind.Archive, "Alpha"));
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/jump-index?parentId=doesnotexist");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ParentOfAnotherLibrary_Returns404()
    {
        string otherFolder = "";
        await SeedLibraryAsync(async s =>
        {
            otherFolder = await s.AddAsync(CatalogNodeKind.Folder, "Elsewhere");
            await s.AddAsync(CatalogNodeKind.Archive, "Alpha.cbz", otherFolder);
            await s.AddAsync(CatalogNodeKind.Archive, "Beta.cbz", otherFolder);
        });
        var libraryId = await SeedLibraryAsync(s => s.AddAsync(CatalogNodeKind.Archive, "Alpha"));
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/jump-index?parentId={otherFolder}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task ReaderWithoutAccess_GetsAnEmptyIndex_AtTheRootAndInAFolder_AsBefore()
    {
        string folder = "";
        var libraryId = await SeedLibraryAsync(async s =>
        {
            folder = await s.AddAsync(CatalogNodeKind.Folder, "Private Folder");
            await s.AddAsync(CatalogNodeKind.Archive, "Alpha.cbz", folder);
            await s.AddAsync(CatalogNodeKind.Archive, "Beta.cbz", folder);
        });
        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var reader = await CreateAndLoginReaderAsync(admin, "jumpreader" + Guid.NewGuid().ToString("N")[..6]);

        Assert.Empty((await GetIndexAsync(reader, libraryId)).Buckets);
        Assert.Empty((await GetIndexAsync(reader, libraryId, $"?parentId={folder}&direction=desc")).Buckets);
        Assert.Equal(2, (await GetIndexAsync(admin, libraryId, $"?parentId={folder}")).Buckets.Count);
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
