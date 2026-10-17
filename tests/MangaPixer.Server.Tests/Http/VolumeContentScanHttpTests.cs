namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImageMagick;
using ImageMagick.Drawing;
using Xunit;

/// <summary>
/// HTTP + process tests for the volume contents scan (1.44.0), with nothing hand-seeded: real archives (synthetic pages) in a library,
/// scanned and analysed by the real worker, then the hosted scan reads them through the worker's <c>page_hash</c> and the Volumes view
/// over HTTP shows the volume file's stack with "Chapters 9-10 are inside the volume file". Also the admin switch route. Only the JSON
/// of public routes is read, so the first test runs unchanged on a server without the scan (it fails there: no stack, no line).
/// </summary>
public sealed class VolumeContentScanHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(
        new Dictionary<string, string?>
        {
            ["MangaPixer:VolumeScan:Enabled"] = "true",
            ["MangaPixer:VolumeScan:InitialDelaySeconds"] = "0",
            ["MangaPixer:VolumeScan:PollSeconds"] = "1",
        });

    private readonly string _root = Path.Combine(Path.GetTempPath(), "mangapixer-volscan-http-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_root, true); } catch { }
    }

    private static byte[] Page(int seed, uint quality)
    {
        using var image = new MagickImage(MagickColors.White, 300, 440);
        var random = new Random(seed);
        var draw = new Drawables();
        for (var i = 0; i < 12; i++)
        {
            var gray = (byte)random.Next(0, 160);
            draw.FillColor(new MagickColor(gray, gray, gray)).Rectangle(random.Next(0, 250), random.Next(0, 400), random.Next(0, 300), random.Next(0, 440));
        }
        draw.Draw(image);
        image.Format = MagickFormat.Jpeg;
        image.Quality = quality;
        return image.ToByteArray();
    }

    private static void Zip(string path, IEnumerable<byte[]> pages)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var n = 0;
        foreach (var page in pages)
        {
            using var s = zip.CreateEntry($"{++n:000}.jpg").Open();
            s.Write(page);
        }
    }

    /// <summary>
    /// <c>Pair v02.cbz</c>: 3 cover pages, the pages of chapters 9 and 10 (another release: re-encoded), 2 back pages; chapter 11 is not in it.
    /// </summary>
    private string SeedFolder()
    {
        var folder = Path.Combine(_root, "Synthetic Pair");
        Directory.CreateDirectory(folder);
        int[] c9 = [11, 12, 13, 14, 15, 16];
        int[] c10 = [21, 22, 23, 24, 25, 26];
        Zip(Path.Combine(folder, "Synthetic Pair v02.cbz"),
            [.. new[] { 1, 2, 3 }.Select(s => Page(s, 90)), .. c9.Concat(c10).Select(s => Page(s, 90)), .. new[] { 4, 5 }.Select(s => Page(s, 90))]);
        Zip(Path.Combine(folder, "Synthetic Pair - Chapter 009.cbz"), c9.Select(s => Page(s, 55)));
        Zip(Path.Combine(folder, "Synthetic Pair - Chapter 010.cbz"), c10.Select(s => Page(s, 55)));
        Zip(Path.Combine(folder, "Synthetic Pair - Chapter 011.cbz"), new[] { 31, 32, 33, 34, 35, 36 }.Select(s => Page(s, 55)));
        return folder;
    }

    [Fact]
    [Trait("Category", "Process")]
    public async Task AVolumeFileHoldingTwoChapterFiles_IsScannedInTheBackground_AndItsStackSaysChapters9to10AreInside()
    {
        SeedFolder();
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var register = await client.PostAsJsonAsync("/api/v1/admin/libraries", new { displayName = "Volume scan", rootPath = _root });
        register.EnsureSuccessStatusCode();
        var libraryId = (await register.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString();
        var scan = await client.PostAsync($"/api/v1/admin/libraries/{libraryId}/scan", null);
        Assert.True(scan.StatusCode is HttpStatusCode.Accepted or HttpStatusCode.Conflict, $"scan: {scan.StatusCode}");

        // Bounded poll: the library scan + analysis, then the volume contents scan (hosted, polling every second).
        var deadline = DateTime.UtcNow.AddSeconds(90);
        JsonElement? stack = null;
        string? folderId = null;
        while (DateTime.UtcNow < deadline && stack is null)
        {
            await Task.Delay(500);
            if (folderId is null)
            {
                var root = await client.GetFromJsonAsync<JsonElement>($"/api/v1/libraries/{libraryId}/browse");
                folderId = root.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()).FirstOrDefault();
                if (folderId is null)
                    continue;
            }
            // Opening the folder's Volumes view puts it at the front of the scan.
            await client.GetAsync($"/api/v1/nodes/{folderId}/volume-view");
            var response = await client.GetAsync($"/api/v1/nodes/{folderId}/volumes/2");
            if (response.StatusCode == HttpStatusCode.OK)
            {
                var body = await response.Content.ReadFromJsonAsync<JsonElement>();
                if (body.TryGetProperty("inVolumeFile", out _))
                    stack = body;
            }
        }

        Assert.True(stack.HasValue, "no stack with chapters found inside the volume file");
        var line = stack.Value.GetProperty("inVolumeFile");
        Assert.Equal(("9", "10", 2), (line.GetProperty("from").GetString(), line.GetProperty("to").GetString(), line.GetProperty("count").GetInt32()));
        Assert.Equal("Volume 2", stack.Value.GetProperty("label").GetString());
        Assert.True(stack.Value.GetProperty("hasVolumeArchive").GetBoolean());

        var status = await client.GetFromJsonAsync<JsonElement>("/api/v1/admin/volume-contents");
        Assert.True(status.GetProperty("enabled").GetBoolean());
        Assert.Equal(1, status.GetProperty("volumesWithChapters").GetInt32());
        Assert.Equal(2, status.GetProperty("chaptersInside").GetInt32());
    }

    [Fact]
    public async Task TheAdminSwitch_IsOnByDefault_AdminOnly_AndTurnsOff()
    {
        var anonymous = _factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/v1/admin/volume-contents")).StatusCode);

        var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
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
    }
}
