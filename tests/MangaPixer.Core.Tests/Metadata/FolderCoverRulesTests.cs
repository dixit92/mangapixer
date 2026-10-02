namespace com.lifepixer.mangapixer.Tests.Core.Metadata;

using com.lifepixer.mangapixer.Core.Metadata;
using Xunit;

/// <summary>
/// Unit tests (1.32.0) of the folder cover preference precedence: a folder's explicit value beats the library's "Show saved web
/// covers" switch in both directions; no value inherits the switch; an admin's explicit web cover choice ignores an inherited
/// "File covers" but not the library switch (unless a folder says Web); only "File covers" skips background web work.
/// </summary>
public sealed class FolderCoverRulesTests
{
    [Theory]
    [InlineData(null, false, true)]  // no row: the library shows web covers
    [InlineData(null, true, false)]  // no row: the library hides them
    [InlineData(FolderCoverPreference.Web, false, true)]
    [InlineData(FolderCoverPreference.Web, true, true)]   // the folder wins over a hiding library
    [InlineData(FolderCoverPreference.File, false, false)] // the folder wins over a showing library
    [InlineData(FolderCoverPreference.File, true, false)]
    public void WebShown_TheFolderValueWinsOverTheLibrarySwitch(FolderCoverPreference? folder, bool libraryHidden, bool expected) =>
        Assert.Equal(expected, FolderCoverRules.WebShown(folder, libraryHidden));

    [Theory]
    [InlineData(null, false, true)]
    [InlineData(null, true, false)]
    [InlineData(FolderCoverPreference.Web, true, true)]
    [InlineData(FolderCoverPreference.File, false, true)] // an explicit choice beats an inherited File covers
    [InlineData(FolderCoverPreference.File, true, false)] // ... but the library still hides web covers (as before 1.32.0)
    public void ChosenWebShown_AnExplicitChoiceIgnoresFileCovers_ButNotTheLibrarySwitch(FolderCoverPreference? folder, bool libraryHidden, bool expected) =>
        Assert.Equal(expected, FolderCoverRules.ChosenWebShown(folder, libraryHidden));

    [Theory]
    [InlineData(null, false)]
    [InlineData(FolderCoverPreference.Web, false)]
    [InlineData(FolderCoverPreference.File, true)]
    public void SkipsWebWork_OnlyFileCovers(FolderCoverPreference? folder, bool expected) =>
        Assert.Equal(expected, FolderCoverRules.SkipsWebWork(folder));

    [Fact]
    public void TheStoredValuesAreStable()
    {
        // Persisted as ints in folder_cover_preferences.Preference.
        Assert.Equal(0, (int)FolderCoverPreference.Web);
        Assert.Equal(1, (int)FolderCoverPreference.File);
    }
}
