namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using com.lifepixer.mangapixer.Core.Api;
using Xunit;

/// <summary>
/// HTTP tests (1.49.0, lane B) for compressed rotating backups through the public operations API: a backup is a
/// <c>.db.gz</c>, it is listed with its real size, can be restored from the list and from an upload (detected by content,
/// whatever the file is called), an old plain <c>.db</c> still restores, and a damaged or oversized gzip is refused with
/// nothing staged. No new route: the existing restore routes are the surface.
/// </summary>
public sealed class CompressedBackupHttpTests : IDisposable
{
    private readonly MangaPixerWebApplicationFactory _factory;

    public CompressedBackupHttpTests()
    {
        _factory = new MangaPixerWebApplicationFactory();
    }

    public void Dispose() => _factory.Dispose();

    private string BackupsDir => Path.Combine(_factory.DataRoot, "backups");
    private string StagedPath => Path.Combine(_factory.DataRoot, "restore-pending", "mangapixer.db.staged");

    private static async Task<string> TakeBackupViaApiAsync(HttpClient admin)
    {
        var response = await admin.PostAsync("/api/v1/operations/backups/rotating", content: null);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<JsonElement>();
        return dto.GetProperty("lastBackupFileName").GetString()!;
    }

    private static MultipartFormDataContent Upload(byte[] bytes, string fileName)
    {
        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(bytes);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", fileName);
        return content;
    }

    private static async Task<byte[]> UnpackAsync(string gzPath)
    {
        await using var packed = File.OpenRead(gzPath);
        await using var unpack = new GZipStream(packed, CompressionMode.Decompress);
        await using var plain = new MemoryStream();
        await unpack.CopyToAsync(plain);
        return plain.ToArray();
    }

    private static async Task<byte[]> StagedHeadAsync(string path)
    {
        var head = new byte[16];
        await using var fs = File.OpenRead(path);
        _ = await fs.ReadAsync(head);
        return head;
    }

    [Fact]
    public async Task RotatingBackup_IsGzip_Listed_AndLeavesNoTemporaryFile()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();

        var fileName = await TakeBackupViaApiAsync(client);

        Assert.EndsWith(".db.gz", fileName);
        var onDisk = new FileInfo(Path.Combine(BackupsDir, fileName));
        Assert.True(onDisk.Length > 0);
        var raw = await UnpackAsync(onDisk.FullName);
        Assert.True(raw.Length > onDisk.Length, "the compressed snapshot is smaller than the database it holds");
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(raw, 0, 16));
        Assert.DoesNotContain(Directory.EnumerateFiles(BackupsDir).Select(Path.GetFileName),
            n => n!.EndsWith(".partial", StringComparison.Ordinal) || n.EndsWith(".tmp", StringComparison.Ordinal));

        var list = await (await client.GetAsync("/api/v1/operations/backups/files")).Content.ReadFromJsonAsync<JsonElement>();
        var first = list.GetProperty("files")[0];
        Assert.Equal(fileName, first.GetProperty("fileName").GetString());
        Assert.Equal(onDisk.Length, first.GetProperty("byteSize").GetInt64());
    }

    [Fact]
    public async Task RestoreFromList_CompressedSnapshot_Stages()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);

        var response = await client.PostAsJsonAsync("/api/v1/operations/backups/restore", new { fileName });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(await StagedHeadAsync(StagedPath)));
    }

    [Theory]
    [InlineData("backup.db.gz")]
    [InlineData("backup.db")] // the name lies: the bytes decide
    [InlineData("backup")]
    public async Task RestoreUpload_CompressedBackup_IsDetectedByContent_AndStagesPlain(string uploadedAs)
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);
        var gz = await File.ReadAllBytesAsync(Path.Combine(BackupsDir, fileName));

        var response = await client.PostAsync("/api/v1/operations/restore", Upload(gz, uploadedAs));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(await StagedHeadAsync(StagedPath)));
        Assert.True(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "restore.json")));
    }

    [Fact]
    public async Task OldPlainBackup_StillListsAndRestores_FromTheListAndAnUpload()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);
        var plain = await UnpackAsync(Path.Combine(BackupsDir, fileName));
        const string oldName = "rotating-19990101-000000.db";
        await File.WriteAllBytesAsync(Path.Combine(BackupsDir, oldName), plain);

        var list = await (await client.GetAsync("/api/v1/operations/backups/files")).Content.ReadFromJsonAsync<JsonElement>();
        var names = list.GetProperty("files").EnumerateArray().Select(f => f.GetProperty("fileName").GetString()).ToArray();
        Assert.Equal(new[] { fileName, oldName }, names); // newest first, both kinds

        var fromList = await client.PostAsJsonAsync("/api/v1/operations/backups/restore", new { fileName = oldName });
        Assert.Equal(HttpStatusCode.Accepted, fromList.StatusCode);
        File.Delete(StagedPath);

        var uploaded = await client.PostAsync("/api/v1/operations/restore", Upload(plain, oldName));
        Assert.Equal(HttpStatusCode.Accepted, uploaded.StatusCode);
        Assert.Equal("SQLite format 3\0", System.Text.Encoding.ASCII.GetString(await StagedHeadAsync(StagedPath)));
    }

    [Fact]
    public async Task RestoreUpload_DamagedGzip_Returns400_AndStagesNothing()
    {
        var client = await _factory.LoginAsAdminWithChangedPasswordAsync();
        var fileName = await TakeBackupViaApiAsync(client);
        var gz = await File.ReadAllBytesAsync(Path.Combine(BackupsDir, fileName));
        var flipped = (byte[])gz.Clone();
        for (var i = 300; i < 340 && i < flipped.Length - 8; i++) flipped[i] ^= 0x5A;
        var badCrc = (byte[])gz.Clone();
        badCrc[^8] ^= 0xFF;

        foreach (var damaged in new[] { flipped, badCrc, gz[..(gz.Length / 3)] })
        {
            var response = await client.PostAsync("/api/v1/operations/restore", Upload(damaged, "backup.db.gz"));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var err = await response.Content.ReadFromJsonAsync<ApiError>();
            Assert.Equal("invalid_backup", err!.Error);
            Assert.False(File.Exists(StagedPath));
            Assert.False(File.Exists(Path.Combine(_factory.DataRoot, "restore-pending", "restore.json")));
        }
        Assert.DoesNotContain(Directory.EnumerateFiles(BackupsDir).Select(Path.GetFileName), n => n!.StartsWith("pre-restore-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RestoreUpload_GzipThatUnpacksPastTheCap_IsRefused_AndNothingIsStaged()
    {
        // A host with a 1 MiB cap on the UNPACKED bytes; 6 MiB of zeros is a few KiB of gzip.
        using var factory = MangaPixerWebApplicationFactory.WithExtraConfiguration(new Dictionary<string, string?>
        {
            ["MangaPixer:Backups:MaxRestoreUploadBytes"] = (1024 * 1024).ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        var client = await factory.LoginAsAdminWithChangedPasswordAsync();
        byte[] bomb;
        using (var ms = new MemoryStream())
        {
            using (var gz = new GZipStream(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                gz.Write(new byte[6 * 1024 * 1024]);
            bomb = ms.ToArray();
        }
        Assert.True(bomb.Length < 64 * 1024);

        var response = await client.PostAsync("/api/v1/operations/restore", Upload(bomb, "backup.db.gz"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var err = await response.Content.ReadFromJsonAsync<ApiError>();
        Assert.Equal("upload_too_large", err!.Error);
        Assert.False(File.Exists(Path.Combine(factory.DataRoot, "restore-pending", "mangapixer.db.staged")));
        Assert.False(File.Exists(Path.Combine(factory.DataRoot, "restore-pending", "restore.json")));
    }
}
