namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Server.Operations;
using Xunit;

/// <summary>
/// Unit tests for <see cref="BackupLocationValidator"/>: syntax + normalization
/// on both path flavors (Windows rules via the injected flavor, so they run on
/// Linux CI), protected-root overlap in both directions, the system / ephemeral
/// deny-list, and the filesystem-backed steps against an in-memory fake.
/// </summary>
public sealed class BackupLocationValidatorTests
{
    private static BackupLocationValidator Unix(FakeBackupFileSystem? fs = null) =>
        new(fs ?? new FakeBackupFileSystem(), BackupPathFlavor.Unix);

    private static BackupLocationValidator Windows(FakeBackupFileSystem? fs = null) =>
        new(fs ?? new FakeBackupFileSystem(), BackupPathFlavor.Windows,
            new[] { @"C:\Windows", @"C:\Program Files", @"C:\Users\me\AppData\Local\Temp" });

    private static readonly BackupLocationContext UnixContext = new()
    {
        DataRoot = "/data",
        ProtectedRoots = new[] { "/cache", "/scratch", "/media", "/app", "/srv/library/manga" },
        DatabaseBytes = 1000,
    };

    private static readonly BackupLocationContext WindowsContext = new()
    {
        DataRoot = @"C:\Users\me\AppData\Local\MangaPixer\data",
        ProtectedRoots = new[] { @"D:\Comics", @"C:\Users\me\AppData\Local\Programs\MangaPixer" },
        DatabaseBytes = 1000,
    };

    [Theory]
    [InlineData("/backups", "/backups")]
    [InlineData("/mnt/archive//mangapixer/", "/mnt/archive/mangapixer")]
    [InlineData("/mnt/./archive", "/mnt/archive")]
    public void Unix_Normalize_Accepts(string input, string expected)
    {
        var (normalized, error) = Unix().Normalize(input);
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData("backups", BackupLocationCodes.NotAbsolute)]
    [InlineData("./backups", BackupLocationCodes.NotAbsolute)]
    [InlineData("/mnt/../etc", BackupLocationCodes.Invalid)]
    [InlineData("", BackupLocationCodes.Invalid)]
    [InlineData("   ", BackupLocationCodes.Invalid)]
    [InlineData("/mnt/a\u0000b", BackupLocationCodes.Invalid)]
    [InlineData("/mnt/a\nb", BackupLocationCodes.Invalid)]
    public void Unix_Normalize_Rejects(string input, string code)
    {
        var (_, error) = Unix().Normalize(input);
        Assert.Equal(code, error);
    }

    [Fact]
    public void Normalize_RejectsOverlongInput()
    {
        var (_, error) = Unix().Normalize("/" + new string('a', BackupLocationValidator.MaxLength));
        Assert.Equal(BackupLocationCodes.Invalid, error);
    }

    [Theory]
    [InlineData(@"D:\MangaPixer-backups", @"D:\MangaPixer-backups")]
    [InlineData(@"d:/archive//mp\", @"D:\archive\mp")]
    [InlineData(@"\\nas\archive\mangapixer", @"\\nas\archive\mangapixer")]
    public void Windows_Normalize_Accepts(string input, string expected)
    {
        var (normalized, error) = Windows().Normalize(input);
        Assert.Null(error);
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(@"C:foo", BackupLocationCodes.NotAbsolute)]
    [InlineData(@"\foo", BackupLocationCodes.NotAbsolute)]
    [InlineData(@"backups", BackupLocationCodes.NotAbsolute)]
    [InlineData(@"\\?\C:\backups", BackupLocationCodes.Invalid)]
    [InlineData(@"\\.\PhysicalDrive0\x", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\backups\CON", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\backups\nul.txt", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\backups\COM1", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\backups:stream", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\backups.", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\backups ", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\a\..\b", BackupLocationCodes.Invalid)]
    [InlineData(@"\\nas\share", BackupLocationCodes.Invalid)]
    [InlineData(@"\\nas", BackupLocationCodes.Invalid)]
    [InlineData(@"D:\a<b", BackupLocationCodes.Invalid)]
    public void Windows_Normalize_Rejects(string input, string code)
    {
        var (_, error) = Windows().Normalize(input);
        Assert.Equal(code, error);
    }

    [Theory]
    [InlineData("/data")]              // equal
    [InlineData("/data/backups-2")]    // inside
    [InlineData("/")]                  // ancestor (also the filesystem root)
    [InlineData("/srv")]               // ancestor of a library root
    [InlineData("/srv/library/manga/x")] // inside a library root
    [InlineData("/media/backups")]     // inside the media root
    [InlineData("/cache/b")]
    [InlineData("/scratch")]
    [InlineData("/app/backups")]       // inside the install dir
    public void Unix_ProtectedRoots_BothDirections(string location)
    {
        var (_, error) = Unix().CheckStatic(location, UnixContext);
        Assert.Equal(BackupLocationCodes.OverlapsProtected, error);
    }

    [Theory]
    [InlineData("/data2/backups")]     // separator-aware: /data2 is not inside /data
    [InlineData("/datastore")]
    [InlineData("/mnt/archive/mangapixer")]
    [InlineData("/srv/library/manga-backups")]
    public void Unix_SiblingNames_AreNotOverlaps(string location)
    {
        var (normalized, error) = Unix().CheckStatic(location, UnixContext);
        Assert.Null(error);
        Assert.Equal(location, normalized);
    }

    [Fact]
    public void Unix_CaseMatters()
    {
        // Linux paths are case-sensitive: /Data is a different folder.
        var (_, error) = Unix().CheckStatic("/Data/backups", UnixContext);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(@"c:\users\ME\appdata\local\mangapixer\DATA\b")] // case-insensitive
    [InlineData(@"D:\Comics")]
    [InlineData(@"D:\Comics\backups")]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\MangaPixer\backups")]
    [InlineData(@"C:\Users\me\AppData\Local")] // ancestor of the data root
    public void Windows_ProtectedRoots_CaseInsensitive(string location)
    {
        var (_, error) = Windows().CheckStatic(location, WindowsContext);
        Assert.Equal(BackupLocationCodes.OverlapsProtected, error);
    }

    [Theory]
    [InlineData("/proc/x")]
    [InlineData("/tmp")]
    [InlineData("/tmp/mangapixer-backups")]
    [InlineData("/dev/shm/b")]
    [InlineData("/etc/mangapixer")]
    [InlineData("/usr/local/backups")]
    [InlineData("/var/tmp/b")]
    [InlineData("/run/b")]
    public void Unix_SystemAndEphemeral_AreForbidden(string location)
    {
        var (_, error) = Unix().CheckStatic(location, UnixContext);
        Assert.Equal(BackupLocationCodes.Forbidden, error);
    }

    [Fact]
    public void Unix_TmpIsAllowed_WhenTheDataRootItselfLivesThere()
    {
        // The data root is no less ephemeral than the backups then.
        var context = UnixContext with { DataRoot = "/tmp/mp/data" };
        var (_, error) = Unix().CheckStatic("/tmp/mp/backups", context);
        Assert.Null(error);
    }

    [Theory]
    [InlineData(@"C:\Windows\Temp\b")]
    [InlineData(@"c:\program files\mp")]
    [InlineData(@"C:\Users\me\AppData\Local\Temp\b")]
    [InlineData(@"E:\")]
    public void Windows_SystemTempAndDriveRoots_AreForbidden(string location)
    {
        var (_, error) = Windows().CheckStatic(location, WindowsContext);
        Assert.Equal(BackupLocationCodes.Forbidden, error);
    }

    [Fact]
    public void ResolvedLink_IntoProtectedRoot_IsRejected()
    {
        var fs = new FakeBackupFileSystem();
        fs.Links["/mnt/sneaky"] = "/srv/library/manga/sub";
        var (_, error) = Unix(fs).CheckStatic("/mnt/sneaky", UnixContext);
        Assert.Equal(BackupLocationCodes.OverlapsProtected, error);
    }

    [Fact]
    public void ResolvedLink_IntoSystemRoot_IsRejected()
    {
        var fs = new FakeBackupFileSystem();
        fs.Links["/mnt/sneaky"] = "/etc/cron.d";
        var (_, error) = Unix(fs).CheckStatic("/mnt/sneaky", UnixContext);
        Assert.Equal(BackupLocationCodes.Forbidden, error);
    }

    [Fact]
    public void Validate_ParentMissing()
    {
        var fs = new FakeBackupFileSystem();
        var result = Unix(fs).Validate("/mnt/archive/mp", UnixContext, null, false, false);
        Assert.Equal(BackupLocationCodes.ParentMissing, result.ErrorCode);
        Assert.Empty(fs.Created);
    }

    [Fact]
    public void Validate_CreatesLeaf_AndReportsWillCreate()
    {
        var fs = new FakeBackupFileSystem();
        fs.Directories.Add("/mnt/archive");
        var result = Unix(fs).Validate("/mnt/archive/mp", UnixContext, null, false, validateOnly: false);
        Assert.True(result.IsValid);
        Assert.True(result.WillCreate);
        Assert.Contains("/mnt/archive/mp", fs.Directories);
        Assert.False(string.IsNullOrEmpty(result.MarkerId));
    }

    [Fact]
    public void Validate_ValidateOnly_RemovesTheLeafItCreated()
    {
        var fs = new FakeBackupFileSystem();
        fs.Directories.Add("/mnt/archive");
        var result = Unix(fs).Validate("/mnt/archive/mp", UnixContext, null, false, validateOnly: true);
        Assert.True(result.IsValid);
        Assert.True(result.WillCreate);
        Assert.DoesNotContain("/mnt/archive/mp", fs.Directories);
    }

    [Fact]
    public void Validate_NotWritable()
    {
        var fs = new FakeBackupFileSystem { ProbeFails = true };
        fs.Directories.Add("/mnt/archive");
        fs.Directories.Add("/mnt/archive/mp");
        var result = Unix(fs).Validate("/mnt/archive/mp", UnixContext, null, false, false);
        Assert.Equal(BackupLocationCodes.NotWritable, result.ErrorCode);
    }

    [Fact]
    public void Validate_ForeignMarker_IsInUse_UnlessAdopted()
    {
        var fs = new FakeBackupFileSystem();
        fs.Directories.Add("/mnt/archive");
        fs.Directories.Add("/mnt/archive/mp");
        fs.Markers["/mnt/archive/mp"] = "other-instance";

        var refused = Unix(fs).Validate("/mnt/archive/mp", UnixContext, "mine", false, false);
        Assert.Equal(BackupLocationCodes.InUse, refused.ErrorCode);

        var adopted = Unix(fs).Validate("/mnt/archive/mp", UnixContext, "mine", adoptExistingMarker: true, false);
        Assert.True(adopted.IsValid);
        Assert.Equal("mine", adopted.MarkerId);
    }

    [Fact]
    public void Validate_OwnMarker_IsAccepted()
    {
        var fs = new FakeBackupFileSystem();
        fs.Directories.Add("/mnt/archive");
        fs.Directories.Add("/mnt/archive/mp");
        fs.Markers["/mnt/archive/mp"] = "mine";
        var result = Unix(fs).Validate("/mnt/archive/mp", UnixContext, "mine", false, false);
        Assert.True(result.IsValid);
        Assert.Equal("mine", result.MarkerId);
    }

    [Fact]
    public void Validate_LowFreeSpace_IsAWarningOnly()
    {
        var fs = new FakeBackupFileSystem { FreeBytes = 1500 };
        fs.Directories.Add("/mnt/archive");
        var result = Unix(fs).Validate("/mnt/archive/mp", UnixContext, null, false, false);
        Assert.True(result.IsValid);
        Assert.Contains(BackupLocationCodes.LowFreeSpace, result.Warnings);
    }

    [Fact]
    public void CheckBeforeRun_NeverCreates_AndNeedsTheMarker()
    {
        var fs = new FakeBackupFileSystem();
        fs.Directories.Add("/mnt/archive");
        var validator = Unix(fs);

        // Missing folder (an unmounted share): unavailable, nothing created.
        Assert.Equal(BackupLocationCodes.Unavailable, validator.CheckBeforeRun("/mnt/archive/mp", UnixContext, "mine").ErrorCode);
        Assert.Empty(fs.Created);

        // Folder present but no marker (an empty mount point): unavailable.
        fs.Directories.Add("/mnt/archive/mp");
        Assert.Equal(BackupLocationCodes.Unavailable, validator.CheckBeforeRun("/mnt/archive/mp", UnixContext, "mine").ErrorCode);

        // Foreign marker: unavailable.
        fs.Markers["/mnt/archive/mp"] = "other";
        Assert.Equal(BackupLocationCodes.Unavailable, validator.CheckBeforeRun("/mnt/archive/mp", UnixContext, "mine").ErrorCode);

        fs.Markers["/mnt/archive/mp"] = "mine";
        Assert.True(validator.CheckBeforeRun("/mnt/archive/mp", UnixContext, "mine").IsValid);
    }

    [Fact]
    public void CheckBeforeRun_ProtectedOverlap_IsInvalid()
    {
        // e.g. a library registered later inside the backup folder.
        var context = UnixContext with { ProtectedRoots = new[] { "/mnt/archive/mp/comics" } };
        var result = Unix().CheckBeforeRun("/mnt/archive/mp", context, "mine");
        Assert.Equal(BackupLocationCodes.Invalid, result.ErrorCode);
    }

    /// <summary>In-memory filesystem for the validator (flavor-agnostic string keys).</summary>
    internal sealed class FakeBackupFileSystem : IBackupLocationFileSystem
    {
        public HashSet<string> Directories { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Created { get; } = new();
        public Dictionary<string, string> Markers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Links { get; } = new(StringComparer.Ordinal);
        public bool ProbeFails { get; set; }
        public long? FreeBytes { get; set; }

        public bool DirectoryExists(string path) => Directories.Contains(path);

        public string ResolveLinks(string path)
        {
            foreach (var (link, target) in Links)
            {
                if (path == link) return target;
                if (path.StartsWith(link + "/", StringComparison.Ordinal)) return target + path[link.Length..];
            }
            return path;
        }

        public void CreateDirectory(string path)
        {
            Directories.Add(path);
            Created.Add(path);
        }

        public void DeleteEmptyDirectory(string path) => Directories.Remove(path);

        public void WriteProbe(string directory)
        {
            if (ProbeFails) throw new UnauthorizedAccessException();
        }

        public BackupMarkerRead ReadMarker(string directory) =>
            Markers.TryGetValue(directory, out var id) ? new BackupMarkerRead(true, id) : new BackupMarkerRead(false, null);

        public void WriteMarker(string directory, string markerId, DateTimeOffset createdUtc) => Markers[directory] = markerId;

        public long? GetAvailableFreeBytes(string directory) => FreeBytes;
    }
}
