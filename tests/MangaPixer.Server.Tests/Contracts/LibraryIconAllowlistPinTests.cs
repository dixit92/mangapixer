namespace com.lifepixer.mangapixer.Tests.Server.Contracts;

using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// The admin icon picker keeps a hand-maintained TypeScript copy of
/// <see cref="LibraryIcons.Allowed"/>. This pins the two together so a name added on one
/// side only fails the build instead of surfacing as a 400 (or a hidden icon) at runtime.
/// </summary>
public sealed partial class LibraryIconAllowlistPinTests
{
    private const string PickerRelativePath =
        "web/src/app/features/admin/library-icon-picker/library-icon-picker.component.ts";

    [GeneratedRegex(@"export const ALLOWED_ICONS[^=]*=\s*\[(?<body>.*?)\]\s*;", RegexOptions.Singleline)]
    private static partial Regex AllowedIconsArray();

    [GeneratedRegex(@"'(?<name>[^']+)'")]
    private static partial Regex QuotedName();

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MangaPixer.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root (MangaPixer.slnx) not found above the test output directory.");
    }

    [Fact]
    public void WebPickerAllowlist_MatchesServerAllowlist()
    {
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), PickerRelativePath));

        var array = AllowedIconsArray().Match(source);
        Assert.True(array.Success, "ALLOWED_ICONS array not found in the icon picker component.");

        var webIcons = QuotedName().Matches(array.Groups["body"].Value).Select(m => m.Groups["name"].Value).ToArray();
        var serverIcons = LibraryIcons.Allowed.ToArray();

        var onlyInServer = serverIcons.Except(webIcons, StringComparer.Ordinal).ToArray();
        var onlyInWeb = webIcons.Except(serverIcons, StringComparer.Ordinal).ToArray();

        Assert.True(
            onlyInServer.Length == 0 && onlyInWeb.Length == 0,
            $"Icon allowlists differ. Only in LibraryIcons.cs: [{string.Join(", ", onlyInServer)}]. " +
            $"Only in ALLOWED_ICONS (web): [{string.Join(", ", onlyInWeb)}].");
        Assert.Equal(serverIcons, webIcons);
    }
}
