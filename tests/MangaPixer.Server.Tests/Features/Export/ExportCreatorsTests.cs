namespace com.lifepixer.mangapixer.Tests.Server.Features.Export;

using System.Text.Json;
using System.Text.Json.Nodes;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Features.Metadata.Authors;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>Creators in the metadata export (1.47.0): grouping per person, role text, order, other names - the pure part.</summary>
[Trait("Category", "Unit")]
public sealed class ExportCreatorsUnitTests
{
    private static readonly IReadOnlyDictionary<string, AuthorAliases> NoAuthors = new Dictionary<string, AuthorAliases>();

    private static IReadOnlyDictionary<string, AuthorAliases> Authors(params AuthorAliases[] rows) => rows.ToDictionary(a => a.AuthorId);

    [Fact]
    public void ACreditedPersonUnderBothRoles_IsOneEntry_WithBothRoles()
    {
        var json = "[{\"name\":\"Sample Writer\",\"role\":\"author\",\"providerId\":\"11\"},{\"name\":\"Sample Writer\",\"role\":\"artist\",\"providerId\":\"11\"}]";

        var creators = ExportCreators.Group("mangaupdates", json, NoAuthors)!;

        var only = Assert.Single(creators);
        Assert.Equal(("Sample Writer", "Author, Artist", 11L), (only.Name, only.Role, only.MuAuthorId));
        Assert.Empty(only.OtherNames);
    }

    [Fact]
    public void TheRoleTextIsFixed_WhateverTheOrderOfTheCredits()
    {
        var artistFirst = "[{\"name\":\"A\",\"role\":\"artist\",\"providerId\":\"1\"},{\"name\":\"A\",\"role\":\"author\",\"providerId\":\"1\"}]";
        var authorOnly = "[{\"name\":\"B\",\"role\":\"Author\",\"providerId\":\"2\"}]";
        var artistOnly = "[{\"name\":\"C\",\"role\":\" ARTIST \",\"providerId\":\"3\"}]";

        Assert.Equal("Author, Artist", ExportCreators.Group("mangaupdates", artistFirst, NoAuthors)!.Single().Role);
        Assert.Equal("Author", ExportCreators.Group("mangaupdates", authorOnly, NoAuthors)!.Single().Role);
        Assert.Equal("Artist", ExportCreators.Group("mangaupdates", artistOnly, NoAuthors)!.Single().Role);
    }

    [Fact]
    public void TheOrderIsEachPersonsFirstCredit_AndOtherCreditsAreDropped()
    {
        var json = "[{\"name\":\"Second Person\",\"role\":\"artist\",\"providerId\":\"2\"},{\"name\":\"Editor\",\"role\":\"other\",\"providerId\":\"9\"},"
            + "{\"name\":\"First Person\",\"role\":\"author\",\"providerId\":\"1\"},{\"name\":\"Second Person\",\"role\":\"author\",\"providerId\":\"2\"}]";

        var creators = ExportCreators.Group("mangaupdates", json, NoAuthors)!;

        Assert.Equal([("Second Person", "Author, Artist"), ("First Person", "Author")], creators.Select(c => (c.Name, c.Role)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    [InlineData("[{\"name\":\"Only Editor\",\"role\":\"other\",\"providerId\":\"5\"}]")]
    [InlineData("[{\"name\":\"  \",\"role\":\"author\"}]")]
    public void NoCreatorsLeft_IsNull_SoTheKeyIsLeftOut(string? json)
    {
        Assert.Null(ExportCreators.Group("mangaupdates", json, NoAuthors));
    }

    [Fact]
    public void TheSamePersonOfADifferentSpelling_StaysOne_WhenTheAuthorIdMatches_AndTwo_WhenOnlyTheNameMatches()
    {
        var sameId = "[{\"name\":\"Sample Writer\",\"role\":\"author\",\"providerId\":\"11\"},{\"name\":\"SAMPLE WRITER (alt)\",\"role\":\"artist\",\"providerId\":\"11\"}]";
        var sameNameOtherId = "[{\"name\":\"Sample Writer\",\"role\":\"author\",\"providerId\":\"11\"},{\"name\":\"Sample Writer\",\"role\":\"artist\",\"providerId\":\"12\"}]";

        var one = Assert.Single(ExportCreators.Group("mangaupdates", sameId, NoAuthors)!);
        Assert.Equal(("Sample Writer", "Author, Artist"), (one.Name, one.Role));
        Assert.Equal([11L, 12L], ExportCreators.Group("mangaupdates", sameNameOtherId, NoAuthors)!.Select(c => c.MuAuthorId!.Value));
    }

    [Fact]
    public void MangaDexAndGcdCredits_GroupByName_AndHaveNoMangaUpdatesAuthorId()
    {
        var json = "[{\"name\":\"Sample Writer\",\"role\":\"author\"},{\"name\":\"sample writer\",\"role\":\"artist\"},{\"name\":\"Other Hand\",\"role\":\"artist\"}]";
        var authors = Authors(new AuthorAliases("11", "Sample Writer", ["Alias"]));

        foreach (var provider in new[] { "mangadex", "gcd" })
        {
            var creators = ExportCreators.Group(provider, json, authors)!;
            Assert.Equal([("Sample Writer", "Author, Artist"), ("Other Hand", "Artist")], creators.Select(c => (c.Name, c.Role)));
            Assert.All(creators, c => Assert.Null(c.MuAuthorId));
            Assert.All(creators, c => Assert.Empty(c.OtherNames));
        }
        // A numeric provider id on a record of another provider is not a MangaUpdates author id.
        var numeric = ExportCreators.Group("mangadex", "[{\"name\":\"X\",\"role\":\"author\",\"providerId\":\"11\"}]", authors)!;
        Assert.Null(numeric.Single().MuAuthorId);
        Assert.Empty(ExportCreators.AuthorIdsOf("mangadex", "[{\"name\":\"X\",\"role\":\"author\",\"providerId\":\"11\"}]"));
    }

    [Fact]
    public void OtherNames_AreTheStoredRowsNames_MainNameFirstWhenItDiffers_DeduplicatedAndCapped()
    {
        var json = "[{\"name\":\"Sample Writer\",\"role\":\"author\",\"providerId\":\"11\"},{\"name\":\"Plain Name\",\"role\":\"artist\",\"providerId\":\"12\"},"
            + "{\"name\":\"No Row\",\"role\":\"artist\",\"providerId\":\"13\"}]";
        var many = Enumerable.Range(0, 80).Select(i => $"Alias {i}").ToList();
        var authors = Authors(
            new AuthorAliases("11", "SAMPLE Writer Main", ["Alias A", "alias a", "Sample Writer", "  ", "Alias B"]),
            new AuthorAliases("12", "plain name", many));

        var creators = ExportCreators.Group("mangaupdates", json, authors)!;

        Assert.Equal(["SAMPLE Writer Main", "Alias A", "Alias B"], creators[0].OtherNames);
        Assert.Equal(ExportCreators.MaxOtherNames, creators[1].OtherNames.Count);
        Assert.Equal("Alias 0", creators[1].OtherNames[0]); // the main name is the credited spelling (ignoring case): not repeated
        Assert.Empty(creators[2].OtherNames); // no stored row
        Assert.Equal(["11", "12", "13"], ExportCreators.AuthorIdsOf("mangaupdates", json));
    }

    [Fact]
    public void TheRecordFieldsMarker_ListsTheCreators()
    {
        Assert.Equal(["creators"], ExportCreators.RecordFields);
    }
}

/// <summary>Creators in the export over a migrated database (1.47.0): the stored form, the fingerprint, the other-names refresh.</summary>
[Trait("Category", "ServiceDb")]
public sealed class ExportCreatorsRebuildTests
{
    private static string Credits(params (string Name, string Role, string? Id)[] credits) =>
        JsonSerializer.Serialize(credits.Select(c => c.Id is null
            ? (object)new { name = c.Name, role = c.Role }
            : new { name = c.Name, role = c.Role, providerId = c.Id }));

    private static async Task<MetadataRecordEntity> RecordAsync(ExportTestKit kit, string id, string? creatorsJson)
    {
        var record = await kit.Db.AddRecordAsync(id, "Series " + id);
        record.CreatorsJson = creatorsJson;
        await kit.Db.Db.SaveChangesAsync();
        return record;
    }

    private static async Task AddAuthorAsync(ExportTestKit kit, string id, string name, params string[] otherNames)
    {
        kit.Db.Db.MetadataAuthors.Add(new MetadataAuthorEntity
        {
            Provider = "mangaupdates",
            ExternalId = id,
            Name = name,
            OtherNamesJson = otherNames.Length == 0 ? null : JsonSerializer.Serialize(otherNames),
            Status = (int)MetadataAuthorStatus.Ok,
            FetchedAt = kit.Clock.Now,
        });
        await kit.Db.Db.SaveChangesAsync();
    }

    [Fact]
    public async Task AnItemWithoutCreators_KeepsItsExactFormAndFingerprint_AndOnlyItemsWithCreatorsGainTheKey()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var plain = await kit.Db.AddFolderAsync(null, "Plain Series");
        var credited = await kit.Db.AddFolderAsync(null, "Credited Series");
        await kit.Db.AddLinkAsync(plain, await RecordAsync(kit, "1", null));
        await kit.Db.AddLinkAsync(credited, await RecordAsync(kit, "2", Credits(("Sample Writer", "author", "11"))));
        await kit.RebuildAsync();

        var rows = await kit.Db.Db.ExportItems.AsNoTracking().ToDictionaryAsync(i => i.NodePublicId);
        var plainJson = JsonNode.Parse(rows[plain.PublicId].Json)!.AsObject()["record"]!.AsObject();
        var creditedJson = JsonNode.Parse(rows[credited.PublicId].Json)!.AsObject()["record"]!.AsObject();
        Assert.False(plainJson.ContainsKey("creators")); // no key, not null, not []
        Assert.True(creditedJson.ContainsKey("creators"));
        // Reading the stored form and writing it again is byte for byte the same, and so is its fingerprint: the shape of an item
        // without creators did not change with this release.
        var item = ExportJson.ReadItem(rows[plain.PublicId].Json)!;
        Assert.Null(item.Record!.Creators);
        Assert.Equal(rows[plain.PublicId].Json, ExportJson.Serialize(item));
        Assert.Equal(rows[plain.PublicId].Fingerprint, ExportJson.Fingerprint(item));

        kit.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, (await kit.RebuildAsync()).Written);
        var items = await kit.ItemsAsync();
        Assert.Equal(ExportTestKit.Start, items[plain.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start, items[credited.PublicId].UpdatedAt);
    }

    [Fact]
    public async Task AnItemThatGainsCreatorsLater_MovesAlone()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var a = await kit.Db.AddFolderAsync(null, "Series A");
        var b = await kit.Db.AddFolderAsync(null, "Series B");
        var recordA = await RecordAsync(kit, "1", null);
        await kit.Db.AddLinkAsync(a, recordA);
        await kit.Db.AddLinkAsync(b, await RecordAsync(kit, "2", null));
        await kit.RebuildAsync();

        kit.Clock.Advance(TimeSpan.FromHours(1));
        await kit.Db.Db.MetadataRecords.Where(r => r.Id == recordA.Id)
            .ExecuteUpdateAsync(u => u.SetProperty(r => r.CreatorsJson, Credits(("Late Writer", "author", "7"), ("Late Writer", "artist", "7"))));
        Assert.Equal(1, (await kit.RebuildAsync()).Written);

        var items = await kit.ItemsAsync();
        Assert.Equal(ExportTestKit.Start.AddHours(1), items[a.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start, items[b.PublicId].UpdatedAt);
        var creator = Assert.Single(items[a.PublicId].Record!.Creators!);
        Assert.Equal(("Late Writer", "Author, Artist", 7L), (creator.Name, creator.Role, creator.MuAuthorId));
    }

    [Fact]
    public async Task AuthorRowsThatArriveAfterAnAdminRun_MoveExactlyTheItemsWhoseListChanges()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var one = await kit.Db.AddFolderAsync(null, "Series One");
        var two = await kit.Db.AddFolderAsync(null, "Series Two");
        var other = await kit.Db.AddFolderAsync(null, "Series Other");
        var none = await kit.Db.AddFolderAsync(null, "Series None");
        await kit.Db.AddLinkAsync(one, await RecordAsync(kit, "1", Credits(("Shared Writer", "author", "77"))));
        await kit.Db.AddLinkAsync(two, await RecordAsync(kit, "2", Credits(("Shared Writer", "artist", "77"), ("Solo Hand", "author", "88"))));
        await kit.Db.AddLinkAsync(other, await RecordAsync(kit, "3", Credits(("Solo Hand", "author", "88"))));
        await kit.Db.AddLinkAsync(none, await RecordAsync(kit, "4", null));
        await kit.RebuildAsync();
        var before = await kit.ItemsAsync();
        Assert.Empty(before[one.PublicId].Record!.Creators!.Single().OtherNames); // no author row yet: []

        // An admin runs "Artists' other names": the rows arrive. Author 77 gains names (series one and two move once); author 88's row
        // adds nothing to the credited name (series other stays); an author nobody credits changes nothing.
        kit.Clock.Advance(TimeSpan.FromHours(1));
        await AddAuthorAsync(kit, "77", "Shared Writer Main", "Alias One");
        await AddAuthorAsync(kit, "88", "Solo Hand");
        await AddAuthorAsync(kit, "99", "Nobody Credits Me", "Alias Zero");
        Assert.Equal(2, (await kit.RebuildAsync()).Written);
        var after = await kit.ItemsAsync();
        Assert.Equal(ExportTestKit.Start.AddHours(1), after[one.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start.AddHours(1), after[two.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start, after[other.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start, after[none.PublicId].UpdatedAt);
        Assert.Equal(["Shared Writer Main", "Alias One"], after[one.PublicId].Record!.Creators!.Single().OtherNames);
    }

    [Fact]
    public async Task ARefreshThatChangesOneAuthorsNames_MovesOnlyThatAuthorsItems()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var one = await kit.Db.AddFolderAsync(null, "Series One");
        var two = await kit.Db.AddFolderAsync(null, "Series Two");
        var other = await kit.Db.AddFolderAsync(null, "Series Other");
        await kit.Db.AddLinkAsync(one, await RecordAsync(kit, "1", Credits(("Shared Writer", "author", "77"))));
        await kit.Db.AddLinkAsync(two, await RecordAsync(kit, "2", Credits(("Shared Writer", "artist", "77"), ("Solo Hand", "author", "88"))));
        await kit.Db.AddLinkAsync(other, await RecordAsync(kit, "3", Credits(("Solo Hand", "author", "88"))));
        await AddAuthorAsync(kit, "77", "Shared Writer", "Alias One");
        await AddAuthorAsync(kit, "88", "Solo Hand", "Alias Two");
        await kit.RebuildAsync();
        var before = await kit.ItemsAsync();
        Assert.Equal(["Alias One"], before[one.PublicId].Record!.Creators!.Single().OtherNames);
        Assert.Equal(["Alias One"], before[two.PublicId].Record!.Creators!.Single(c => c.MuAuthorId == 77).OtherNames);

        // The refresh of author 77 stores a new name; author 88 is unchanged; an uncredited author appears.
        kit.Clock.Advance(TimeSpan.FromHours(1));
        await kit.Db.Db.MetadataAuthors.Where(a => a.ExternalId == "77")
            .ExecuteUpdateAsync(u => u.SetProperty(a => a.OtherNamesJson, "[\"Alias One\",\"Alias New\"]").SetProperty(a => a.FetchedAt, kit.Clock.Now));
        await AddAuthorAsync(kit, "99", "Nobody Credits Me", "Alias Zero");
        var result = await kit.RebuildAsync();

        Assert.Equal(2, result.Written);
        var after = await kit.ItemsAsync();
        Assert.Equal(ExportTestKit.Start.AddHours(1), after[one.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start.AddHours(1), after[two.PublicId].UpdatedAt);
        Assert.Equal(ExportTestKit.Start, after[other.PublicId].UpdatedAt);
        Assert.Equal(["Alias One", "Alias New"], after[one.PublicId].Record!.Creators!.Single().OtherNames);

        // A failed re-check keeps the names read before (status flips, names stay): nothing in the export changes.
        kit.Clock.Advance(TimeSpan.FromHours(1));
        await kit.Db.Db.MetadataAuthors.Where(a => a.ExternalId == "77").ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, (int)MetadataAuthorStatus.Failed));
        Assert.Equal(0, (await kit.RebuildAsync()).Written);
    }

    [Fact]
    public async Task ThePageSaysItExportsCreators_OutsideEveryItem()
    {
        await using var kit = await ExportTestKit.CreateAsync();
        var folder = await kit.Db.AddFolderAsync(null, "Series A");
        await kit.Db.AddLinkAsync(folder, await RecordAsync(kit, "1", Credits(("Sample Writer", "author", "11"))));
        var bare = await kit.Db.AddFolderAsync(null, "Series B");
        await kit.Db.AddLinkAsync(bare, await RecordAsync(kit, "2", null));

        var page = await kit.PageAsync();

        Assert.Equal(["creators"], page.GetProperty("recordFields").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(1, page.GetProperty("schemaVersion").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToDictionary(i => i.GetProperty("nodeId").GetString()!);
        Assert.True(items[folder.PublicId].GetProperty("record").TryGetProperty("creators", out _));
        Assert.False(items[bare.PublicId].GetProperty("record").TryGetProperty("creators", out _));
        // The marker is not stored with an item, so it cannot move a fingerprint.
        var stored = await kit.Db.Db.ExportItems.AsNoTracking().Select(i => i.Json).ToListAsync();
        Assert.All(stored, json => Assert.DoesNotContain("recordFields", json, StringComparison.Ordinal));
    }
}
