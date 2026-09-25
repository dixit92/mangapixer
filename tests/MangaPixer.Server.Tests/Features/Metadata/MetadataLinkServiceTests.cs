namespace com.lifepixer.mangapixer.Tests.Server.Features.Metadata;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata;
using com.lifepixer.mangapixer.Server.Features.Admin;
using com.lifepixer.mangapixer.Server.Features.Metadata;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Service-with-DB tests for <c>MetadataLinkService</c> (1.24.0): link invariants
/// without network (existing records only), relink / Don't match / unlink with
/// orphan-record cleanup, folder/library precedence, purge scope, and the id-only
/// audit rows.
/// </summary>
public sealed class MetadataLinkServiceTests
{
    private static LinkSeriesRequest Req(string externalId, string provider = "mangaupdates") =>
        new() { Provider = provider, ExternalId = externalId };

    [Fact]
    public async Task Link_ToAMissingRecord_IsRecordNotFound_AndStoresNothing()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");

        var (code, _) = await t.Links().LinkAsync(folder.PublicId, Req("999"), "admin");

        Assert.Equal(MetadataLinkResultCode.RecordNotFound, code);
        Assert.False(await t.Db.NodeSeriesLinks.AnyAsync());
    }

    [Theory]
    [InlineData("MangaUpdates", "1")]
    [InlineData("mangaupdates", "")]
    [InlineData("mangaupdates", "../../etc")]
    [InlineData("bad provider", "1")]
    public async Task Link_WithInvalidIdentifiers_IsInvalid(string provider, string externalId)
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");

        var (code, _) = await t.Links().LinkAsync(folder.PublicId, Req(externalId, provider), "admin");

        Assert.Equal(MetadataLinkResultCode.InvalidRequest, code);
    }

    [Fact]
    public async Task Link_ThenRelink_DeletesTheOrphanedRecord_AndAuditsIdsOnly()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        await t.AddUserAsync("admin", isAdmin: true);
        var folder = await t.AddFolderAsync(null, "Folder");
        var first = await t.AddRecordAsync("1", "First");
        var second = await t.AddRecordAsync("2", "Second");

        var (code1, change1) = await t.Links().LinkAsync(folder.PublicId, Req("1"), "admin");
        var (code2, change2) = await t.Links().LinkAsync(folder.PublicId, Req("2"), "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, code1);
        Assert.Equal(MetadataLinkResultCode.Ok, code2);
        Assert.Null(change1!.Previous);
        Assert.Equal("1", change2!.Previous!.ExternalId);
        Assert.Equal("2", change2.Link!.ExternalId);
        Assert.Equal(SeriesLinkState.Confirmed, change2.Link.State);

        t.Db.ChangeTracker.Clear();
        Assert.False(await t.Db.MetadataRecords.AnyAsync(r => r.Id == first.Id));
        Assert.True(await t.Db.MetadataRecords.AnyAsync(r => r.Id == second.Id));
        Assert.Equal([first.Id], t.RemovedRecordIds);

        var audits = await t.Db.AuditEvents.OrderBy(a => a.Id).ToListAsync();
        Assert.Equal([AuditActions.MetadataLink, AuditActions.MetadataRelink], audits.Select(a => a.Action));
        Assert.All(audits, a =>
        {
            Assert.Equal(folder.Id, a.TargetItemId);
            Assert.Equal(folder.LibraryId, a.TargetLibraryId);
            Assert.NotNull(a.ActorUserId);
        });
    }

    [Fact]
    public async Task SharedRecord_SurvivesWhileAnotherLinkUsesIt()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var a = await t.AddFolderAsync(null, "A");
        var b = await t.AddFolderAsync(null, "B");
        var record = await t.AddRecordAsync("10", "Shared");
        await t.Links().LinkAsync(a.PublicId, Req("10"), "admin");
        await t.Links().LinkAsync(b.PublicId, Req("10"), "admin");

        await t.Links().RemoveAsync(a.PublicId, onlyDontMatch: false, "admin");
        t.Db.ChangeTracker.Clear();
        Assert.True(await t.Db.MetadataRecords.AnyAsync(r => r.Id == record.Id));

        await t.Links().RemoveAsync(b.PublicId, onlyDontMatch: false, "admin");
        t.Db.ChangeTracker.Clear();
        Assert.False(await t.Db.MetadataRecords.AnyAsync(r => r.Id == record.Id));
    }

    [Fact]
    public async Task DontMatch_ReplacesALink_KeepsTheRecordIdNull()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddRecordAsync("20", "Linked");
        await t.Links().LinkAsync(folder.PublicId, Req("20"), "admin");

        var (code, change) = await t.Links().SetDontMatchAsync(folder.PublicId, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(SeriesLinkState.DontMatch, change!.Link!.State);
        Assert.Equal(SeriesLinkState.Confirmed, change.Previous!.State);
        t.Db.ChangeTracker.Clear();
        var row = await t.Db.NodeSeriesLinks.SingleAsync();
        Assert.Null(row.RecordId);
        Assert.False(await t.Db.MetadataRecords.AnyAsync());
        Assert.Contains(await t.Db.AuditEvents.Select(a => a.Action).ToListAsync(), a => a == AuditActions.MetadataDontMatch);
    }

    [Fact]
    public async Task ClearDontMatch_LeavesAConfirmedLinkAlone()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        await t.AddRecordAsync("30", "Linked");
        await t.Links().LinkAsync(folder.PublicId, Req("30"), "admin");

        var (code, change) = await t.Links().RemoveAsync(folder.PublicId, onlyDontMatch: true, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Null(change!.Previous);
        Assert.True(await t.Db.NodeSeriesLinks.AnyAsync());
    }

    [Fact]
    public async Task UnknownNode_IsNodeNotFound()
    {
        await using var t = await MetadataTestDb.CreateAsync();

        Assert.Equal(MetadataLinkResultCode.NodeNotFound, (await t.Links().SetDontMatchAsync("nope", "admin")).Code);
        Assert.Equal(MetadataLinkResultCode.NodeNotFound, (await t.Links().RemoveAsync("nope", false, "admin")).Code);
        Assert.Equal(MetadataLinkResultCode.NodeNotFound, await t.Links().SetFolderPrecedenceAsync("nope", MetadataPrecedence.WebFirst, "admin"));
        Assert.Equal(MetadataLinkResultCode.LibraryNotFound, await t.Links().SetLibraryPrecedenceAsync("nope", null, "admin"));
    }

    [Fact]
    public async Task FolderPrecedence_OnAnArchive_IsNotAFolder_AndSetClearRoundTrips()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "Folder");
        var archive = await t.AddArchiveAsync(folder, "v1");

        Assert.Equal(MetadataLinkResultCode.NotAFolder, await t.Links().SetFolderPrecedenceAsync(archive.PublicId, MetadataPrecedence.ComicInfoFirst, "admin"));
        Assert.Equal(MetadataLinkResultCode.Ok, await t.Links().SetFolderPrecedenceAsync(folder.PublicId, MetadataPrecedence.ComicInfoFirst, "admin"));
        Assert.Equal(MetadataLinkResultCode.Ok, await t.Links().SetFolderPrecedenceAsync(folder.PublicId, MetadataPrecedence.WebFirst, "admin"));
        Assert.Equal((int)MetadataPrecedence.WebFirst, (await t.Db.FolderMetadataPrecedences.SingleAsync()).Precedence);
        Assert.Equal(MetadataLinkResultCode.Ok, await t.Links().ClearFolderPrecedenceAsync(folder.PublicId, "admin"));
        Assert.False(await t.Db.FolderMetadataPrecedences.AnyAsync());
        Assert.Equal(MetadataLinkResultCode.InvalidRequest, await t.Links().SetFolderPrecedenceAsync(folder.PublicId, (MetadataPrecedence)7, "admin"));
    }

    [Fact]
    public async Task LibraryPrecedence_SetsAndClears()
    {
        await using var t = await MetadataTestDb.CreateAsync();

        await t.Links().SetLibraryPrecedenceAsync(t.LibraryPublicId, MetadataPrecedence.ComicInfoFirst, "admin");
        t.Db.ChangeTracker.Clear();
        Assert.Equal((int)MetadataPrecedence.ComicInfoFirst, (await t.Db.Libraries.SingleAsync(l => l.Id == t.LibraryId)).MetadataPrecedence);

        await t.Links().SetLibraryPrecedenceAsync(t.LibraryPublicId, null, "admin");
        t.Db.ChangeTracker.Clear();
        Assert.Null((await t.Db.Libraries.SingleAsync(l => l.Id == t.LibraryId)).MetadataPrecedence);
    }

    [Fact]
    public async Task Purge_PerLibrary_KeepsDontMatch_AndOtherLibrariesRecords()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var other = await t.AddLibraryAsync("purge-other", "Other");
        var here = await t.AddFolderAsync(null, "Here");
        var dont = await t.AddFolderAsync(null, "Dont");
        var there = await t.AddFolderAsync(null, "There", other.Id);
        var shared = await t.AddRecordAsync("40", "Shared");
        var own = await t.AddRecordAsync("41", "Own");
        await t.AddLinkAsync(here, own);
        await t.AddLinkAsync(dont, null, SeriesLinkState.DontMatch);
        await t.AddLinkAsync(there, shared);
        var sibling = await t.AddFolderAsync(null, "Sibling");
        await t.AddLinkAsync(sibling, shared);

        var (code, result) = await t.Links().PurgeAsync(t.LibraryPublicId, "admin");

        Assert.Equal(MetadataLinkResultCode.Ok, code);
        Assert.Equal(2, result!.LinksRemoved);   // here + sibling
        Assert.Equal(1, result.RecordsRemoved);  // own; shared is still used in the other library
        t.Db.ChangeTracker.Clear();
        Assert.True(await t.Db.NodeSeriesLinks.AnyAsync(l => l.NodeId == dont.Id));
        Assert.True(await t.Db.NodeSeriesLinks.AnyAsync(l => l.NodeId == there.Id));
        Assert.True(await t.Db.MetadataRecords.AnyAsync(r => r.Id == shared.Id));
    }

    [Fact]
    public async Task Purge_Global_RemovesEveryRecord_IncludingUnlinkedPreviews()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "F");
        await t.AddLinkAsync(folder, await t.AddRecordAsync("50", "Linked"));
        await t.AddRecordAsync("51", "Previewed only");

        var (_, result) = await t.Links().PurgeAsync(null, "admin");

        Assert.Equal(1, result!.LinksRemoved);
        Assert.Equal(2, result.RecordsRemoved);
        Assert.Equal(2, t.RemovedRecordIds.Count);
        Assert.Contains(await t.Db.AuditEvents.Select(a => a.Action).ToListAsync(), a => a == AuditActions.MetadataPurge);
    }

    [Fact]
    public async Task Purge_UnknownLibrary_IsLibraryNotFound()
    {
        await using var t = await MetadataTestDb.CreateAsync();

        Assert.Equal(MetadataLinkResultCode.LibraryNotFound, (await t.Links().PurgeAsync("nope", "admin")).Code);
    }

    [Fact]
    public async Task Links_CascadeWithTheirNode()
    {
        await using var t = await MetadataTestDb.CreateAsync();
        var folder = await t.AddFolderAsync(null, "F");
        await t.AddLinkAsync(folder, null, SeriesLinkState.DontMatch);
        t.Db.FolderMetadataPrecedences.Add(new() { NodeId = folder.Id, Precedence = 1 });
        await t.Db.SaveChangesAsync();

        await t.Db.CatalogNodes.Where(n => n.Id == folder.Id).ExecuteDeleteAsync();

        Assert.False(await t.Db.NodeSeriesLinks.AnyAsync());
        Assert.False(await t.Db.FolderMetadataPrecedences.AnyAsync());
    }
}
