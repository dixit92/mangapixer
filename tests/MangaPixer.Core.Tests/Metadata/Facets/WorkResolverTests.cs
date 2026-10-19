namespace com.lifepixer.mangapixer.Tests.Core.Metadata.Facets;

using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// Unit tests for <see cref="WorkResolver"/> (1.45.0 lane F, D1 2.1 rules 1-6, owner answers 2026-10-19): a split series counts once, the
/// same record in two libraries is one work, the owner's "(3)" example counts the one-shot ARCHIVE, a folder holding two series is split by
/// its ComicInfo, collection-about and artist folders open their items, Don't match keeps ComicInfo only, Needs review is unlinked, and the
/// owner's Q5 condition - a ComicInfo work key carries RESOLVED creator ids, so alias spellings of one creator merge two folders and two
/// people sharing a spelling do not. Every node is the location of at most one work. Synthetic names only.
/// </summary>
public sealed class WorkResolverTests
{
    private static void AssertOneWorkPerNode(IReadOnlyList<FacetResolvedWork> works)
    {
        var nodes = works.SelectMany(w => w.Locations.Select(l => l.NodeId)).ToList();
        Assert.Equal(nodes.Count, nodes.Distinct().Count());
        var archives = works.SelectMany(w => w.Locations.SelectMany(l => l.ArchiveIds)).ToList();
        Assert.Equal(archives.Count, archives.Distinct().Count());
    }

    private static FacetResolvedWork Only(IReadOnlyList<FacetResolvedWork> works, string key) =>
        Assert.Single(works, w => string.Equals(w.Key, key, StringComparison.Ordinal));

    [Fact]
    public void ASplitSeries_IsOneWork_WithBothFolders_AsLocations()
    {
        var lib = new FacetTestLibrary();
        var first = lib.Folder("Series Alpha c001-100");
        lib.Archives(first, "Series Alpha c{0:000}.cbz", 3);
        var second = lib.Folder("Series Alpha c101-200");
        lib.Archives(second, "Series Alpha c{0:000}.cbz", 3);
        lib.Link(first, SeriesLinkState.Confirmed, 10, "Series Alpha");
        lib.Link(second, SeriesLinkState.Auto, 10);

        var works = lib.Works();

        var work = Assert.Single(works);
        Assert.Equal("rec:10", work.Key);
        Assert.Equal(FacetWorkKind.Record, work.Kind);
        Assert.Equal("Series Alpha", work.Title);
        Assert.Equal(new[] { first, second }, work.Locations.Select(l => l.NodeId).ToArray());
        Assert.All(work.Locations, l => Assert.Equal(3, l.ArchiveIds.Count));
        AssertOneWorkPerNode(works);
    }

    [Fact]
    public void TheSameRecord_InTwoLibraries_GivesTheSameKey()
    {
        var a = new FacetTestLibrary(1);
        var fa = a.Folder("Series Alpha");
        a.Archives(fa, "Series Alpha v{0:00}.cbz", 2);
        a.Link(fa, SeriesLinkState.Confirmed, 10, "Series Alpha");
        var b = new FacetTestLibrary(100);
        var fb = b.Folder("Alpha (other library)");
        b.Archives(fb, "Alpha v{0:00}.cbz", 2);
        b.Link(fb, SeriesLinkState.Confirmed, 10, "Series Alpha");

        Assert.Equal("rec:10", Assert.Single(a.Works()).Key);
        Assert.Equal("rec:10", Assert.Single(b.Works()).Key);
    }

    [Fact]
    public void TheOwnersExample_TwoLinkedSeries_AndAOneShotArchive_InAOneShotsFolder()
    {
        var lib = new FacetTestLibrary();
        var seriesA = lib.Folder("Series A");
        lib.Archives(seriesA, "Series A chapter{0}.cbz", 4);
        lib.Link(seriesA, SeriesLinkState.Confirmed, 1, "Series A");
        var seriesB = lib.Folder("Series B");
        lib.Archives(seriesB, "Series B volume{0}.cbz", 3);
        lib.Link(seriesB, SeriesLinkState.Confirmed, 2, "Series B");
        var oneShots = lib.Folder("One Shots");
        var linkedOneShot = lib.Archive("Gamma Story.cbz", oneShots);
        var otherOneShot = lib.Archive("Delta Night.cbz", oneShots);
        var thirdOneShot = lib.Archive("Epsilon Sky.cbz", oneShots);
        lib.Link(linkedOneShot, SeriesLinkState.Confirmed, 3, "Gamma Story");

        var works = lib.Works();

        Assert.Equal(new[] { "node:" + otherOneShot, "node:" + thirdOneShot, "rec:1", "rec:2", "rec:3" },
            works.Select(w => w.Key).Order(StringComparer.Ordinal).ToArray());
        // The one-shot work is located at the ARCHIVE, not its folder; the folder is no work at all.
        Assert.Equal(new[] { linkedOneShot }, Only(works, "rec:3").Locations.Select(l => l.NodeId).ToArray());
        Assert.DoesNotContain(works, w => w.Locations.Any(l => l.NodeId == oneShots));
        AssertOneWorkPerNode(works);
    }

    [Fact]
    public void AnUnlinkedFolderHoldingTwoSeries_IsSplitByItsComicInfo_WithARemainder()
    {
        var lib = new FacetTestLibrary();
        var folder = lib.Folder("Mixed Bag");
        var archives = lib.Archives(folder, "Volume {0:00}.cbz", 5);
        lib.Ci(archives[0], "Alpha Saga");
        lib.Ci(archives[1], "alpha saga");
        lib.Ci(archives[2], "Beta Tales");
        lib.Ci(archives[3], "Beta Tales");

        var works = lib.Works();

        Assert.Equal(3, works.Count);
        var alpha = Only(works, "node:" + archives[0]);
        Assert.Equal(new[] { archives[0], archives[1] }, alpha.Locations.Select(l => l.NodeId).ToArray());
        Assert.Equal("Alpha Saga", alpha.Title);
        Assert.Equal(new[] { archives[2], archives[3] }, Only(works, "node:" + archives[2]).Locations.Select(l => l.NodeId).ToArray());
        var remainder = Only(works, "node:" + folder);
        Assert.Equal(folder, Assert.Single(remainder.Locations).NodeId);
        Assert.Equal(new[] { archives[4] }, remainder.Locations[0].ArchiveIds.ToArray());
        AssertOneWorkPerNode(works);
    }

    [Fact]
    public void ALinkedFolder_StaysOneWork_WhateverItsComicInfoSays()
    {
        var lib = new FacetTestLibrary();
        var folder = lib.Folder("Mixed Bag");
        var archives = lib.Archives(folder, "Volume {0:00}.cbz", 4);
        lib.Ci(archives[0], "Alpha Saga");
        lib.Ci(archives[1], "Beta Tales");
        lib.Link(folder, SeriesLinkState.Confirmed, 7, "Alpha Saga");

        var work = Assert.Single(lib.Works());
        Assert.Equal("rec:7", work.Key);
        Assert.Equal(4, Assert.Single(work.Locations).ArchiveIds.Count);
    }

    [Fact]
    public void ACollectionAboutFolder_IsNoWork_ItsItemsAre_AndCarryTheAboutRecord()
    {
        var lib = new FacetTestLibrary();
        var collection = lib.Folder("Fan Works");
        var one = lib.Archive("Alpha Doujin.cbz", collection);
        var two = lib.Archive("Beta Comic.cbz", collection);
        var sub = lib.Folder("Gamma Series", collection);
        lib.Archives(sub, "Gamma Series v{0:00}.cbz", 3);
        lib.Link(collection, SeriesLinkState.CollectionAbout, 50, "The Original");

        var works = lib.Works();

        Assert.DoesNotContain(works, w => w.Locations.Any(l => l.NodeId == collection));
        Assert.Equal(new[] { "node:" + one, "node:" + two, "node:" + sub }, works.Select(w => w.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.All(works, w => Assert.All(w.Locations, l => Assert.Equal(50, l.AboutRecordId)));
        Assert.DoesNotContain(works, w => w.Key == "rec:50");
        AssertOneWorkPerNode(works);
    }

    [Fact]
    public void AnArtistFolder_IsNoWork_EachArchiveIs_WithoutAnAbout()
    {
        var lib = new FacetTestLibrary();
        var artist = lib.Folder("Some Artist");
        var archives = new[] { lib.Archive("Alpha.cbz", artist), lib.Archive("Beta Two.cbz", artist), lib.Archive("Gamma Three.cbz", artist) };
        lib.Link(artist, SeriesLinkState.ArtistFolder);

        var works = lib.Works();

        Assert.Equal(archives.Select(a => "node:" + a).Order(StringComparer.Ordinal).ToArray(), works.Select(w => w.Key).Order(StringComparer.Ordinal).ToArray());
        Assert.All(works, w => Assert.Null(Assert.Single(w.Locations).AboutRecordId));
    }

    [Fact]
    public void DontMatch_KeepsComicInfoOnly_AndCutsANodeOutOfTheSeriesAbove()
    {
        var lib = new FacetTestLibrary();
        var series = lib.Folder("Series Alpha");
        lib.Archives(series, "Series Alpha v{0:00}.cbz", 2);
        var extras = lib.Folder("Unrelated Extras", series);
        var extraArchives = lib.Archives(extras, "Unrelated Extras {0:00}.cbz", 2);
        lib.Link(series, SeriesLinkState.Confirmed, 10, "Series Alpha");
        lib.Link(extras, SeriesLinkState.DontMatch);
        lib.Ci(extraArchives[0], "Unrelated", new FacetCredit("Pen Name", "writer"));
        lib.Ci(extraArchives[1], "Unrelated", new FacetCredit("Pen Name", "writer"));

        var works = lib.Works(new CreatorResolver(CreatorRegistry.Empty));

        var record = Only(works, "rec:10");
        Assert.Equal(series, Assert.Single(record.Locations).NodeId);
        Assert.Equal(2, record.Locations[0].ArchiveIds.Count); // The Don't match folder's archives are not the record's.
        var cut = Assert.Single(works, w => w.Kind == FacetWorkKind.ComicInfo);
        Assert.StartsWith("ci:unrelated#", cut.Key, StringComparison.Ordinal);
        Assert.Equal(extras, Assert.Single(cut.Locations).NodeId);
        AssertOneWorkPerNode(works);
    }

    [Fact]
    public void NeedsReview_IsUnlinked_ItsCandidateRecordIsNotAWork()
    {
        var lib = new FacetTestLibrary();
        var folder = lib.Folder("Series Alpha");
        lib.Archives(folder, "Series Alpha v{0:00}.cbz", 3);
        lib.Link(folder, SeriesLinkState.NeedsReview, 9, "A Candidate");

        var work = Assert.Single(lib.Works());
        Assert.Equal("node:" + folder, work.Key);
        Assert.Null(work.RecordId);
        Assert.Equal("Series Alpha", work.Title);
    }

    [Fact]
    public void ANeedsReviewRow_BelowALinkedSeries_StaysPartOfIt()
    {
        var lib = new FacetTestLibrary();
        var series = lib.Folder("Series Alpha");
        var volumes = lib.Folder("Volumes", series);
        var archives = lib.Archives(volumes, "Series Alpha v{0:00}.cbz", 2);
        lib.Link(series, SeriesLinkState.Confirmed, 10, "Series Alpha");
        lib.Link(volumes, SeriesLinkState.NeedsReview, 11);
        lib.Link(archives[0], SeriesLinkState.Confirmed, 10); // The same series again: no second location.

        var work = Assert.Single(lib.Works());
        Assert.Equal(series, Assert.Single(work.Locations).NodeId);
        Assert.Equal(archives.Order().ToArray(), work.Locations[0].ArchiveIds.ToArray());
    }

    [Fact]
    public void AnArchiveLinkedToAnotherRecord_InsideALinkedSeries_IsItsOwnWork()
    {
        var lib = new FacetTestLibrary();
        var series = lib.Folder("Series Alpha");
        var archives = lib.Archives(series, "Series Alpha v{0:00}.cbz", 3);
        lib.Link(series, SeriesLinkState.Confirmed, 10, "Series Alpha");
        lib.Link(archives[2], SeriesLinkState.Confirmed, 20, "Side Story");

        var works = lib.Works();

        Assert.Equal(new[] { archives[0], archives[1] }, Only(works, "rec:10").Locations[0].ArchiveIds.ToArray());
        Assert.Equal(archives[2], Assert.Single(Only(works, "rec:20").Locations).NodeId);
        AssertOneWorkPerNode(works);
    }

    // --- Owner Q5: ComicInfo-only works across folders merge only on the same series AND a same RESOLVED creator. ---

    private static CreatorResolver ProviderBacked(params (string Ref, string[] Names)[] people) =>
        new(CreatorRegistry.Empty with
        {
            ProviderCreators = people.Select((p, i) => (p.Ref, Id: (long)(i + 1))).ToDictionary(x => x.Ref, x => x.Id),
            ProviderNames = people.ToDictionary(p => p.Ref, p => (IReadOnlyCollection<string>)p.Names),
        });

    [Fact]
    public void Q5_AliasSpellingsOfOneProviderBackedCreator_OnTwoUnlinkedFolders_WithTheSameSeries_AreOneWork()
    {
        var lib = new FacetTestLibrary();
        var first = lib.Folder("Saga Part One");
        var a = lib.Archives(first, "Saga Part One {0:00}.cbz", 2);
        var second = lib.Folder("Saga Part Two");
        var b = lib.Archives(second, "Saga Part Two {0:00}.cbz", 2);
        foreach (var id in a)
            lib.Ci(id, "Synthetic Saga", new FacetCredit("Hiroshi Tanaka", "writer"));
        foreach (var id in b)
            lib.Ci(id, "Synthetic Saga", new FacetCredit("Kenta Mori", "Writer")); // the same person's pen name

        var resolver = ProviderBacked(("mangaupdates:7", new[] { "Tanaka Hiroshi", "Kenta Mori" }));
        var works = lib.Works(resolver);

        var work = Assert.Single(works);
        Assert.Equal("ci:synthetic saga#1", work.Key);
        Assert.Equal(new[] { first, second }, work.Locations.Select(l => l.NodeId).ToArray());

        // Without the alias (no provider author record naming both spellings), the two folders are two works.
        var unaliased = lib.Works(ProviderBacked(("mangaupdates:7", new[] { "Tanaka Hiroshi" })));
        Assert.Equal(2, unaliased.Count);
    }

    [Fact]
    public void Q5_TwoDifferentCreatorsSharingASpelling_AreTwoWorks_UnlessResolutionMergesThem()
    {
        var lib = new FacetTestLibrary();
        var first = lib.Folder("Saga Part One");
        var a = lib.Archives(first, "Saga Part One {0:00}.cbz", 2);
        var second = lib.Folder("Saga Part Two");
        var b = lib.Archives(second, "Saga Part Two {0:00}.cbz", 2);
        // The first folder's declared artist is the provider author 7 (an admin picked it); the second folder's "Sato" is somebody else.
        lib.Declare(first, DeclaredFactKeys.Creator, "Sato", "author", externalRef: "mangaupdates:7");
        foreach (var id in a.Concat(b))
            lib.Ci(id, "Synthetic Saga", new FacetCredit("Sato", "writer"));

        var works = lib.Works(ProviderBacked(("mangaupdates:7", new[] { "Sato" })));

        Assert.Equal(2, works.Count);
        Assert.All(works, w => Assert.Equal(FacetWorkKind.ComicInfo, w.Kind));
        Assert.Contains(works, w => w.Key == "ci:synthetic saga#1" && w.Locations.Single().NodeId == first);
        Assert.Contains(works, w => w.Key != "ci:synthetic saga#1" && w.Locations.Single().NodeId == second);

        // Resolution merging them (no declaration: both "Sato" credits are the one name-only creator of that key) makes one work.
        var plain = new FacetTestLibrary();
        var p1 = plain.Folder("Saga Part One");
        var p2 = plain.Folder("Saga Part Two");
        foreach (var id in plain.Archives(p1, "Saga Part One {0:00}.cbz", 2).Concat(plain.Archives(p2, "Saga Part Two {0:00}.cbz", 2)))
            plain.Ci(id, "Synthetic Saga", new FacetCredit("Sato", "writer"));
        Assert.Single(plain.Works(ProviderBacked(("mangaupdates:7", new[] { "Sato" }))));
    }

    [Fact]
    public void Q5_TheSameSeries_WithoutASharedCreator_IsTwoWorks_AndNoCreatorMeansANodeKey()
    {
        var lib = new FacetTestLibrary();
        var first = lib.Folder("Oneshot Alpha");
        var a = lib.Archives(first, "Oneshot Alpha {0:00}.cbz", 2);
        var second = lib.Folder("Oneshot Beta");
        var b = lib.Archives(second, "Oneshot Beta {0:00}.cbz", 2);
        var third = lib.Folder("Oneshot Gamma");
        var c = lib.Archives(third, "Oneshot Gamma {0:00}.cbz", 2);
        foreach (var id in a)
            lib.Ci(id, "Oneshot", new FacetCredit("Alpha Writer", "writer"));
        foreach (var id in b)
            lib.Ci(id, "Oneshot", new FacetCredit("Beta Writer", "writer"));
        foreach (var id in c)
            lib.Ci(id, "Oneshot", new FacetCredit("Gamma Colorist", "colorist")); // not an indexed role: no creator

        var works = lib.Works(new CreatorResolver(CreatorRegistry.Empty));

        Assert.Equal(3, works.Count);
        Assert.Equal(2, works.Count(w => w.Kind == FacetWorkKind.ComicInfo));
        Assert.Equal("node:" + third, Assert.Single(works, w => w.Kind == FacetWorkKind.Node).Key);
    }

    [Fact]
    public void Q5_AComicInfoWorkOfAnotherLibrary_SharesItsKey_OnTheSameSeriesAndASharedCreator()
    {
        var lib = new FacetTestLibrary();
        var folder = lib.Folder("Saga");
        foreach (var id in lib.Archives(folder, "Saga {0:00}.cbz", 2))
            lib.Ci(id, "Synthetic Saga", new FacetCredit("Hiroshi Tanaka", "writer"), new FacetCredit("Second Artist", "penciller"));
        var resolver = ProviderBacked(("mangaupdates:7", new[] { "Hiroshi Tanaka" }));
        var other = new FacetComicInfoWorkKey("ci:synthetic saga#1,9", "synthetic saga", [1, 9]);
        var unrelated = new FacetComicInfoWorkKey("ci:synthetic saga#8", "synthetic saga", [8]);

        Assert.Equal("ci:synthetic saga#1,9", Assert.Single(lib.Works(resolver, [unrelated, other])).Key);
        Assert.NotEqual("ci:synthetic saga#8", Assert.Single(lib.Works(resolver, [unrelated])).Key);
    }

    [Fact]
    public void Containers_AndWrappers_AreNeverWorks_TheirChildrenAre()
    {
        var lib = new FacetTestLibrary();
        var category = lib.Folder("Manga");
        var alpha = lib.Folder("Alpha Series", category);
        lib.Archives(alpha, "Alpha Series v{0:00}.cbz", 2);
        var beta = lib.Folder("Beta Story", category);
        lib.Archives(beta, "Beta Story v{0:00}.cbz", 2);
        var wrapper = lib.Folder("Wrapper", category);
        var inner = lib.Folder("Gamma Saga", wrapper);
        lib.Archives(inner, "Gamma Saga v{0:00}.cbz", 2);

        var works = lib.Works();

        Assert.Equal(new[] { "node:" + alpha, "node:" + beta, "node:" + inner }, works.Select(w => w.Key).Order(StringComparer.Ordinal).ToArray());
        AssertOneWorkPerNode(works);
    }
}
