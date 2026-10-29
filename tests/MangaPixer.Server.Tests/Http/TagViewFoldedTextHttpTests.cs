namespace com.lifepixer.mangapixer.Tests.Server.Http;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Metadata.Facets;
using Xunit;

/// <summary>
/// 1.50.0 lane S: the tag view's free text compares folded, as the plain search compares names (<c>mp_fold</c>) - accents, case and
/// punctuation ignored on the work's title, its record's alternative titles and its locations' names; a leading <c>-</c> still leaves
/// out. Before 1.50.0 it was a plain <c>LIKE</c>: <c>pokemon</c> missed <c>Pokémon</c>, <c>re-zero</c> missed <c>Re:Zero</c>. Synthetic
/// names and well-known public titles only.
///
/// Seed - library "tfA": every work carries genre Mystery.
///   "Pokémon Adventures" @ "Pkmn Box"; "Pokemon Origins" @ "Origins"; "Pluto" (record, alternative title "Édition Kanzenban") @ "Pluto";
///   "Monster" @ "Re:Zero Box" (a location name with punctuation).
/// </summary>
[Trait("Category", "Http")]
public sealed class TagViewFoldedTextHttpTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private readonly MangaPixerWebApplicationFactory _factory;
    private readonly FacetQueryTestSupport _support;
    private static readonly SemaphoreSlim SeedLock = new(1, 1);
    private static readonly Dictionary<MangaPixerWebApplicationFactory, (HttpClient Admin, string Mystery)> Seeds = [];

    public TagViewFoldedTextHttpTests(MangaPixerWebApplicationFactory factory)
    {
        _factory = factory;
        _support = new FacetQueryTestSupport(factory);
    }

    private async Task<(HttpClient Admin, string Mystery)> SeedAsync()
    {
        await SeedLock.WaitAsync();
        try
        {
            if (Seeds.TryGetValue(_factory, out var existing))
                return existing;
            var admin = await _factory.LoginAsAdminWithChangedPasswordAsync();
            var s = _support;
            var lib = await s.LibraryAsync("tfA", "Folded Lib");
            var n1 = await s.NodeAsync(lib, null, "tf1", "Pkmn Box");
            var n2 = await s.NodeAsync(lib, null, "tf2", "Origins");
            var n3 = await s.NodeAsync(lib, null, "tf3", "Pluto");
            var n4 = await s.NodeAsync(lib, null, "tf4", "Re:Zero Box");
            var record = await s.RecordAsync("88001", "Pluto", "[\"\\u00c9dition Kanzenban\"]");
            var w1 = await s.WorkAsync("node:9201", "Pokémon Adventures");
            var w2 = await s.WorkAsync("node:9202", "Pokemon Origins");
            var w3 = await s.WorkAsync("rec:" + record, "Pluto", record);
            var w4 = await s.WorkAsync("node:9204", "Monster");
            var mystery = await s.ValueAsync(FacetBucket.Genres, "g:mystery", "Mystery");
            foreach (var (work, node) in new[] { (w1, n1), (w2, n2), (w3, n3), (w4, n4) })
                await s.EntryAsync(work, node, lib, mystery.Id);
            Seeds[_factory] = (admin, mystery.PublicId);
            return Seeds[_factory];
        }
        finally
        {
            SeedLock.Release();
        }
    }

    private async Task<string[]> TitlesAsync(string q)
    {
        var (admin, mystery) = await SeedAsync();
        var result = await FacetQueryTestSupport.GetAsync<SearchResultsDto>(admin,
            "/api/v1/search?f=" + Uri.EscapeDataString("genre:" + mystery) + "&q=" + Uri.EscapeDataString(q));
        return result.Works!.Select(w => w.Title).OrderBy(t => t, StringComparer.Ordinal).ToArray();
    }

    [Fact]
    public async Task FreeText_IgnoresAccentsAndCase_OnTitles() =>
        Assert.Equal(["Pokemon Origins", "Pokémon Adventures"], await TitlesAsync("pokemon"));

    [Fact]
    public async Task FreeText_WithAnAccent_FindsTheTitleWithout() =>
        Assert.Equal(["Pokemon Origins", "Pokémon Adventures"], await TitlesAsync("POKÉMON"));

    [Fact]
    public async Task FreeText_IgnoresAccents_OnAlternativeTitles()
    {
        Assert.Equal(["Pluto"], await TitlesAsync("edition"));
        Assert.Equal(["Pluto"], await TitlesAsync("édition kanzenban"));
    }

    [Fact]
    public async Task FreeText_IgnoresPunctuation_OnLocationNames() =>
        Assert.Equal(["Monster"], await TitlesAsync("re-zero"));

    [Fact]
    public async Task FreeText_ANegatedFoldedWord_LeavesOut() =>
        Assert.Equal(["Monster", "Pluto"], await TitlesAsync("-pokémon"));
}
