namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// HTTP tests of "who started the last scan" (1.37.0) on the library list the admin page shows: the schedule, an admin (by user
/// name) or an API token (by its name), read from the last COMPLETED scan run; a removed account or token keeps its kind with no
/// name; readers never get the field.
/// </summary>
public sealed class LastScanStartedByHttpTests
{
    [Fact]
    public async Task LibraryList_NamesWhoStartedTheLastCompletedScan_ForAnAdminOnly()
    {
        using var factory = new MangaPixerWebApplicationFactory();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        (await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest
        {
            Username = "reader1",
            Password = "ReaderPass123!",
            IsAdmin = false,
        })).EnsureSuccessStatusCode();

        string[] names = ["By Schedule", "By Admin", "By Token", "By Removed Token", "By Removed Admin", "Never Scanned"];
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var adminId = await db.Users.Where(u => u.NormalizedUserName == "ADMIN").Select(u => u.Id).SingleAsync();
            var reader = await db.Users.SingleAsync(u => u.NormalizedUserName == "READER1");
            reader.ForcePasswordChange = false;
            var now = DateTimeOffset.UtcNow;
            var libs = names.Select((n, i) => new LibraryEntity
            {
                PublicId = "sb" + i,
                DisplayName = n,
                RootPath = "/synthetic/started-by-" + i,
                CreatedAt = now,
                LastScanCompleted = i < 5 ? now : null,
            }).ToList();
            db.Libraries.AddRange(libs);
            db.ApiTokens.Add(new ApiTokenEntity
            {
                PublicId = "tok1",
                UserId = adminId,
                Name = "MangaList",
                Prefix = "mpx_Test",
                SecretHash = new string('a', 64),
                Scopes = "library:scan",
                CreatedAt = now,
            });
            await db.SaveChangesAsync();
            db.LibraryGrants.AddRange(libs.Select(l => new LibraryGrantEntity { UserId = reader.Id, LibraryId = l.Id, GrantedAt = now }));

            void Run(LibraryEntity lib, string owner, int status, int minutesAgo) => db.ScanRuns.Add(new ScanRunEntity
            {
                LibraryId = lib.Id,
                Status = status,
                LeaseOwner = owner,
                StartedAt = now.AddMinutes(-minutesAgo),
                CompletedAt = now.AddMinutes(-minutesAgo + 1),
            });
            Run(libs[0], "server:" + adminId, 2, 30); // older: the schedule's run is the last completed one
            Run(libs[0], "scheduler", 2, 10);
            Run(libs[1], "server:" + adminId, 2, 20);
            Run(libs[1], "scheduler", 3, 5); // a later FAILED run does not count
            Run(libs[2], "token:tok1", 2, 10);
            Run(libs[3], "token:gone", 2, 10);
            Run(libs[4], "server:999999", 2, 10);
            await db.SaveChangesAsync();
        }

        var list = (await admin.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web))!;
        ScanStarterDto? By(string name) => list.Single(l => l.Name == name).LastScanStartedBy;
        Assert.Equal(new ScanStarterDto { Kind = "schedule" }, By("By Schedule"));
        Assert.Equal(new ScanStarterDto { Kind = "admin", Name = "admin" }, By("By Admin"));
        Assert.Equal(new ScanStarterDto { Kind = "token", Name = "MangaList" }, By("By Token"));
        Assert.Equal(new ScanStarterDto { Kind = "token" }, By("By Removed Token"));
        Assert.Equal(new ScanStarterDto { Kind = "admin" }, By("By Removed Admin"));
        Assert.Null(By("Never Scanned"));

        var reader = factory.CreateClient();
        (await reader.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = "reader1", Password = "ReaderPass123!" }))
            .EnsureSuccessStatusCode();
        var readerList = (await reader.GetFromJsonAsync<List<LibraryDto>>("/api/v1/libraries", TestJson.Web))!;
        Assert.Equal(names.Length, readerList.Count);
        Assert.All(readerList, l => Assert.Null(l.LastScanStartedBy));
    }
}
