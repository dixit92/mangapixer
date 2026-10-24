namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Metadata.Freshness;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests for 1.47.0 (owner decision 10a): the admin list of MangaDex records the latest-upload sweep stopped finding
/// (<c>MissingSince</c> / <c>MissingCount</c>, until now read nowhere). Stored rows only - every host fails on any outgoing request -, a
/// record is listed only while a Confirmed / Auto series in a metadata-enabled library still uses it, oldest first, and the route is
/// admin-only (anonymous 401, a reader 403). Synthetic names only.
/// </summary>
[Trait("Category", "Http")]
public sealed class TakenDownRecordsHttpTests
{
    private const string Route = "/api/v1/admin/metadata/taken-down";
    private static readonly DateTimeOffset Base = new(2026, 10, 1, 6, 0, 0, TimeSpan.Zero);

    private static async Task SeedAsync(MetadataNetworkWebApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var on = new LibraryEntity { PublicId = "tdOn", DisplayName = "Takedown On", RootPath = "/synthetic/td1", CreatedAt = Base, MetadataEnabled = true };
        var off = new LibraryEntity { PublicId = "tdOff", DisplayName = "Takedown Off", RootPath = "/synthetic/td2", CreatedAt = Base, MetadataEnabled = false };
        db.Libraries.AddRange(on, off);
        await db.SaveChangesAsync();

        // (series folder, library, link state, MangaDex record title, MissingSince days after Base or null, sweeps)
        var cases = new (string Pub, string Name, LibraryEntity Lib, SeriesLinkState State, string Title, int? Since, int Sweeps)[]
        {
            ("tdNew", "Shelf New", on, SeriesLinkState.Confirmed, "Newer Takedown", 9, 2),
            ("tdOld", "Shelf Old", on, SeriesLinkState.Auto, "Older Takedown", 3, 7),
            ("tdOldB", "Shelf Old Twin", on, SeriesLinkState.Confirmed, "Older Takedown", 3, 7), // a second series on the same record
            ("tdFine", "Shelf Fine", on, SeriesLinkState.Confirmed, "Found Again", null, 0),
            ("tdReview", "Shelf Review", on, SeriesLinkState.NeedsReview, "Unlinked Takedown", 5, 4),
            ("tdOff", "Shelf Off", off, SeriesLinkState.Confirmed, "Disabled Library Takedown", 5, 4),
        };
        var mangaDexByTitle = new Dictionary<string, MetadataRecordEntity>(StringComparer.Ordinal);
        var next = 100;
        foreach (var c in cases)
        {
            var folder = new CatalogNodeEntity
            {
                PublicId = c.Pub,
                LibraryId = c.Lib.Id,
                Kind = (int)CatalogNodeKind.Folder,
                DisplayName = c.Name,
                RelativePath = c.Pub,
                PathKey = c.Pub,
                SortKey = "0" + c.Name,
                CreatedAt = Base,
            };
            db.CatalogNodes.Add(folder);
            var series = new MetadataRecordEntity { PublicId = "mu" + c.Pub, Provider = "mangaupdates", ExternalId = (next++).ToString(System.Globalization.CultureInfo.InvariantCulture), Title = "Series " + c.Pub, FetchedAt = Base };
            db.MetadataRecords.Add(series);
            if (!mangaDexByTitle.TryGetValue(c.Title, out var md))
            {
                md = new MetadataRecordEntity { PublicId = "md" + c.Pub, Provider = "mangadex", ExternalId = $"00000000-0000-4000-8000-{next++:000000000000}", Title = c.Title, FetchedAt = Base };
                db.MetadataRecords.Add(md);
                mangaDexByTitle[c.Title] = md;
            }
            await db.SaveChangesAsync();
            db.NodeSeriesLinks.Add(new NodeSeriesLinkEntity
            {
                NodeId = folder.Id,
                LibraryId = c.Lib.Id,
                State = (int)c.State,
                RecordId = series.Id,
                CreatedAt = Base,
                UpdatedAt = Base,
            });
            db.MetadataCompanions.Add(new MetadataCompanionEntity
            {
                RecordId = series.Id,
                Provider = "mangadex",
                CompanionRecordId = md.Id,
                State = (int)CompanionState.Auto,
                CheckedAt = Base,
            });
            await db.SaveChangesAsync();
            if (!db.MangaDexLatestUploads.Any(u => u.RecordId == md.Id))
            {
                db.MangaDexLatestUploads.Add(new MangaDexLatestUploadEntity
                {
                    RecordId = md.Id,
                    ChapterId = "10000000-0000-4000-8000-000000000001",
                    SeenAt = Base,
                    CheckedAt = Base.AddDays(1),
                    MissingSince = c.Since is { } d ? Base.AddDays(d) : null,
                    MissingCount = c.Sweeps,
                });
                await db.SaveChangesAsync();
            }
        }
    }

    [Fact]
    public async Task TheList_HoldsTheTakenDownRecordsOfLinkedSeries_OldestFirst_SendingNothing()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var response = await admin.GetAsync(Route);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<TakenDownRecordsDto>(TestJson.Web))!;

        Assert.Equal((2, TakenDownRecordsService.Limit), (dto.Total, dto.Limit));
        Assert.Equal(["Older Takedown", "Newer Takedown"], dto.Items.Select(i => i.Title));
        var older = dto.Items[0];
        Assert.Equal((Base.AddDays(3), (DateTimeOffset?)Base.AddDays(1), 7), (older.Since, older.LastSeen, older.Sweeps));
        // Both series that use the record, by name; the other record has its one.
        Assert.Equal(["Shelf Old", "Shelf Old Twin"], older.Series.Select(s => s.DisplayName));
        Assert.Equal(("tdOld", "tdOn"), (older.Series[0].NodeId, older.Series[0].LibraryId));
        Assert.Equal(0, older.MoreSeries);
        Assert.Equal(["Shelf New"], dto.Items[1].Series.Select(s => s.DisplayName));
        Assert.Equal(0, factory.Handler.CallCount);
    }

    [Fact]
    public async Task TheList_IsEmpty_WhenNoRecordIsMissing()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();

        var dto = (await (await admin.GetAsync(Route)).Content.ReadFromJsonAsync<TakenDownRecordsDto>(TestJson.Web))!;

        Assert.Equal(0, dto.Total);
        Assert.Empty(dto.Items);
    }

    [Fact]
    public async Task TheRoute_IsAdminOnly_AnonymousIs401_AReaderIs403()
    {
        using var factory = new MetadataNetworkWebApplicationFactory(failOnAnyRequest: true);
        await SeedAsync(factory);
        using var anon = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync(Route)).StatusCode);

        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = "tdreader", IsAdmin = false });
        created.EnsureSuccessStatusCode();
        var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        (await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate", new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        using var reader = factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "tdreader", Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.GetAsync(Route)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync(Route)).StatusCode);
        Assert.Equal(0, factory.Handler.CallCount);
    }
}
