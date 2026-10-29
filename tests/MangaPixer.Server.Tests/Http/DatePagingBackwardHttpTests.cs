namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Ordering;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// 1.50.0 lane D through the public surface: under the date sorts a time chip of the jump index lands browse mid-list, the page reports
/// <c>hasPrevious</c> / <c>prevCursor</c>, and <c>before=prevCursor</c> returns the rows above it - the request the web's
/// <c>loadPrevious</c> already sends for any sort. Rows years apart, so the chips differ whatever the wall clock says.
/// </summary>
public sealed class DatePagingBackwardHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public DatePagingBackwardHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
    }

    private static readonly System.Text.Json.JsonSerializerOptions s_json = CreateJsonOptions();

    private static System.Text.Json.JsonSerializerOptions CreateJsonOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
        return options;
    }

    /// <summary>A library root with folders and archives spread over the years; two folders without a descendant (recentlyUpdated's tail).</summary>
    private async Task<string> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = new LibraryEntity
        {
            PublicId = "dpb" + Guid.NewGuid().ToString("N")[..8],
            DisplayName = "Date Paging Library",
            RootPath = "/synthetic/date-paging",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        CatalogNodeEntity Node(CatalogNodeKind kind, string name, int yearsAgo, int? latestYearsAgo = null)
        {
            var id = library.PublicId + (kind == CatalogNodeKind.Folder ? "f" : "a") + name.Replace(" ", "", StringComparison.Ordinal);
            return new CatalogNodeEntity
            {
                PublicId = id,
                LibraryId = library.Id,
                Kind = (int)kind,
                DisplayName = name,
                RelativePath = id,
                PathKey = id,
                SortKey = SortKey.ForNode(kind, name),
                Availability = (int)CatalogNodeAvailability.Available,
                CreatedAt = now.AddDays(-365.25 * yearsAgo),
                LatestDescendantAddedAt = latestYearsAgo is { } y ? now.AddDays(-365.25 * y) : null,
            };
        }

        // Archives: two per period (ties on the period, not the instant), periods 0, 2, 4 and 6 years back.
        foreach (var (name, years) in new[] { ("Story A", 0), ("Story B", 0), ("Story C", 2), ("Story D", 2), ("Story E", 4), ("Story F", 4),
                     ("Story G", 6), ("Story H", 6) })
            db.CatalogNodes.Add(Node(CatalogNodeKind.Archive, name, years));
        // Folders: newest descendants spread too; two have none.
        db.CatalogNodes.Add(Node(CatalogNodeKind.Folder, "Series A", 1, 1));
        db.CatalogNodes.Add(Node(CatalogNodeKind.Folder, "Series B", 3, 3));
        db.CatalogNodes.Add(Node(CatalogNodeKind.Folder, "Series C", 5, 5));
        db.CatalogNodes.Add(Node(CatalogNodeKind.Folder, "Series D", 7));
        db.CatalogNodes.Add(Node(CatalogNodeKind.Folder, "Series E", 7));
        await db.SaveChangesAsync();
        return library.PublicId;
    }

    private static async Task<PageResponse<CatalogNodeDto>> BrowseAsync(HttpClient client, string libraryId, string query)
    {
        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/browse?{query}");
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PageResponse<CatalogNodeDto>>(s_json))!;
    }

    private static async Task<List<string>> ListingAsync(HttpClient client, string libraryId, string sort)
    {
        var ids = new List<string>();
        string? cursor = null;
        while (true)
        {
            var page = await BrowseAsync(client, libraryId,
                $"sort={sort}&pageSize=4" + (cursor is null ? "" : "&cursor=" + Uri.EscapeDataString(cursor)));
            ids.AddRange(page.Items.Select(i => i.Id));
            if (!page.HasMore)
                return ids;
            cursor = page.NextCursor;
        }
    }

    [Theory]
    [InlineData("recentlyAdded")]
    [InlineData("recentlyUpdated")]
    public async Task FromEveryTimeChip_TheListScrollsUp_ToTheStart(string sort)
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var libraryId = await SeedAsync();
        var listing = await ListingAsync(client, libraryId, sort);
        Assert.Equal(13, listing.Count);

        var response = await client.GetAsync($"/api/v1/libraries/{libraryId}/jump-index?sort={sort}");
        response.EnsureSuccessStatusCode();
        var rail = (await response.Content.ReadFromJsonAsync<JumpIndexDto>(s_json))!;
        Assert.Equal("dates", rail.Mode);
        Assert.True(rail.Buckets.Count >= 4, "expected several time chips");

        var chipsWithCursor = 0;
        foreach (var chip in rail.Buckets.Where(b => b.FirstCursor is not null))
        {
            chipsWithCursor++;
            var position = listing.IndexOf(chip.FirstNodeId!);
            Assert.True(position > 0);

            // Land on the chip (the web's jump), then scroll up with prevCursor until nothing is above.
            var landing = await BrowseAsync(client, libraryId, $"sort={sort}&pageSize=2&cursor={Uri.EscapeDataString(chip.FirstCursor!)}");
            Assert.Equal(chip.FirstNodeId, landing.Items[0].Id);
            Assert.True(landing.HasPrevious);

            var above = new List<string>();
            var before = landing.PrevCursor;
            while (before is not null)
            {
                var back = await BrowseAsync(client, libraryId, $"sort={sort}&pageSize=2&before={Uri.EscapeDataString(before)}");
                Assert.NotEmpty(back.Items);
                Assert.True(back.HasMore);
                above.InsertRange(0, back.Items.Select(i => i.Id));
                before = back.HasPrevious ? back.PrevCursor : null;
                Assert.True(above.Count <= listing.Count, "backward paging must end");
            }
            Assert.Equal(listing.Take(position), above);
        }
        Assert.True(chipsWithCursor >= 3);
    }
}
