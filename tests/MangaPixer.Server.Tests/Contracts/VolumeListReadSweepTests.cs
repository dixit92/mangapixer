namespace com.lifepixer.mangapixer.Tests.Server.Contracts;

using Xunit;

/// <summary>
/// 1.34.0 (lane V): a near-empty MangaDex list is "no volume list" EVERYWHERE it is read, and the rule lives in one place
/// (<c>VolumeMapService.IsUsable</c> / <c>UsableMangaDexMap</c>). This sweep fails when a server file picks a stored MangaDex map by
/// "state Ok" itself, so a new reader cannot bypass the rule by copying the 1.29.0 pattern.
/// </summary>
public sealed class VolumeListReadSweepTests
{
    private const string RuleHome = "VolumeMapService.cs";

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MangaPixer.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (MangaPixer.slnx) not found above the test output directory.");
    }

    [Fact]
    public void NoServerFile_SelectsAStoredMangaDexMapByStateOk_OutsideTheRule()
    {
        var server = Path.Combine(FindRepoRoot(), "src", "MangaPixer.Server");
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains(Path.DirectorySeparatorChar + "Migrations" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                || Path.GetFileName(file) == RuleHome)
            {
                continue;
            }
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.Contains("VolumeMapSource.MangaDexAggregate", StringComparison.Ordinal) && line.Contains("VolumeMapState.Ok", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetRelativePath(server, file)}:{i + 1}");
            }
        }
        Assert.True(offenders.Count == 0, "Read a stored MangaDex map through VolumeMapService.UsableMangaDexMap / IsUsable: " + string.Join(", ", offenders));
    }
}
