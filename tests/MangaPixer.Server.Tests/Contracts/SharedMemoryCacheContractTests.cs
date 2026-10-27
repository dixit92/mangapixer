namespace com.lifepixer.mangapixer.Tests.Server.Contracts;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// 1.49.0 lane M: the shared <c>IMemoryCache</c> has a size limit, and with a size limit EVERY entry must carry a size or the cache
/// throws. This sweep fails when a server file writes an <c>IMemoryCache</c> (<c>Set</c>, <c>GetOrCreate</c>, <c>GetOrCreateAsync</c>,
/// <c>CreateEntry</c>) instead of going through <c>SharedMemoryCache.SetSized</c>, so a new call site cannot ship a sizeless entry
/// that throws only on the path nobody tested. The HTTP side (<c>SharedMemoryCacheHttpTests</c>) proves the limit is on.
/// </summary>
public sealed partial class SharedMemoryCacheContractTests
{
    private const string RuleHome = "SharedMemoryCache.cs";

    [GeneratedRegex(@"\bIMemoryCache\??\s+(?<name>[A-Za-z_][A-Za-z0-9_]*)\b")]
    private static partial Regex Declaration();

    [GeneratedRegex(@"\bpartial\s+(?:class|record|struct)\s+(?<type>[A-Za-z_][A-Za-z0-9_]*)")]
    private static partial Regex PartialType();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MangaPixer.slnx")))
                return dir.FullName;
        }
        throw new InvalidOperationException("Repository root (MangaPixer.slnx) not found above the test output directory.");
    }

    private static IEnumerable<string> ServerFiles(string server) =>
        Directory.EnumerateFiles(server, "*.cs", SearchOption.AllDirectories).Where(f =>
            !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && Path.GetFileName(f) != RuleHome);

    [Fact]
    public void NoServerFile_WritesTheSharedCache_OutsideSetSized()
    {
        var server = Path.Combine(FindRepoRoot(), "src", "MangaPixer.Server");
        var offenders = new List<string>();
        var holders = 0;
        var sizedWrites = 0;
        var texts = ServerFiles(server).ToDictionary(f => f, File.ReadAllText);

        // A partial type may declare the cache field in one file and write it in another: the names follow the type.
        var namesOfPartial = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var text in texts.Values)
        {
            var declared = Declaration().Matches(text).Select(m => m.Groups["name"].Value).ToList();
            if (declared.Count == 0)
                continue;
            foreach (Match type in PartialType().Matches(text))
            {
                var key = type.Groups["type"].Value;
                if (!namesOfPartial.TryGetValue(key, out var set))
                    namesOfPartial[key] = set = new HashSet<string>(StringComparer.Ordinal);
                set.UnionWith(declared);
            }
        }

        foreach (var (file, text) in texts)
        {
            var names = Declaration().Matches(text).Select(m => m.Groups["name"].Value).ToHashSet(StringComparer.Ordinal);
            foreach (Match type in PartialType().Matches(text))
            {
                if (namesOfPartial.TryGetValue(type.Groups["type"].Value, out var fromOtherParts))
                    names.UnionWith(fromOtherParts);
            }
            if (names.Count == 0)
                continue;
            holders++;
            var write = new Regex(@"\b(?:" + string.Join("|", names.Select(Regex.Escape)) + @")\??\s*\.\s*(?<call>Set|GetOrCreate|GetOrCreateAsync|CreateEntry)\s*[<(]");
            var sized = new Regex(@"\b(?:" + string.Join("|", names.Select(Regex.Escape)) + @")\??\s*\.\s*SetSized\s*[<(]");
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (write.IsMatch(lines[i]))
                    offenders.Add($"{Path.GetRelativePath(server, file)}:{i + 1}");
                sizedWrites += sized.Matches(lines[i]).Count;
            }
        }

        // Not vacuous: the sweep sees the files that hold the shared cache and their sized writes.
        Assert.True(holders >= 6, $"expected the shared cache's holders, found {holders}");
        Assert.True(sizedWrites >= 10, $"expected the sized writes, found {sizedWrites}");
        Assert.True(offenders.Count == 0,
            "Write the shared IMemoryCache through SharedMemoryCache.SetSized (it has a size limit; a sizeless entry throws): "
            + string.Join(", ", offenders));
    }
}
