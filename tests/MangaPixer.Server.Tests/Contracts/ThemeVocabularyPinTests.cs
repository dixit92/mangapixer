namespace com.lifepixer.mangapixer.Tests.Server.Contracts;

using System.Text.RegularExpressions;
using com.lifepixer.mangapixer.Core.Reading;
using Xunit;

/// <summary>
/// The appearance vocabulary (1.40.0) lives twice: <see cref="ThemeVocabulary"/> validates what the server stores, and
/// <c>theme-vocabulary.ts</c> drives the web (the Appearance card, the theme engine, the no-flash script). This pins the two
/// lists - and the two defaults - together, so a base or accent added on one side only fails the build instead of a 400 (or
/// a value the web cannot paint) at runtime.
/// </summary>
public sealed partial class ThemeVocabularyPinTests
{
    private const string VocabularyRelativePath = "web/src/app/core/theme/theme-vocabulary.ts";
    private const string IndexRelativePath = "web/src/index.html";

    [GeneratedRegex(@"export const (?<name>THEME_BASES|THEME_ACCENTS)\s*=\s*\[(?<body>[^\]]*)\]", RegexOptions.Singleline)]
    private static partial Regex ListDeclaration();

    [GeneratedRegex(@"export const (?<name>DEFAULT_THEME_BASE|DEFAULT_THEME_ACCENT)\s*:\s*\w+\s*=\s*'(?<value>[^']+)'")]
    private static partial Regex DefaultDeclaration();

    [GeneratedRegex(@"'(?<name>[^']+)'")]
    private static partial Regex QuotedName();

    [GeneratedRegex(@"var (?<name>bases|accents)\s*=\s*\[(?<body>[^\]]*)\]")]
    private static partial Regex BootScriptList();

    [GeneratedRegex(@"var theme = '(?<base>[^']+)', accent = '(?<accent>[^']+)'")]
    private static partial Regex BootScriptDefaults();

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

    private static string Source() => File.ReadAllText(Path.Combine(FindRepoRoot(), VocabularyRelativePath));

    private static string[] WebList(string source, string name)
    {
        var declaration = ListDeclaration().Matches(source).FirstOrDefault(m => m.Groups["name"].Value == name);
        Assert.True(declaration is not null, $"{name} not found in {VocabularyRelativePath}.");
        return QuotedName().Matches(declaration!.Groups["body"].Value).Select(m => m.Groups["name"].Value).ToArray();
    }

    private static string WebDefault(string source, string name)
    {
        var declaration = DefaultDeclaration().Matches(source).FirstOrDefault(m => m.Groups["name"].Value == name);
        Assert.True(declaration is not null, $"{name} not found in {VocabularyRelativePath}.");
        return declaration!.Groups["value"].Value;
    }

    [Fact]
    public void WebBases_MatchServerBases_InOrder()
    {
        Assert.Equal(ThemeVocabulary.Bases, WebList(Source(), "THEME_BASES"));
    }

    [Fact]
    public void WebAccents_MatchServerAccents_InOrder()
    {
        Assert.Equal(ThemeVocabulary.Accents, WebList(Source(), "THEME_ACCENTS"));
    }

    [Fact]
    public void WebDefaults_MatchServerDefaults_AndAreInTheirLists()
    {
        var source = Source();
        Assert.Equal(ThemeVocabulary.DefaultBase, WebDefault(source, "DEFAULT_THEME_BASE"));
        Assert.Equal(ThemeVocabulary.DefaultAccent, WebDefault(source, "DEFAULT_THEME_ACCENT"));
        Assert.True(ThemeVocabulary.IsBase(ThemeVocabulary.DefaultBase));
        Assert.True(ThemeVocabulary.IsAccent(ThemeVocabulary.DefaultAccent));
    }

    /// <summary>The no-flash script in index.html (it runs before Angular, so it cannot import the TS lists).</summary>
    [Fact]
    public void IndexBootScript_ListsAndDefaults_MatchTheServer()
    {
        var html = File.ReadAllText(Path.Combine(FindRepoRoot(), IndexRelativePath));
        string[] List(string name)
        {
            var m = BootScriptList().Matches(html).FirstOrDefault(x => x.Groups["name"].Value == name);
            Assert.True(m is not null, $"var {name} not found in the index.html boot script.");
            return QuotedName().Matches(m!.Groups["body"].Value).Select(x => x.Groups["name"].Value).ToArray();
        }

        Assert.Equal(ThemeVocabulary.Bases, List("bases"));
        Assert.Equal(ThemeVocabulary.Accents, List("accents"));
        var defaults = BootScriptDefaults().Match(html);
        Assert.True(defaults.Success, "the boot script's defaults were not found in index.html.");
        Assert.Equal(ThemeVocabulary.DefaultBase, defaults.Groups["base"].Value);
        Assert.Equal(ThemeVocabulary.DefaultAccent, defaults.Groups["accent"].Value);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Dark", false)]
    [InlineData("dark ", false)]
    [InlineData("sepia", true)]
    public void IsBase_IsExactAndCaseSensitive(string? value, bool expected)
    {
        Assert.Equal(expected, ThemeVocabulary.IsBase(value));
    }
}
