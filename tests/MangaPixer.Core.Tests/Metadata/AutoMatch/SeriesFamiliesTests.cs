namespace com.lifepixer.mangapixer.Tests.Core.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using Xunit;

/// <summary>
/// Unit tests for <see cref="SeriesFamilies"/> (1.30.0): which candidates of one work are the same series family (provider relations
/// either way, else a shared title head with the same author), the family's main story and each member's role. Synthetic records.
/// </summary>
public sealed class SeriesFamiliesTests
{
    private static MatchCandidate Rec(string id, string title, string[]? alt = null, int? year = null, string[]? authors = null,
        (string Id, string Rel)[]? related = null, string provider = "mangaupdates") =>
        new(provider, id, title, alt ?? [], MetadataFormat.Comic, "Manga", year, null, null, authors ?? [],
            (related ?? []).Select(r => new CandidateRelation(r.Id, r.Rel)).ToList());

    [Fact]
    public void MainStoryAndSpinOff_AreOneFamily_WithTheirRoles()
    {
        var family = SeriesFamilies.Of([
            Rec("2", "Alpha Garden - Before the Frost", related: [("1", "main story")]),
            Rec("1", "Alpha Garden", related: [("2", "spin-off")]),
            Rec("9", "Something Else"),
        ]);

        Assert.Equal(new SeriesFamilyMember(0, SeriesFamilyRole.SpinOff), family[0]);
        Assert.Equal(new SeriesFamilyMember(0, SeriesFamilyRole.MainStory), family[1]);
        Assert.Null(family[2]);
    }

    [Fact]
    public void APrequelPair_TheShorterTitleIsTheMainStory()
    {
        // The recorded MangaUpdates shape: the main series lists the subtitled record as its Prequel; that one lists it as its Sequel.
        var family = SeriesFamilies.Of([
            Rec("2", "Alpha Garden - Before the Frost", related: [("1", "sequel")]),
            Rec("1", "Alpha Garden", related: [("2", "prequel")]),
        ]);

        Assert.Equal(SeriesFamilyRole.Prequel, family[0]!.Role);
        Assert.Equal(SeriesFamilyRole.MainStory, family[1]!.Role);
    }

    [Fact]
    public void ARelationListedOnOneSideOnly_IsEnough_AndIsReadBackwards()
    {
        // A search hit carries no relations; the fetched record's list names it: 2 lists 1 as its side story, so 2 is the main story.
        var family = SeriesFamilies.Of([Rec("1", "Beta Tales"), Rec("2", "Gamma Saga", related: [("1", "side story")])]);

        Assert.Equal(SeriesFamilyRole.SideStory, family[0]!.Role);
        Assert.Equal(SeriesFamilyRole.MainStory, family[1]!.Role);
    }

    [Fact]
    public void ASequelChain_WithoutAHead_OrdersByStartYear()
    {
        var family = SeriesFamilies.Of([
            Rec("b", "Delta Road Second Run", year: 2015, related: [("a", "prequel")]),
            Rec("a", "Delta Road", year: 2010, related: [("b", "sequel")]),
        ]);

        // "Delta Road" is not a subtitle head of "Delta Road Second Run" (no break), so the earliest start leads the family.
        Assert.Equal(SeriesFamilyRole.Sequel, family[0]!.Role);
        Assert.Equal(SeriesFamilyRole.Prequel, family[1]!.Role);
    }

    [Fact]
    public void NoRelation_SharedHeadAndSameAuthor_IsTheFallback()
    {
        var same = SeriesFamilies.Of([
            Rec("1", "Alpha Garden", authors: ["SMITH Anna"]),
            Rec("2", "Alpha Garden: Before the Frost", authors: ["Smith Anna", "JONES Bert"]),
        ]);
        var otherAuthor = SeriesFamilies.Of([
            Rec("1", "Alpha Garden", authors: ["SMITH Anna"]),
            Rec("2", "Alpha Garden: Before the Frost", authors: ["JONES Bert"]),
        ]);
        var unknownAuthor = SeriesFamilies.Of([Rec("1", "Alpha Garden", authors: ["SMITH Anna"]), Rec("2", "Alpha Garden: Before the Frost")]);

        Assert.Equal(SeriesFamilyRole.MainStory, same[0]!.Role);
        Assert.Equal(SeriesFamilyRole.Related, same[1]!.Role);
        Assert.All(otherAuthor, Assert.Null);
        Assert.All(unknownAuthor, Assert.Null);
    }

    [Fact]
    public void SameTitle_SameAuthor_NoRelation_IsNotAFamily()
    {
        // Two records with the very same title and no subtitle are homonyms more often than editions: only a relation groups them.
        var family = SeriesFamilies.Of([Rec("1", "Alpha Garden", authors: ["SMITH Anna"]), Rec("2", "Alpha Garden", authors: ["SMITH Anna"])]);

        Assert.All(family, Assert.Null);
    }

    [Fact]
    public void ANonFamilyRelation_WinsOverTheFallback()
    {
        // MangaUpdates says how they relate, and it is not a family relation (a fan work): the head + author fallback is not read.
        var family = SeriesFamilies.Of([
            Rec("1", "Alpha Garden", authors: ["SMITH Anna"], related: [("2", "doujinshi")]),
            Rec("2", "Alpha Garden: Before the Frost", authors: ["SMITH Anna"]),
        ]);

        Assert.All(family, Assert.Null);
        Assert.False(SeriesFamilies.IsFamilyRelation("doujinshi"));
        Assert.True(SeriesFamilies.IsFamilyRelation("Spin-Off"));
    }

    [Fact]
    public void ThreeRecords_JoinThroughTheMainStory()
    {
        var family = SeriesFamilies.Of([
            Rec("s1", "Alpha Garden - Before the Frost"),
            Rec("s2", "Alpha Garden - Lost Gardeners"),
            Rec("m", "Alpha Garden", related: [("s1", "prequel"), ("s2", "spin-off")]),
        ]);

        Assert.All(family, m => Assert.Equal(0, m!.Group));
        Assert.Equal([SeriesFamilyRole.Prequel, SeriesFamilyRole.SpinOff, SeriesFamilyRole.MainStory], family.Select(m => m!.Role));
    }

    [Fact]
    public void AlternateStory_AndAlternateVersion_KeepTheirOwnRoles()
    {
        // The fixture's subtitle pair is an "Alternate Story" - not an edition of the same story.
        var story = SeriesFamilies.Of([Rec("2", "Alpha Garden - Before the Frost", related: [("1", "alternate story")]), Rec("1", "Alpha Garden")]);
        var version = SeriesFamilies.Of([Rec("2", "Alpha Garden - Full Colour", related: [("1", "alternate version")]), Rec("1", "Alpha Garden")]);

        Assert.Equal([SeriesFamilyRole.AlternateStory, SeriesFamilyRole.MainStory], story.Select(m => m!.Role));
        Assert.Equal([SeriesFamilyRole.Alternate, SeriesFamilyRole.MainStory], version.Select(m => m!.Role));
    }

    [Fact]
    public void TwoFamilies_KeepTheirOwnGroups()
    {
        var family = SeriesFamilies.Of([
            Rec("1", "Alpha Garden", related: [("3", "spin-off")]),
            Rec("2", "Omega Garden", related: [("4", "sequel")]),
            Rec("3", "Alpha Garden: Frost"),
            Rec("4", "Omega Garden Next"),
        ]);

        Assert.Equal([0, 1, 0, 1], family.Select(m => m!.Group));
    }

    [Fact]
    public void OtherProviders_AreNeverOneFamily()
    {
        Assert.False(SeriesFamilies.AreFamily(Rec("1", "Alpha Garden", related: [("2", "spin-off")]),
            Rec("2", "Alpha Garden: Frost", provider: "other")));
    }
}
