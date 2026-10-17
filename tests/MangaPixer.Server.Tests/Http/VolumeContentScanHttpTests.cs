namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.WorkerProtocol;
using com.lifepixer.mangapixer.Server.Features.VolumeScan;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Tests.Server.Features.Catalog;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

/// <summary>
/// HTTP tests for the volume contents scan (1.44.0) through the running host: the HOSTED scan (registered in <c>AddVolumeScan</c>) picks
/// up a folder opened in the Volumes view, hashes its archives through <see cref="IVolumePageHasher"/> (a fake here - the real worker op
/// is covered by the process tests and the E2E), stores the answer, and the Volumes view over HTTP shows the volume file's stack with
/// "Chapters 9-10 are inside the volume file". Also the admin switch route and the Jobs row.
/// </summary>
public sealed class VolumeContentScanHttpTests : IDisposable
{
    private static readonly Dictionary<string, string?> Configuration = new()
    {
        ["MangaPixer:VolumeScan:Enabled"] = "true",
        ["MangaPixer:VolumeScan:InitialDelaySeconds"] = "0",
        ["MangaPixer:VolumeScan:PollSeconds"] = "1",
        ["MangaPixer:VolumeScan:RetrySeconds"] = "1",
    };

    private readonly FakeHasher _hasher = new();
    private readonly ApiTokenTestFactory _factory;

    public VolumeContentScanHttpTests()
    {
        _factory = new ApiTokenTestFactory(Configuration, configureServices: services =>
        {
            services.RemoveAll<IVolumePageHasher>();
            services.AddSingleton<IVolumePageHasher>(_hasher);
        });
    }

    public void Dispose() => _factory.Dispose();

    private sealed class FakeHasher : IVolumePageHasher
    {
        public Dictionary<string, ulong?[]> Pages { get; } = new(StringComparer.Ordinal);
        public int Calls;

        public Task<PageHashOutcome> HashAsync(PageHashRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (!Pages.TryGetValue(request.ArchivePath, out var pages))
                return Task.FromResult(PageHashOutcome.Failed(PageHashErrors.Missing));
            var to = request.Count == 0 ? pages.Length : Math.Min(pages.Length, request.FromOrdinal + request.Count);
            return Task.FromResult(PageHashOutcome.Ok(new PageHashResult
            {
                JobId = request.JobId,
                PageCount = pages.Length,
                Pages = [.. Enumerable.Range(request.FromOrdinal, to - request.FromOrdinal).Select(i => new PageHashEntry { Ordinal = i, Hash = pages[i] })],
                ObservedLastWriteTicks = request.ExpectedLastWriteTicks,
                ObservedByteLength = request.ExpectedByteLength,
            }));
        }
    }

    /// <summary>
    /// An unlinked folder: <c>v02</c> holds chapters 9 and 10 (another release: a few bits off per page) between 3 cover pages and 2
    /// back pages; chapter 11 is not in it. Returns (library public id, folder public id).
    /// </summary>
    private async Task<(string Library, string Folder)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var lib = await VolumeTestData.AddLibraryAsync(db);
        var folder = await VolumeTestData.AddFolderAsync(db, lib.Id, null, "Synthetic Pair");
        var random = new Random(7);
        ulong?[] Pages(int n) => [.. Enumerable.Range(0, n).Select(_ => (ulong?)(ulong)random.NextInt64(long.MinValue, long.MaxValue))];
        ulong?[] Copy(ulong?[] pages) => [.. pages.Select(p => (ulong?)(p!.Value ^ 0b1011))];
        async Task AddAsync(string name, ulong?[] pages)
        {
            var node = await VolumeTestData.AddArchiveAsync(db, lib.Id, folder.Id, name);
            var item = db.ArchiveItems.Single(a => a.NodeId == node.Id);
            item.PageCount = pages.Length;
            await db.SaveChangesAsync();
            _hasher.Pages[Path.Combine(lib.RootPath, node.RelativePath)] = pages;
        }
        var c9 = Pages(8);
        var c10 = Pages(8);
        await AddAsync("Synthetic Pair v02", [.. Pages(3), .. c9, .. c10, .. Pages(2)]);
        await AddAsync("Synthetic Pair - Chapter 009", Copy(c9));
        await AddAsync("Synthetic Pair - Chapter 010", Copy(c10));
        await AddAsync("Synthetic Pair - Chapter 011", Pages(8));
        return (lib.PublicId, folder.PublicId);
    }

    [Fact]
    public async Task AFolderOpenedInTheVolumesView_IsScannedInTheBackground_AndItsStackSaysChapters9to10AreInside()
    {
        var (_, folderId) = await SeedAsync();
        var client = await _factory.AdminAsync();

        // Opening the folder's Volumes view puts it at the front of the scan; poll (bounded) for the answer.
        var deadline = DateTime.UtcNow.AddSeconds(60);
        JsonElement? stack = null;
        while (DateTime.UtcNow < deadline && stack is null)
        {
            await client.GetAsync($"/api/v1/nodes/{folderId}/volume-view");
            var response = await client.GetAsync($"/api/v1/nodes/{folderId}/volumes/2");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("inVolumeFile", out _))
                    stack = body;
            }
            if (stack is null)
                await Task.Delay(250);
        }

        Assert.True(stack.HasValue, $"no stack with chapters found inside the volume file ({_hasher.Calls} hash requests): "
            + await client.GetStringAsync("/api/v1/admin/volume-contents"));
        var line = stack.Value.GetProperty("inVolumeFile");
        Assert.Equal(("9", "10", 2), (line.GetProperty("from").GetString(), line.GetProperty("to").GetString(), line.GetProperty("count").GetInt32()));
        Assert.Equal("Volume 2", stack.Value.GetProperty("label").GetString());
        Assert.True(stack.Value.GetProperty("hasVolumeArchive").GetBoolean());
        Assert.Equal(3, stack.Value.GetProperty("presentCount").GetInt32());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/volume-contents");
        Assert.Equal((1, 1, 2), (status.GetProperty("volumesChecked").GetInt32(), status.GetProperty("volumesWithChapters").GetInt32(),
            status.GetProperty("chaptersInside").GetInt32()));
    }

    [Fact]
    public async Task TheAdminSwitch_IsOnByDefault_AdminOnly_AndTurnsOff()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/volume-contents")).StatusCode);

        var admin = await _factory.AdminAsync();
        var initial = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/volume-contents");
        Assert.True(initial.GetProperty("enabled").GetBoolean());
        Assert.False(initial.GetProperty("configDisabled").GetBoolean());

        var off = await admin.PutAsJsonAsync("/api/v1/admin/volume-contents", new { enabled = false });
        off.EnsureSuccessStatusCode();
        Assert.False((await off.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("enabled").GetBoolean());
        Assert.False((await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/volume-contents")).GetProperty("enabled").GetBoolean());

        var jobs = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/jobs");
        var row = jobs.GetProperty("jobs").EnumerateArray().Single(j => j.GetProperty("key").GetString() == "volume-contents");
        Assert.Equal("interval", row.GetProperty("kind").GetString());
        Assert.False(row.GetProperty("enabled").GetBoolean());
    }

    [Fact]
    public async Task Switched_Off_ByConfiguration_NothingIsHashed_AndTheCardSaysSo()
    {
        using var factory = new ApiTokenTestFactory(new Dictionary<string, string?>(Configuration) { ["MangaPixer:VolumeScan:Enabled"] = "false" },
            configureServices: services =>
            {
                services.RemoveAll<IVolumePageHasher>();
                services.AddSingleton<IVolumePageHasher>(_hasher);
            });
        var admin = await factory.AdminAsync();
        var status = await admin.GetFromJsonAsync<JsonElement>("/api/v1/admin/volume-contents");

        Assert.True(status.GetProperty("configDisabled").GetBoolean());
        await Task.Delay(1500);
        Assert.Equal(0, _hasher.Calls);
    }
}
