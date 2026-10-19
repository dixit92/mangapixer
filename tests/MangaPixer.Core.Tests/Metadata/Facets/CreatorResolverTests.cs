namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// Unit tests for <see cref="CreatorResolver"/> (1.45.0 lane F, D1 2.2 R1-R4, owner Q7) and the display-name rule: provider ids never merge
/// on their own, a same-work spelling joins the work's credited creator, a unique two-word name joins the one provider-backed creator, a
/// one-word or ambiguous name stays name-only (one creator per key), admin split pins win over every rule, merges redirect. Synthetic names.
/// </summary>
public sealed class CreatorResolverTests
{
    private static CreatorRegistry Registry(
        (string Ref, long Id, string[] Names)[]? providers = null,
        (string Key, long Id)[]? nameOnly = null,
        (long From, long To)[]? merges = null,
        CreatorOverride[]? overrides = null) => new(
            (providers ?? []).ToDictionary(p => p.Ref, p => p.Id),
            (nameOnly ?? []).ToDictionary(n => n.Key, n => n.Id),
            (merges ?? []).ToDictionary(m => m.From, m => m.To),
            overrides ?? [],
            (providers ?? []).ToDictionary(p => p.Ref, p => (IReadOnlyCollection<string>)p.Names));

    private static readonly (string, long, string[])[] Two =
    [
        ("mangaupdates:7", 1L, new[] { "Tanaka Hiroshi", "Kenta Mori" }),
        ("mangaupdates:8", 2L, new[] { "Sato", "Sato Yuki" }),
    ];

    [Fact]
    public void R1_AProviderReference_IsItsCreator_OrANewOne_AndTwoReferencesNeverMerge()
    {
        var resolver = new CreatorResolver(Registry(Two));

        Assert.Equal(CreatorHandle.Existing(1), resolver.Resolve(new FacetCredit("Anything", "author", "mangaupdates:7")));
        Assert.Equal(CreatorHandle.ForNewProvider("mangaupdates:9"), resolver.Resolve(new FacetCredit("Tanaka Hiroshi", "author", "mangaupdates:9")));
        Assert.NotEqual(resolver.Resolve(new FacetCredit("Same Name", "author", "mangaupdates:7")),
            resolver.Resolve(new FacetCredit("Same Name", "author", "mangaupdates:8")));
    }

    [Fact]
    public void R2_ASpellingOnTheSameWork_JoinsThatWorksCreditedCreator_EvenAOneWordName()
    {
        var resolver = new CreatorResolver(Registry(Two));

        // "Sato" is one word (R3 would not take it), but the same work credits provider author 8, whose spellings include it.
        Assert.Equal(CreatorHandle.Existing(2), resolver.Resolve(new FacetCredit("SATO", "writer"), ["mangaupdates:8"]));
        // The provider author record's other names count as the creator's spellings.
        Assert.Equal(CreatorHandle.Existing(1), resolver.Resolve(new FacetCredit("Mori, Kenta", "writer"), ["mangaupdates:7", "mangaupdates:8"]));
        // Not on the work: a one-word name stays name-only.
        Assert.Equal(CreatorHandle.ForNewName("sato"), resolver.Resolve(new FacetCredit("Sato", "writer"), ["mangaupdates:7"]));
    }

    [Fact]
    public void R3_AUniqueTwoWordName_JoinsTheOneProviderBackedCreator_AnAmbiguousOneDoesNot()
    {
        var resolver = new CreatorResolver(Registry(
        [
            ("mangaupdates:7", 1L, new[] { "Tanaka Hiroshi" }),
            ("mangaupdates:8", 2L, new[] { "Common Name" }),
            ("mangaupdates:9", 3L, new[] { "Common Name" }),
        ]));

        Assert.Equal(CreatorHandle.Existing(1), resolver.Resolve(new FacetCredit("Hiroshi Tanaka", "writer")));
        Assert.Equal(CreatorHandle.ForNewName("common name"), resolver.Resolve(new FacetCredit("Common Name", "writer")));
    }

    [Fact]
    public void R4_EveryUnresolvedCreditWithOneKey_SharesItsNameOnlyCreator()
    {
        var resolver = new CreatorResolver(Registry(Two, nameOnly: [("solo artist", 40)]));

        Assert.Equal(CreatorHandle.Existing(40), resolver.Resolve(new FacetCredit("Solo Artist", "penciller")));
        Assert.Equal(CreatorHandle.Existing(40), resolver.Resolve(new FacetCredit("artist, solo", "writer")));
        Assert.Equal(CreatorHandle.ForNewName("another one"), resolver.Resolve(new FacetCredit("Another  One", "writer")));
        Assert.Null(resolver.Resolve(new FacetCredit("   ", "writer")));
    }

    [Fact]
    public void SplitPins_WinOverEveryRule_TheNewestPinPerSubject()
    {
        var resolver = new CreatorResolver(Registry(Two, nameOnly: [("hiroshi tanaka", 50)], overrides:
        [
            new CreatorOverride(1, CreatorOverrideKind.SplitPin, "mangaupdates:7", 60),
            new CreatorOverride(2, CreatorOverrideKind.SplitPin, "mangaupdates:7", 61),
            new CreatorOverride(3, CreatorOverrideKind.SplitPin, "kenta mori", 62),
        ]));

        Assert.Equal(CreatorHandle.Existing(61), resolver.Resolve(new FacetCredit("x", "author", "mangaupdates:7")));
        // R2 / R3 would pick provider author 7 for this spelling: the pinned key wins.
        Assert.Equal(CreatorHandle.Existing(62), resolver.Resolve(new FacetCredit("Mori Kenta", "writer"), ["mangaupdates:7"]));
    }

    [Fact]
    public void AMergedCreator_RedirectsEveryHandle_AndTwoMergedProvidersCountAsOneForR3()
    {
        var resolver = new CreatorResolver(Registry(
        [
            ("mangaupdates:7", 1L, new[] { "Common Name" }),
            ("mangaupdates:8", 2L, new[] { "Common Name" }),
        ], nameOnly: [("solo", 5)], merges: [(1, 2), (5, 2)]));

        Assert.Equal(CreatorHandle.Existing(2), resolver.Resolve(new FacetCredit("x", "author", "mangaupdates:7")));
        Assert.Equal(CreatorHandle.Existing(2), resolver.Resolve(new FacetCredit("Common Name", "writer")));
        Assert.Equal(CreatorHandle.Existing(2), resolver.Resolve(new FacetCredit("Solo", "writer")));
        Assert.Equal(2, resolver.FinalId(1));
    }

    [Fact]
    public void AMergeCycle_StopsAtTheHopBound()
    {
        var resolver = new CreatorResolver(Registry(merges: [(1, 2), (2, 1)]));
        Assert.True(resolver.FinalId(1) is 1 or 2);
    }

    [Fact]
    public void TheDisplayName_IsAdmin_ThenTheProviderMainName_ThenTheMostUsedSpelling_OldestOnATie()
    {
        IEnumerable<CreatorSpellingUse> uses = [new("Mori Kenta", 2, 5), new("Kenta Mori", 2, 3), new("KENTA MORI", 1, 1)];

        Assert.Equal<(string, CreatorDisplayNameSource)?>(("Pen Name", CreatorDisplayNameSource.Admin), CreatorDisplayName.Choose(" Pen  Name ", ["Main"], uses));
        Assert.Equal<(string, CreatorDisplayNameSource)?>(("Main", CreatorDisplayNameSource.Provider), CreatorDisplayName.Choose(null, [null, "Main"], uses));
        Assert.Equal<(string, CreatorDisplayNameSource)?>(("Kenta Mori", CreatorDisplayNameSource.MostUsed), CreatorDisplayName.Choose("", [], uses));
        Assert.Null(CreatorDisplayName.Choose(null, [], []));
    }

    [Fact]
    public void TheRoleWords_FollowOwnerQ11()
    {
        Assert.Equal(FacetRoles.Writer, FacetText.RolesOf("author"));
        Assert.Equal(FacetRoles.Writer, FacetText.RolesOf("Writer"));
        Assert.Equal(FacetRoles.Artist, FacetText.RolesOf("artist"));
        Assert.Equal(FacetRoles.Artist, FacetText.RolesOf("penciller"));
        Assert.Equal(FacetRoles.Artist, FacetText.RolesOf("inker"));
        Assert.Equal(FacetRoles.Artist, FacetText.RolesOf("coverArtist"));
        foreach (var unindexed in new[] { "colorist", "letterer", "editor", "translator", "other" })
            Assert.Null(FacetText.RolesOf(unindexed));
        Assert.Null(FacetText.RolesOf(null));
        Assert.Equal(FacetRoles.None, FacetText.RolesOf(null, declared: true));
    }

    [Fact]
    public void ScriptsAndProviderReferences()
    {
        Assert.Equal(CreatorNameScript.Latin, FacetText.Script("Éric Tanaka"));
        Assert.Equal(CreatorNameScript.Cjk, FacetText.Script("田中 ひろし"));
        Assert.Equal(CreatorNameScript.Hangul, FacetText.Script("김철수"));
        Assert.Equal(CreatorNameScript.Other, FacetText.Script("Иван"));
        Assert.Equal("mangaupdates:123", FacetText.ProviderRef("MangaUpdates", " 123 "));
        Assert.Equal(("mangaupdates", "123"), FacetText.ParseProviderRef("mangaupdates:123"));
        Assert.Null(FacetText.ParseProviderRef("tanaka hiroshi"));
        Assert.Null(FacetText.ParseProviderRef("MangaUpdates:123"));
        Assert.Equal("synthetic saga", FacetText.SeriesForm("  Synthetic \t SAGA "));
        Assert.Null(FacetText.SeriesForm(" \u0001 "));
    }
}
