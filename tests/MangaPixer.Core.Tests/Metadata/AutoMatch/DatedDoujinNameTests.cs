namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// 1.34.2 dated doujin names (<c>Creator] [yyyy-mm] Character (Tag) (Title)</c>): the parser (<see cref="DatedDoujinName"/>), the
/// cleaned title and base title (<see cref="TitleNormalizer"/>), the anatomy, the detector's grouping, the planner's searches with the
/// review-only character fallback, and the scorer's cap. The undated convention <c>[Creator] Title (Parody)</c> must read as before.
/// Synthetic names only.
/// </summary>
public sealed class DatedDoujinNameTests
{
    // --- The parser ---

    [Fact]
    public void CharacterTagTitle_TheLastGroupIsTheTitle()
    {
        var d = DatedDoujinName.TryParse("Artist Name] [2023-05] Character Name (Some Tag) (The Real Title).cbz");

        Assert.NotNull(d);
        Assert.Equal("Artist Name", d!.Creator);
        Assert.Equal("The Real Title", d.Title);
        Assert.Equal("Character Name", d.Character);
        Assert.Equal(["Some Tag"], d.Tags);
    }

    [Fact]
    public void CharacterTitle_TheOneGroupIsTheTitle()
    {
        var d = DatedDoujinName.TryParse("Artist Name] [2023-05] Character Name (The Real Title).zip");

        Assert.Equal(("The Real Title", "Character Name"), (d?.Title, d?.Character));
        Assert.Empty(d!.Tags);
    }

    [Theory]
    [InlineData("[Artist Name] [2023-05] Character Name (The Real Title).cbz")]
    [InlineData("Artist Name] [2023-05-17] Character Name (The Real Title).cbz")]
    [InlineData("Artist Name] [2019] Character Name (The Real Title).cbz")]
    [InlineData("(Event 5) [Artist Name] [2023-05] Character Name (The Real Title).cbz")]
    public void EveryDateForm_AndBothTagForms_AreRead(string name) =>
        Assert.Equal("The Real Title", DatedDoujinName.TryParse(name)?.Title);

    [Fact]
    public void ReleaseTags_AreNeverTheTitle()
    {
        var d = DatedDoujinName.TryParse("Artist Name] [2023-05] Character Name (Some Tag) (The Real Title) (x1600) [Decensored] [English].cbz");

        Assert.Equal(("The Real Title", "Character Name"), (d?.Title, d?.Character));
        Assert.Equal(["Some Tag"], d!.Tags);
    }

    [Theory]
    [InlineData("Artist Name] [2023-05] Character Name (x1600).cbz")]
    [InlineData("Artist Name] [2023-05] Character Name.cbz")]
    [InlineData("[Artist Name] [2023-05] Character Name [Decensored].cbz")]
    public void NoTitleGroup_HasNoTitle_AndKeepsTheText(string name)
    {
        var d = DatedDoujinName.TryParse(name);

        Assert.NotNull(d);
        Assert.Null(d!.Title);
        Assert.Equal("Character Name", d.Character);
    }

    [Fact]
    public void TextAfterTheTitle_BelongsToIt_WithItsSeparator()
    {
        Assert.Equal("The Real Title - Part 2", DatedDoujinName.TryParse("Artist] [2023-05] Character (Tag) (The Real Title) - Part 2.cbz")?.Title);
        Assert.Equal("The Real Title 2", DatedDoujinName.TryParse("Artist] [2023-05] Character (The Real Title) 2 (x1600).cbz")?.Title);
    }

    [Fact]
    public void NestedGroupInTheTitle_StaysInTheTitle() =>
        Assert.Equal("The Real Title Part 2", DatedDoujinName.TryParse("Artist] [2023-05] Character (The Real Title (Part 2)).cbz")?.Title);

    [Fact]
    public void TitleWithoutACharacter()
    {
        var d = DatedDoujinName.TryParse("Artist] [2023-05] (The Real Title).cbz");

        Assert.Equal("The Real Title", d?.Title);
        Assert.Null(d!.Character);
    }

    [Theory]
    [InlineData("[Artist Name] Some Title (Some Parody).cbz")]             // the common convention: no date
    [InlineData("[Artist Name] Some Title (Some Parody) [2023-05].cbz")]   // a date elsewhere is not the gate
    [InlineData("[Artist Name] (2023) Some Title (Some Parody).cbz")]      // a year group is not a bracketed date
    [InlineData("[Artist Name] [2023-13] Character (Title).cbz")]          // no month 13
    [InlineData("[Artist Name] [23-05] Character (Title).cbz")]
    [InlineData("[123] [2023-05] Character (Title).cbz")]                  // a creator tag has letters
    [InlineData("Some Series v01 [2023-05] (Title).cbz")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherNames_AreNotDated(string? name) => Assert.Null(DatedDoujinName.TryParse(name));

    // --- The cleaned title and the base title ---

    [Fact]
    public void Normalize_SearchesTheTitle_NotTheCharacter()
    {
        Assert.Equal("The Real Title", TitleNormalizer.Normalize("Artist Name] [2023-05] Character Name (Some Tag) (The Real Title).cbz").Primary);
        Assert.Equal("The Real Title", TitleNormalizer.Normalize("[Artist Name] [2023-05] Character Name (The Real Title) [English].cbz").Primary);
        // The subtitle split still finds the title alone.
        var parted = TitleNormalizer.Normalize("Artist] [2023-05] Character (The Real Title) - Part 2.cbz");
        Assert.Contains(parted.Derived, x => x.Text == "The Real Title");
    }

    [Fact]
    public void Normalize_WithoutATitleGroup_IsUnchanged()
    {
        Assert.Equal("Character Name", TitleNormalizer.Normalize("Artist Name] [2023-05] Character Name (x1600).cbz").Primary);
        Assert.Equal("Character Name", TitleNormalizer.Normalize("Artist Name] [2023-05] Character Name.cbz").Primary);
    }

    [Fact]
    public void Normalize_TheUndatedConvention_IsUnchanged() =>
        Assert.Equal("Some Title", TitleNormalizer.Normalize("[Artist Name] Some Title (Some Parody) [English].cbz").Primary);

    [Fact]
    public void BaseTitle_IsTheTitle_SoANumberedPairStaysOneWork()
    {
        Assert.Equal("The Real Title", TitleNormalizer.ArchiveBaseTitle("Artist] [2023-05] Character (Tag) (The Real Title).cbz"));
        Assert.Equal("The Real Title", TitleNormalizer.ArchiveBaseTitle("Artist] [2023-06] Character (Tag) (The Real Title 2).cbz"));
    }

    // --- The anatomy and the author hint ---

    [Fact]
    public void Anatomy_DatedName_IsDoujinShaped_WithItsCreator_AndNoParody()
    {
        var a = ArchiveNameAnatomy.Parse("Circle Name (Artist Name)] [2023-05] Character Name (Some Tag) (The Real Title).cbz");

        Assert.True(a.IsDated);
        Assert.True(a.IsDoujinShaped);
        Assert.Equal("Circle Name (Artist Name)", a.LeadingTag);
        Assert.Equal(("Circle Name", "Artist Name"), (a.Circle, a.Artist));
        Assert.Null(a.Parody);
        Assert.Equal("The Real Title", a.Title);
    }

    [Fact]
    public void Anatomy_BalancedDatedName_HasNoParodyEither()
    {
        var a = ArchiveNameAnatomy.Parse("[Artist Name] [2023-05] Character Name (Some Tag) (The Real Title).cbz");

        Assert.Equal("Artist Name", a.LeadingTag);
        Assert.Null(a.Parody);
        Assert.True(a.IsDoujinShaped);
    }

    [Fact]
    public void Anatomy_TheUndatedConvention_KeepsItsParody()
    {
        var a = ArchiveNameAnatomy.Parse("[Artist Name] Some Title (Some Parody).cbz");

        Assert.False(a.IsDated);
        Assert.Equal("Some Parody", a.Parody);
        Assert.True(a.IsDoujinShaped);
    }

    [Fact]
    public void Anatomy_DatedChapter_IsNotDoujinShaped() =>
        Assert.False(ArchiveNameAnatomy.Parse("Group Name] [2023-05] Some Series Ch. 12 (Some Title).cbz").IsDoujinShaped);

    [Fact]
    public void SameAuthor_ReadsTheCreator_OfBothTagForms()
    {
        Assert.Equal(["Artist Name"], ReviewAuthorNames.FromWorkName("Artist Name] [2023-05] Character (Tag) (Title).cbz").Select(n => n.Label));
        Assert.Equal(["Artist Name"], ReviewAuthorNames.FromWorkName("[Artist Name] [2023-05] Character (Tag) (Title).cbz").Select(n => n.Label));
    }

    // --- The detector: one work per title, not per character ---

    private readonly WorkDetector _detector = new();
    private readonly MatchQueryPlanner _planner = new();
    private readonly MatchScorer _scorer = new();

    private static readonly string[] Dated =
    [
        "Artist One] [2023-05] Hero Name (Some Tag) (First Story).cbz",
        "Artist Two] [2023-06] Hero Name (Some Tag) (Second Story).cbz",
        "Artist Three] [2023-07] Hero Name (Third Story) (x1600).cbz",
        "Artist One] [2023-08] Other Hero (Fourth Story 1).cbz",
        "Artist One] [2023-09] Other Hero (Fourth Story 2).cbz",
    ];

    private static FolderShape Shape(IReadOnlyList<string> archives, bool collection = true) =>
        new("Starlight Academy", 2, archives, [], "Doujin", null, null, collection, collection ? "Starlight Academy" : null);

    [Fact]
    public void Detector_GroupsByTitle_NotByCharacter()
    {
        var c = _detector.Classify(Shape(Dated));

        Assert.Equal(WorkClass.CollectionLeaf, c.Class);
        Assert.Equal(
            ["First Story: 0", "Second Story: 1", "Third Story: 2", "Fourth Story: 3,4"],
            c.ArchiveGroups.Select(g => $"{g.QueryTitle}: {string.Join(',', g.ArchiveIndexes)}"));
    }

    [Fact]
    public void Detector_UnbalancedDatedNames_SuggestDoujinContent() =>
        Assert.Equal(ContentSuggestion.DoujinshiAndAdultOneShots, _detector.Classify(Shape(Dated, collection: false)).ContentSuggestion);

    // --- The planner: the title first, the series' dj form second, the character last ---

    [Fact]
    public void Planner_InACollection_SearchesTheTitle_ThenTheDjForm_ThenTheCharacter()
    {
        var folder = Shape(Dated);
        var c = _detector.Classify(folder);
        var plan = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups[0]);

        Assert.Equal(("First Story", QueryVariantKind.Primary), (plan.Variants[0].Text, plan.Variants[0].Kind));
        Assert.Equal(("Starlight Academy dj - First Story", QueryVariantKind.DoujinParodyForm), (plan.Variants[1].Text, plan.Variants[1].Kind));
        Assert.Equal(("Hero Name", QueryVariantKind.CharacterName), (plan.Variants[^1].Text, plan.Variants[^1].Kind));
        Assert.DoesNotContain(plan.Variants, v => v.Text.Contains("Some Tag", StringComparison.Ordinal));
        Assert.Contains("Artist One", plan.Context.AuthorTags);
    }

    [Fact]
    public void Planner_OneShotFolder_OfADatedArchive_SearchesItsTitle()
    {
        var folder = new FolderShape("Artist] [2023-05] Hero Name (Some Tag) (First Story)", 2,
            ["Artist] [2023-05] Hero Name (Some Tag) (First Story).cbz"], []);
        var plan = _planner.PlanFolder(folder, _detector.Classify(folder));

        Assert.Equal("First Story", plan.Variants[0].Text);
        Assert.Equal(("Hero Name", QueryVariantKind.CharacterName), (plan.Variants[^1].Text, plan.Variants[^1].Kind));
    }

    [Fact]
    public void Planner_NoCharacterSearch_WithoutATitleGroup_OrForTheUndatedConvention()
    {
        foreach (var name in new[] { "Artist] [2023-05] Hero Name (x1600).cbz", "[Artist Name] Some Title (Some Parody).cbz" })
        {
            var folder = Shape([name, "[Other Artist] Another Story.cbz", "[Third Artist] Third Story.cbz"]);
            var c = _detector.Classify(folder);
            var plan = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups[0]);
            Assert.DoesNotContain(plan.Variants, v => v.Kind == QueryVariantKind.CharacterName);
        }
    }

    // --- The scorer: a record named after the character never links on its own ---

    private static MatchCandidate Rec(string id, string title, MetadataFormat format = MetadataFormat.Doujinshi) =>
        new("mangaupdates", id, title, [], format, "Manga", null, null, null, [], [], null);

    [Fact]
    public void RecordNamedAfterTheCharacter_IsReviewOnly()
    {
        var folder = Shape(Dated);
        var c = _detector.Classify(folder);
        var plan = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups[0]);

        var o = _scorer.Score(plan, [Rec("1", "Hero Name", MetadataFormat.Comic)], MatchThresholds.Default);
        Assert.Equal(MatchScorer.SubtitleHeadCap, o.Ranked[0].TitleScore, 3);
        Assert.NotEqual(MatchBand.Auto, o.Band);
    }

    [Fact]
    public void RecordOfTheTitle_StillLinks()
    {
        var folder = Shape(Dated);
        var c = _detector.Classify(folder);
        var plan = _planner.PlanArchiveGroup(folder, c, c.ArchiveGroups[0]);

        var o = _scorer.Score(plan, [Rec("1", "Starlight Academy dj - First Story"), Rec("2", "Hero Name", MetadataFormat.Comic)], MatchThresholds.Default);
        Assert.Equal("1", o.Ranked[0].Candidate.ExternalId);
        Assert.Equal(MatchBand.Auto, o.Band);
    }
}
