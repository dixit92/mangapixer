namespace com.lifepixer.mangapixer.Tests.Core.Catalog;

using com.lifepixer.mangapixer.Core.Catalog;
using Xunit;

/// <summary>
/// Unit tests for the pure grouping of stories collected in one volume (1.37.0, "tankoubon stacks"): two or more archives linked to the
/// same record form one entry in the place of their first member; one archive alone, folders and unlinked archives stay as they were;
/// the rest goes through the volume grouping unchanged, without placeholders. Synthetic names only.
/// </summary>
public sealed class StoryCollectionGroupingTests
{
    private static GroupingRow Archive(string name) => new("id:" + name, GroupingRowKind.Archive, name, name.ToLowerInvariant());

    private static GroupingRow Folder(string name) => new("id:" + name, GroupingRowKind.Folder, name, name.ToLowerInvariant());

    private static Dictionary<string, string> Links(params (string Name, string Record)[] links) =>
        links.ToDictionary(l => "id:" + l.Name, l => l.Record, StringComparer.Ordinal);

    [Fact]
    public void TwoArchivesLinkedToOneRecord_FormOneEntry_InThePlaceOfTheFirst()
    {
        var rows = new List<GroupingRow>
        {
            Archive("Sample Artist - Alpha Story"), Archive("Sample Artist - Beta Story"), Archive("Sample Artist - Gamma Story"),
            Archive("Sample Artist - Delta Story"),
        };
        var r = StoryCollectionGrouping.Group(rows, Links(("Sample Artist - Beta Story", "rec1"), ("Sample Artist - Delta Story", "rec1")));

        Assert.Equal(1, r.CollectionCount);
        Assert.Equal(0, r.StackCount);
        // Alpha, the stack (at Beta's place), Gamma - Delta is inside the stack.
        Assert.Equal(["id:Sample Artist - Alpha Story", "cs:rec1", "id:Sample Artist - Gamma Story"], r.Entries.Select(e => e.Id));
        var stack = r.Entries[1];
        Assert.Equal(VolumeEntryKind.CollectionStack, stack.Kind);
        Assert.Equal(2, stack.Rank);
        Assert.Equal("rec1", stack.Collection!.Key);
        Assert.Equal(["id:Sample Artist - Beta Story", "id:Sample Artist - Delta Story"], stack.Collection.Members.Select(m => m.Id));
        Assert.Equal(stack.Collection.Members[0].SortKey, stack.SortKey);
    }

    [Fact]
    public void OneArchiveAlone_WithARecord_StaysANormalCard()
    {
        var rows = new List<GroupingRow> { Archive("Story A"), Archive("Story B") };
        var r = StoryCollectionGrouping.Group(rows, Links(("Story A", "rec1"), ("Story B", "rec2")));

        Assert.Equal(0, r.CollectionCount);
        Assert.All(r.Entries, e => Assert.Equal(VolumeEntryKind.Archive, e.Kind));
        Assert.Equal(2, r.Entries.Count);
    }

    [Fact]
    public void WithoutAnyCollection_TheResultIsExactlyTheVolumeGrouping()
    {
        var rows = new List<GroupingRow>
        {
            Folder("Extras"), Archive("Series v01 c001"), Archive("Series v01 c002"), Archive("Series v02 c003"), Archive("Loose One"),
        };
        var plain = VolumeGrouping.Group(rows, null);
        foreach (var links in new[] { Links(), Links(("Loose One", "rec1")) })
        {
            var r = StoryCollectionGrouping.Group(rows, links);
            Assert.Equal(plain.StackCount, r.StackCount);
            Assert.Equal(0, r.CollectionCount);
            Assert.Equal(plain.Entries.Select(e => (e.Kind, e.Id, e.Rank, e.VolumeKey)), r.Entries.Select(e => (e.Kind, e.Id, e.Rank, e.VolumeKey)));
        }
    }

    [Fact]
    public void TwoRecords_TwoStacks_InFolderOrder_AndFoldersAreNeverCollected()
    {
        var rows = new List<GroupingRow>
        {
            Folder("A Folder"), Archive("Story 1"), Archive("Story 2"), Archive("Story 3"), Archive("Story 4"), Archive("Story 5"),
        };
        var links = Links(("A Folder", "rec9"), ("Story 4", "recB"), ("Story 2", "recA"), ("Story 5", "recB"), ("Story 3", "recA"));
        var r = StoryCollectionGrouping.Group(rows, links);

        Assert.Equal(2, r.CollectionCount);
        Assert.Equal(["id:A Folder", "id:Story 1", "cs:recA", "cs:recB"], r.Entries.Select(e => e.Id));
        Assert.Equal(["recA", "recB"], StoryCollectionGrouping.CollectionKeys(r.Entries));
        Assert.Equal(VolumeEntryKind.Folder, r.Entries[0].Kind);
    }

    [Fact]
    public void MembersFollowTheFolderOrder_NotTheLinkOrder()
    {
        var rows = new List<GroupingRow> { Archive("Story C"), Archive("Story A"), Archive("Story B") };
        var (collections, rest) = StoryCollectionGrouping.Split(rows, Links(("Story C", "r"), ("Story A", "r"), ("Story B", "r")));

        Assert.Empty(rest);
        var only = Assert.Single(collections);
        Assert.Equal(["id:Story A", "id:Story B", "id:Story C"], only.Members.Select(m => m.Id));
    }

    [Fact]
    public void TheRestStillGroupsByVolume_WithoutMissingVolumePlaceholders()
    {
        // The stories carry volume numbers of the collected volume; the other files state their own volumes 1 and 3: no placeholder
        // for volume 2 (a story collection is not a series), and the collected stories never join the volume stacks.
        var rows = new List<GroupingRow>
        {
            Archive("Other v01 c001"), Archive("Other v01 c002"), Archive("Other v03 c005"), Archive("Other v03 c006"),
            Archive("Tale v01 c001"), Archive("Tale v01 c002"),
        };
        var r = StoryCollectionGrouping.Group(rows, Links(("Tale v01 c001", "tank"), ("Tale v01 c002", "tank")));

        Assert.Equal(1, r.CollectionCount);
        Assert.Equal(2, r.StackCount);
        Assert.DoesNotContain(r.Entries, e => e.Kind == VolumeEntryKind.MissingVolume);
        var volumeMembers = r.Entries.Where(e => e.Kind == VolumeEntryKind.Stack).SelectMany(e => e.Stack!.Members).Select(m => m.Row.Id).ToList();
        Assert.DoesNotContain("id:Tale v01 c001", volumeMembers);
        Assert.Equal(VolumeEntryKind.CollectionStack, r.Entries[^1].Kind);
    }

    [Fact]
    public void AnEmptyRecordKey_IsNotALink()
    {
        var rows = new List<GroupingRow> { Archive("Story A"), Archive("Story B") };
        var r = StoryCollectionGrouping.Group(rows, Links(("Story A", ""), ("Story B", "")));

        Assert.Equal(0, r.CollectionCount);
        Assert.Equal(2, r.Entries.Count);
    }
}
