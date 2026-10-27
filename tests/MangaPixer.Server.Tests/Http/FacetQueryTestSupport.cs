namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net.Http.Json;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server;
using com.lifepixer.mangapixer.Server.Features.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Seeds the tag index tables DIRECTLY (1.45.0 lane Q: the builder is lane F's, in parallel), plus libraries, nodes and readers, for the
/// tag query's HTTP tests. Synthetic names only (well-known public titles / lookalikes).
/// </summary>
internal sealed class FacetQueryTestSupport
{
    private readonly IServiceProvider _services;
    private readonly WebApplicationFactory<Program> _factory;

    public FacetQueryTestSupport(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _services = factory.Services;
    }

    public async Task<T> WithDbAsync<T>(Func<MangaPixerDbContext, Task<T>> work)
    {
        using var scope = _services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await work(db);
    }

    public Task WithDbAsync(Func<MangaPixerDbContext, Task> work) => WithDbAsync(async db =>
    {
        await work(db);
        return 0;
    });

    /// <summary>Marks every cached count stale, as the index writer does after a write.</summary>
    public void Bump() => _services.GetRequiredService<IFacetIndexGeneration>().Bump();

    public Task<long> LibraryAsync(string publicId, string name) => WithDbAsync(async db =>
    {
        var library = new LibraryEntity { PublicId = publicId, DisplayName = name, RootPath = "/tmp/" + publicId, CreatedAt = DateTimeOffset.UtcNow };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();
        return library.Id;
    });

    public Task<long> NodeAsync(long libraryId, long? parentId, string publicId, string name, bool folder = true, bool tombstoned = false) =>
        WithDbAsync(async db =>
        {
            var node = new CatalogNodeEntity
            {
                PublicId = publicId,
                LibraryId = libraryId,
                ParentId = parentId,
                Kind = folder ? (int)CatalogNodeKind.Folder : (int)CatalogNodeKind.Archive,
                DisplayName = name,
                RelativePath = publicId + "/" + name,
                PathKey = publicId + "/" + name,
                SortKey = (folder ? "0" : "1") + name.ToLowerInvariant(),
                Availability = tombstoned ? (int)CatalogNodeAvailability.Tombstoned : 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            return node.Id;
        });

    public Task<long> WorkAsync(string key, string title, long? recordId = null) => WithDbAsync(async db =>
    {
        var work = new FacetWorkEntity
        {
            WorkKey = key,
            Kind = recordId is null ? (int)FacetWorkKind.Node : (int)FacetWorkKind.Record,
            RecordId = recordId,
            Title = title,
            SortKey = title.ToLowerInvariant(),
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.FacetWorks.Add(work);
        await db.SaveChangesAsync();
        return work.Id;
    });

    /// <summary>1.49.0 (lane T): a stored web record with alternative titles and categories (<c>[{name, votes}]</c>).</summary>
    public Task<long> RecordAsync(string externalId, string title, string? altTitlesJson = null, string? categoriesJson = null) =>
        WithDbAsync(async db =>
        {
            var record = new MetadataRecordEntity
            {
                PublicId = "rec" + externalId,
                Provider = "mangaupdates",
                ExternalId = externalId,
                Title = title,
                AltTitlesJson = altTitlesJson,
                CategoriesJson = categoriesJson,
                FetchedAt = DateTimeOffset.UtcNow,
            };
            db.MetadataRecords.Add(record);
            await db.SaveChangesAsync();
            return record.Id;
        });

    public Task<long> CreatorAsync(string displayName, IEnumerable<string> names, long? mergedInto = null) => WithDbAsync(async db =>
    {
        var creator = new CreatorEntity
        {
            PublicId = FacetPublicIds.NewCreatorId(),
            DisplayName = displayName,
            DisplayNameSource = (int)CreatorDisplayNameSource.MostUsed,
            MergedIntoId = mergedInto,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Creators.Add(creator);
        await db.SaveChangesAsync();
        foreach (var name in names.Append(displayName).Distinct())
        {
            db.CreatorNames.Add(new CreatorNameEntity
            {
                CreatorId = creator.Id,
                Name = name,
                NameKey = com.lifepixer.mangapixer.Core.Metadata.DeclaredFactsComparer.NameKey(name),
                Source = (int)CreatorNameSource.ComicInfo,
            });
        }
        await db.SaveChangesAsync();
        return creator.Id;
    });

    /// <summary>A value; returns (internal id, public id).</summary>
    public Task<(long Id, string PublicId)> ValueAsync(FacetBucket bucket, string valueKey, string display, long? creatorId = null) =>
        WithDbAsync(async db =>
        {
            var value = new FacetValueEntity
            {
                PublicId = FacetPublicIds.NewValueId(),
                Bucket = (int)bucket,
                ValueKey = valueKey,
                Display = display,
                CreatorId = creatorId,
            };
            db.FacetValues.Add(value);
            await db.SaveChangesAsync();
            return (value.Id, value.PublicId);
        });

    public Task EntryAsync(long workId, long nodeId, long libraryId, long valueId, FacetSources sources = FacetSources.Web,
        FacetRoles roles = FacetRoles.None, bool conflict = false) => WithDbAsync(async db =>
        {
            db.FacetEntries.Add(new FacetEntryEntity
            {
                WorkId = workId,
                LocationNodeId = nodeId,
                LibraryId = libraryId,
                ValueId = valueId,
                Sources = (int)sources,
                Roles = (int)roles,
                Conflict = conflict,
            });
            await db.SaveChangesAsync();
        });

    /// <summary>An activated reader granted <paramref name="grantLibraryIds"/>; returns the signed-in client and the user id.</summary>
    public async Task<(HttpClient Client, long UserId)> ReaderAsync(HttpClient admin, string username, params long[] grantLibraryIds)
    {
        var created = await admin.PostAsJsonAsync("/api/v1/admin/users", new CreateUserRequest { Username = username, IsAdmin = false });
        created.EnsureSuccessStatusCode();
        var url = (await created.Content.ReadFromJsonAsync<CreateUserResponse>(TestJson.Web))!.ActivationUrl!;
        var token = Uri.UnescapeDataString(url[(url.IndexOf("token=", StringComparison.Ordinal) + 6)..]);
        (await _factory.CreateClient().PostAsJsonAsync("/api/v1/auth/activate",
            new ActivateAccountRequest { Token = token, Password = "ReaderPassword123!" })).EnsureSuccessStatusCode();

        var userId = await WithDbAsync(async db =>
        {
            var id = await db.Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).Select(u => u.Id).SingleAsync();
            foreach (var lib in grantLibraryIds)
                db.LibraryGrants.Add(new LibraryGrantEntity { UserId = id, LibraryId = lib, GrantedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            return id;
        });

        var client = _factory.CreateClient();
        (await client.PostAsJsonAsync("/api/v1/auth/login", new LoginRequest { Username = username, Password = "ReaderPassword123!" }))
            .EnsureSuccessStatusCode();
        var csrf = await (await client.GetAsync("/api/v1/auth/csrf")).Content.ReadFromJsonAsync<CsrfTokenDto>();
        client.DefaultRequestHeaders.Add("X-MangaPixer-Csrf", csrf!.Token);
        return (client, userId);
    }

    public Task<long> UserIdAsync(string username) => WithDbAsync(db =>
        db.Users.Where(u => u.NormalizedUserName == username.ToUpperInvariant()).Select(u => u.Id).SingleAsync());

    /// <summary>Replaces a user's Private libraries.</summary>
    public Task SetPrivateAsync(long userId, params long[] libraryIds) => WithDbAsync(async db =>
    {
        db.PrivateLibraries.RemoveRange(db.PrivateLibraries.Where(p => p.UserId == userId));
        foreach (var lib in libraryIds)
            db.PrivateLibraries.Add(new PrivateLibraryEntity { UserId = userId, LibraryId = lib, MarkedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
    });

    public Task SetSeriesInfoHiddenAsync(long libraryId, bool hidden) => WithDbAsync(async db =>
    {
        var library = await db.Libraries.SingleAsync(l => l.Id == libraryId);
        library.MetadataSeriesInfoHidden = hidden;
        await db.SaveChangesAsync();
    });

    public Task SetSeriesInfoHiddenEverywhereAsync(bool hidden) => WithDbAsync(async db =>
    {
        var settings = await db.AppSettings.FirstOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
        if (settings is null)
        {
            settings = new AppSettingsEntity { Id = AppSettingsEntity.SingletonId };
            db.AppSettings.Add(settings);
        }
        settings.MetadataSeriesInfoHidden = hidden;
        await db.SaveChangesAsync();
    });

    public static async Task<T> GetAsync<T>(HttpClient client, string url, bool incognito = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (incognito)
            request.Headers.Add("X-Incognito", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<T>(TestJson.Web))!;
    }

    public static async Task<string> GetRawAsync(HttpClient client, string url, bool incognito = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (incognito)
            request.Headers.Add("X-Incognito", "1");
        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    public static async Task<System.Net.HttpStatusCode> StatusAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url);
        return response.StatusCode;
    }

    /// <summary>A chip's count as the panel shows it (null = quiet or absent).</summary>
    public static FacetChipDto? Chip(InfoPanelDto panel, string valueId) =>
        panel.Buckets.SelectMany(b => b.Chips).FirstOrDefault(c => c.Id == valueId);
}
