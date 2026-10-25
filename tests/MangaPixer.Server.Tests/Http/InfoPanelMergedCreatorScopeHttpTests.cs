namespace com.lifepixer.mangapixer.Tests.Server.Http;

using System.Text.Json;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// 1.48.0 lane A (owner decision 6a): a creator chip names the creator a merged creator was merged INTO only when the viewer may see a
/// work carrying that target. A target whose works are all in a library the viewer cannot see (or in their Private library under
/// Incognito) is neither named nor linked - not its name, not its value id, not its creator id: the chip stays the merged value the
/// viewer's own works carry, with that value's own creator (the admin creator routes resolve a merged creator to the live one). A viewer
/// who can see the target sees exactly what they saw before.
/// </summary>
public sealed class InfoPanelMergedCreatorScopeHttpTests
{
    private sealed record Chip(string Id, string Display, string? CreatorId);

    private static List<Chip> CreatorChips(string raw)
    {
        using var json = JsonDocument.Parse(raw);
        return json.RootElement.GetProperty("buckets").EnumerateArray()
            .Where(b => b.GetProperty("bucket").GetString() == "Creators")
            .SelectMany(b => b.GetProperty("chips").EnumerateArray())
            .Select(c => new Chip(c.GetProperty("id").GetString()!, c.GetProperty("display").GetString()!,
                c.TryGetProperty("creatorId", out var id) ? id.GetString() : null))
            .ToList();
    }

    [Fact]
    public async Task AMergeTarget_OnlyInLibrariesTheViewerCannotSee_IsNeverNamed_TheChipKeepsTheMergedValue()
    {
        await using var factory = MangaPixerWebApplicationFactory.WithoutBackgroundServices();
        var admin = await factory.LoginAsAdminWithChangedPasswordAsync();
        var s = new FacetQueryTestSupport(factory);

        var libG = await s.LibraryAsync("mcG", "Merge Granted");
        var libU = await s.LibraryAsync("mcU", "Merge Ungranted");
        var libP = await s.LibraryAsync("mcP", "Merge Private");
        var nodeG = await s.NodeAsync(libG, null, "mcGs", "Granted Story", folder: false);
        var nodeU = await s.NodeAsync(libU, null, "mcUs", "Ungranted Story", folder: false);
        var nodeP = await s.NodeAsync(libP, null, "mcPs", "Private Story", folder: false);
        var workG = await s.WorkAsync("mc-g", "Granted Story");
        var workU = await s.WorkAsync("mc-u", "Ungranted Story");
        var workP = await s.WorkAsync("mc-p", "Private Story");

        // "Ortwin Vale" carries only U's work; "O. Vale" (merged into it) carries G's. "Petra Lume" only P's; "P. Lume" (merged) G's.
        var ortwin = await s.CreatorAsync("Ortwin Vale", []);
        var oVale = await s.CreatorAsync("O. Vale", [], mergedInto: ortwin);
        var petra = await s.CreatorAsync("Petra Lume", []);
        var pLume = await s.CreatorAsync("P. Lume", [], mergedInto: petra);
        var ortwinValue = await s.ValueAsync(FacetBucket.Creators, "c:" + ortwin, "Ortwin Vale", ortwin);
        var oValeValue = await s.ValueAsync(FacetBucket.Creators, "c:" + oVale, "O. Vale", oVale);
        var petraValue = await s.ValueAsync(FacetBucket.Creators, "c:" + petra, "Petra Lume", petra);
        var pLumeValue = await s.ValueAsync(FacetBucket.Creators, "c:" + pLume, "P. Lume", pLume);
        await s.EntryAsync(workU, nodeU, libU, ortwinValue.Id, roles: FacetRoles.Writer);
        await s.EntryAsync(workG, nodeG, libG, oValeValue.Id, roles: FacetRoles.Writer);
        await s.EntryAsync(workP, nodeP, libP, petraValue.Id, roles: FacetRoles.Artist);
        await s.EntryAsync(workG, nodeG, libG, pLumeValue.Id, roles: FacetRoles.Artist);
        s.Bump();
        var creatorIds = await s.WithDbAsync(db => db.Creators.ToDictionaryAsync(c => c.Id, c => c.PublicId));

        var reader = await AccessTestUsers.ReaderAsync(factory, admin, "mcreader", [libG, libP], [libP]);

        // The admin sees every library: both chips name the live creators, as before.
        var asAdmin = CreatorChips(await FacetQueryTestSupport.GetRawAsync(admin, "/api/v1/libraries/mcG/info"));
        Assert.Equal(
            [new Chip(ortwinValue.PublicId, "Ortwin Vale", creatorIds[ortwin]), new Chip(petraValue.PublicId, "Petra Lume", creatorIds[petra])],
            asAdmin.OrderBy(c => c.Display, StringComparer.Ordinal).ToList());

        // The reader cannot see U: "O. Vale" stays itself; P is theirs, so "Petra Lume" is named.
        var raw = await FacetQueryTestSupport.GetRawAsync(reader.Client, "/api/v1/libraries/mcG/info");
        Assert.Equal(
            [new Chip(oValeValue.PublicId, "O. Vale", creatorIds[oVale]), new Chip(petraValue.PublicId, "Petra Lume", creatorIds[petra])],
            CreatorChips(raw).OrderBy(c => c.Display, StringComparer.Ordinal).ToList());
        foreach (var hidden in new[] { "Ortwin", ortwinValue.PublicId, creatorIds[ortwin] })
            Assert.DoesNotContain(hidden, raw, StringComparison.Ordinal);

        // Under Incognito P is hidden as well: "P. Lume" stays itself too.
        var rawIncognito = await FacetQueryTestSupport.GetRawAsync(reader.Client, "/api/v1/libraries/mcG/info", incognito: true);
        Assert.Equal(
            [new Chip(oValeValue.PublicId, "O. Vale", creatorIds[oVale]), new Chip(pLumeValue.PublicId, "P. Lume", creatorIds[pLume])],
            CreatorChips(rawIncognito).OrderBy(c => c.Display, StringComparer.Ordinal).ToList());
        foreach (var hidden in new[] { "Ortwin", "Petra", petraValue.PublicId, creatorIds[petra] })
            Assert.DoesNotContain(hidden, rawIncognito, StringComparison.Ordinal);

        // The node's own panel and its bucket page follow the same rule.
        var node = await FacetQueryTestSupport.GetRawAsync(reader.Client, "/api/v1/nodes/mcGs/info");
        var bucket = await FacetQueryTestSupport.GetRawAsync(reader.Client, "/api/v1/nodes/mcGs/info/buckets/creator");
        Assert.DoesNotContain("Ortwin", node, StringComparison.Ordinal);
        Assert.DoesNotContain("Ortwin", bucket, StringComparison.Ordinal);
        Assert.Contains("O. Vale", node, StringComparison.Ordinal);
    }
}
