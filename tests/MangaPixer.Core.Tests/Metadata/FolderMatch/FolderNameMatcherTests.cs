namespace com.lifepixer.mangapixer.Tests.Core.Metadata.FolderMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Core.Metadata.FolderMatch;
using Xunit;

/// <summary>"Match folders by name" (1.38.0): the pure name comparison. All names are synthetic.</summary>
public sealed class FolderNameMatcherTests
{
    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static CreatorCredit Mu(string? id, string name, string role, long record, int day = 0) =>
        new("mangaupdates", id, name, role, record, T0.AddDays(day));

    private static CreatorCredit Gcd(string name, string role, long record) => new("gcd", null, name, role, record, T0);

    // --- folder forms ---

    [Fact]
    public void FolderForms_AreTheWrittenNameAndTheMatchersCleanedVariants()
    {
        var forms = FolderNameMatcher.FolderForms("Moonlit Garden (Complete) [Digital]");
        Assert.Equal(TitleNormalizer.ScoringForm("Moonlit Garden Complete Digital"), forms[0]);
        Assert.Contains(TitleNormalizer.ScoringForm("Moonlit Garden"), forms);
    }

    [Fact]
    public void FolderForms_KeepATrailingEnglishTitleAsASecondVariant()
    {
        var forms = FolderNameMatcher.FolderForms("Tsuki no Niwa [Moonlit Garden]");
        Assert.Contains(TitleNormalizer.ScoringForm("Tsuki no Niwa"), forms);
        Assert.Contains(TitleNormalizer.ScoringForm("Moonlit Garden"), forms);
    }

    [Fact]
    public void FolderForms_OfAnEmptyName_AreEmpty() => Assert.Empty(FolderNameMatcher.FolderForms("  "));

    // --- artists: grouping ---

    [Fact]
    public void GroupArtists_GroupsSpellingsByTheAuthorId_AndDeclaresTheSpellingOnTheMostRecords()
    {
        var artists = FolderNameMatcher.GroupArtists([
            Mu("101", "Given Family", "author", 1),
            Mu("101", "Family Given", "author", 2),
            Mu("101", "Family Given", "artist", 3),
            Mu("202", "Other Person", "artist", 3),
        ]);
        Assert.Equal(2, artists.Count);
        var a = artists.Single(x => x.AuthorId == "101");
        Assert.Equal("Family Given", a.DeclaredName);
        Assert.Equal("author", a.Role);
        Assert.Equal(3, a.RecordCount);
        Assert.Equal(["Family Given", "Given Family"], a.Names);
        Assert.Equal("artist", artists.Single(x => x.AuthorId == "202").Role);
    }

    [Fact]
    public void GroupArtists_OnATie_DeclaresTheMostRecentlyFetchedSpelling()
    {
        var artists = FolderNameMatcher.GroupArtists([
            Mu("101", "Spelling Old", "author", 1, day: 1),
            Mu("101", "Spelling New", "author", 2, day: 5),
        ]);
        Assert.Equal("Spelling New", Assert.Single(artists).DeclaredName);
    }

    [Fact]
    public void GroupArtists_ASpellingCountsOncePerRecord()
    {
        var artists = FolderNameMatcher.GroupArtists([
            Mu("101", "Twice Here", "author", 1, day: 9),
            Mu("101", "Twice Here", "artist", 1, day: 9),
            Mu("101", "Two Records", "author", 2),
            Mu("101", "Two Records", "author", 3),
        ]);
        Assert.Equal("Two Records", Assert.Single(artists).DeclaredName);
    }

    [Fact]
    public void GroupArtists_TheStoredAuthorRecordGivesTheMainNameAndTheOtherNames()
    {
        var stored = new Dictionary<string, StoredAuthor>
        {
            ["101"] = new("101", "Main Name", ["Pen Name", "Other Spelling"]),
        };
        var artists = FolderNameMatcher.GroupArtists([Mu("101", "Record Spelling", "artist", 1)], stored);
        var a = Assert.Single(artists);
        Assert.Equal("Main Name", a.DeclaredName);
        Assert.Equal(["Main Name", "Record Spelling", "Pen Name", "Other Spelling"], a.Names);
        Assert.Equal("artist", a.Role);
    }

    [Fact]
    public void GroupArtists_IgnoresOtherRoles_AndGivesEachNameWithoutAnIdItsOwnArtist()
    {
        var artists = FolderNameMatcher.GroupArtists([
            Mu("101", "Some Editor", "other", 1),
            Gcd("Comic Writer", "author", 2),
            Gcd("comic  writer", "artist", 3),
            Gcd("Comic Penciller", "artist", 2),
            Mu(null, "No Id Person", "author", 4),
        ]);
        Assert.Equal(3, artists.Count);
        var writer = artists.Single(a => a.Provider == "gcd" && a.DeclaredName.StartsWith("Comic W", StringComparison.Ordinal));
        Assert.Equal(2, writer.RecordCount); // the same name (scoring form) on two GCD records is one artist
        Assert.Null(writer.AuthorId);
        Assert.Contains(artists, a => a.Provider == "mangaupdates" && a.AuthorId is null && a.DeclaredName == "No Id Person");
    }

    // --- artists: matching ---

    [Theory]
    [InlineData("Family Given")]
    [InlineData("Given Family")] // name order
    [InlineData("family given")]
    [InlineData("FamilyGiven")] // no space
    [InlineData("Family Given (Artbooks)")] // cleaned: tags go
    [InlineData("[Family Given]")]
    public void ArtistIndex_MatchesAnyNameOrderCaseSpacingAndCleanedForm(string folder)
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Mu("101", "Family Given", "author", 1)]));
        Assert.Equal("Family Given", Assert.Single(index.Match(folder)).DeclaredName);
    }

    [Fact]
    public void ArtistIndex_FoldsDiacriticsAndLongVowels()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Mu("101", "Sátou Kéiko", "author", 1)]));
        Assert.Single(index.Match("Sato Keiko"));
        Assert.Single(index.Match("Keiko Satou"));
    }

    [Fact]
    public void ArtistIndex_MatchesAnyOtherSpellingOfTheSameAuthor_AndDeclaresTheMainName()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([
            Mu("101", "Main Spelling", "author", 1),
            Mu("101", "Main Spelling", "author", 2),
            Mu("101", "Rare Spelling", "author", 3),
        ]));
        var hit = Assert.Single(index.Match("Rare Spelling"));
        Assert.Equal("Main Spelling", hit.DeclaredName);
    }

    [Fact]
    public void ArtistIndex_MatchesAnAliasFromTheStoredAuthorRecord_AndWorksWithoutOne()
    {
        var credits = new[] { Mu("101", "Record Spelling", "author", 1) };
        Assert.Empty(new ArtistNameIndex(FolderNameMatcher.GroupArtists(credits)).Match("Pen Name"));
        var stored = new Dictionary<string, StoredAuthor> { ["101"] = new("101", "Main Name", ["Pen Name"]) };
        var hit = Assert.Single(new ArtistNameIndex(FolderNameMatcher.GroupArtists(credits, stored)).Match("Pen Name"));
        Assert.Equal("Main Name", hit.DeclaredName);
    }

    [Fact]
    public void ArtistIndex_TwoDifferentArtistsWithTheName_AreBothReturned()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([
            Mu("101", "Shared Name", "author", 1),
            Mu("202", "Other Person", "artist", 2),
            Mu("202", "Shared Name", "artist", 3),
            Mu("202", "Other Person", "artist", 4),
        ]));
        var hits = index.Match("Shared Name");
        Assert.Equal(2, hits.Count);
        Assert.Equal(["Shared Name", "Other Person"], hits.Select(h => h.DeclaredName));
    }

    [Fact]
    public void ArtistIndex_ArtistsThatWouldDeclareTheSameNameAndRole_AreOneProposal()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([
            Mu("101", "Same Name", "author", 1),
            Gcd("Same Name", "author", 2),
        ]));
        Assert.Single(index.Match("Same Name"));
    }

    [Fact]
    public void ArtistIndex_GcdNamesMatchToo()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Gcd("Comic Penciller", "artist", 1)]));
        var hit = Assert.Single(index.Match("Comic Penciller"));
        Assert.Equal("gcd", hit.Provider);
        Assert.Equal("artist", hit.Role);
    }

    [Theory]
    [InlineData("Family")] // part of a name is not the name
    [InlineData("Family Given Other")]
    [InlineData("Someone Else")]
    [InlineData("")]
    public void ArtistIndex_NoMatch(string folder)
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Mu("101", "Family Given", "author", 1)]));
        Assert.Empty(index.Match(folder));
    }

    [Theory]
    [InlineData("Family Given", "Given Family")]
    [InlineData("Family Given", "FamilyGiven")]
    [InlineData("Ren'ai Name", "Renai Name")]
    [InlineData("Family Given", "Family Given Other")]
    [InlineData("A B C", "C A B")]
    [InlineData("Ab C", "A Bc")]
    [InlineData("Sato", "Satou")]
    [InlineData("One", "Two")]
    public void ArtistIndex_AgreesWithNamesEqual(string name, string folder)
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Mu("1", name, "author", 1)]));
        Assert.Equal(AutoMatchText.NamesEqual(name, folder), index.Match(folder).Count == 1);
    }

    // --- collections: titles ---

    [Fact]
    public void TitleIndex_MatchesTheTitleOrAnAltTitle_ByScoringForm()
    {
        var index = new TitleNameIndex([
            new TitledRecord(10, "Moonlit Garden", ["Tsuki no Niwa", "Le Jardin"]),
            new TitledRecord(11, "Another Series", []),
        ]);
        Assert.Equal(new TitleMatch(10, "Moonlit Garden"), Assert.Single(index.Match("moonlit  garden")));
        Assert.Equal(new TitleMatch(10, "Tsuki no Niwa"), Assert.Single(index.Match("Tsuki no Niwa")));
        Assert.Equal(new TitleMatch(10, "Le Jardin"), Assert.Single(index.Match("Le Jardín")));
        Assert.Equal(11, Assert.Single(index.Match("Another Series (Doujinshi)")).RecordId); // cleaned name
    }

    [Theory]
    [InlineData("Moonlit")] // a part of the title is not the title
    [InlineData("Moonlit Garden Extra")]
    [InlineData("Garden Moonlit")] // titles are not order-free
    public void TitleIndex_IsExactNotASubstring(string folder)
    {
        var index = new TitleNameIndex([new TitledRecord(10, "Moonlit Garden", [])]);
        Assert.Empty(index.Match(folder));
    }

    [Fact]
    public void TitleIndex_SeveralRecords_AreAllReturnedInGivenOrder_EachOnce()
    {
        var index = new TitleNameIndex([
            new TitledRecord(30, "Shared Title", ["Shared Title!"]),
            new TitledRecord(20, "Different", ["Shared Title"]),
        ]);
        var hits = index.Match("Shared Title");
        Assert.Equal([30L, 20L], hits.Select(h => h.RecordId));
    }

    // --- 1.39.0: "Circle (Artist)" folders ---

    [Theory]
    [InlineData("Night Owl Circle (Family Given)")]
    [InlineData("[Night Owl Circle (Family Given)]")]
    public void ArtistIndex_FindsTheArtistInsideACircleArtistName(string folder)
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Mu("101", "Family Given", "author", 1)]));

        Assert.Equal("Family Given", Assert.Single(index.Match(folder)).DeclaredName);
    }

    [Fact]
    public void ArtistIndex_FindsTheCircleToo_AndBothKnownAreTwoMatches()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([
            Mu("101", "Family Given", "author", 1),
            Mu("202", "Night Owl Circle", "artist", 2),
        ]));

        Assert.Equal("Night Owl Circle", Assert.Single(index.Match("Night Owl Circle (Unknown Person)")).DeclaredName);
        Assert.Equal(["Family Given", "Night Owl Circle"], index.Match("Night Owl Circle (Family Given)").Select(a => a.DeclaredName).Order());
    }

    [Theory]
    [InlineData("Moonlit Garden (2019)")]
    [InlineData("Moonlit Garden (Digital)")]
    public void ArtistForms_SkipABracketThatIsAYearOrATag(string folder)
    {
        Assert.Equal(FolderNameMatcher.FolderForms(folder), FolderNameMatcher.ArtistForms(folder));
    }

    [Fact]
    public void TitleIndex_DoesNotUseTheBracketNames()
    {
        var index = new TitleNameIndex([new TitledRecord(7, "Family Given", [])]);

        Assert.Empty(index.Match("Night Owl Circle (Family Given)"));
    }

    [Fact]
    public void ArtistIndex_ACreditWithAnAliasInBrackets_IsFoundByEitherName()
    {
        var index = new ArtistNameIndex(FolderNameMatcher.GroupArtists([Mu("101", "Main Pen (Second Pen)", "author", 1)]));

        Assert.Single(index.Match("Second Pen"));
        Assert.Single(index.Match("Main Pen"));
    }
}
