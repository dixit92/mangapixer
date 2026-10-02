namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using com.lifepixer.mangapixer.Server.Features.Reading;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Tests.Server.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// The first-write race on <c>reading_progress</c> / <c>read_marks</c> through the REAL host: the real DI services,
/// the real SQLite file and the host's real Serilog pipeline (its filter included). The assertion reads the rolling
/// log file the host writes AFTER its filter, because a capture sink wrapped around the host's logger would see the
/// events before the filter ran and prove nothing about it.
/// </summary>
public sealed class RecoveredRaceLoggingTests : IClassFixture<MangaPixerWebApplicationFactory>
{
    private const int Writers = 8;
    private readonly MangaPixerWebApplicationFactory _factory;

    public RecoveredRaceLoggingTests(MangaPixerWebApplicationFactory factory) => _factory = factory;

    [Fact]
    public async Task ConcurrentFirstProgressWrites_RecoverWithoutAnErrorLine()
    {
        var userId = await SeedUserAsync();
        var items = await SeedItemsAsync("race-mid", 6);

        // A page in the middle: only the reading_progress insert races.
        foreach (var itemId in items)
            await RaceProgressAsync(userId, itemId, pageIndex: _ => 3);

        AssertNoErrorLines();
        await AssertOneProgressRowEachAsync(userId, items);
    }

    [Fact]
    public async Task ConcurrentFirstCompletions_RecoverWithoutAnErrorLine()
    {
        var userId = await SeedUserAsync();
        var items = await SeedItemsAsync("race-end", 6);

        // The last page completes the item: the progress row AND the sticky read mark are inserted by every
        // writer, and the unique-index message may name either table.
        foreach (var itemId in items)
            await RaceProgressAsync(userId, itemId, pageIndex: _ => 9);

        AssertNoErrorLines();
        await AssertOneProgressRowEachAsync(userId, items);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        foreach (var itemId in items)
            Assert.Equal(1, await db.ReadMarks.CountAsync(m => m.UserId == userId && m.ItemId == itemId));
    }

    private async Task RaceProgressAsync(long userId, long itemId, Func<int, int> pageIndex)
    {
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, Writers).Select(i => Task.Run(async () =>
        {
            await start.Task;
            await using var scope = _factory.Services.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<ReadingStateService>();
            return await service.UpdateProgressAsync(userId, itemId, pageIndex(i),
                expectedContentVersion: 1, mutationId: $"mut-{itemId}-{i}");
        })).ToArray();
        start.SetResult();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, r => Assert.Equal(UpdateStatus.Success, r.Status));
    }

    private async Task AssertOneProgressRowEachAsync(long userId, IEnumerable<long> items)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        foreach (var itemId in items)
            Assert.Equal(1, await db.ReadingProgress.CountAsync(p => p.UserId == userId && p.ItemId == itemId));
    }

    /// <summary>No ERROR line in what the host's pipeline wrote to its log file (after the filter).</summary>
    private void AssertNoErrorLines()
    {
        var errors = HostLogLines().Where(l => l.Contains("[ERROR]", StringComparison.Ordinal)).ToList();
        Assert.True(errors.Count == 0, "The host logged " + errors.Count + " error line(s): "
            + string.Join(" || ", errors.Select(l => l.Length > 300 ? l[..300] : l)));
    }

    private IEnumerable<string> HostLogLines()
    {
        var logs = Path.Combine(_factory.DataRoot, "logs");
        foreach (var file in Directory.EnumerateFiles(logs, "mangapixer-*.log"))
        {
            // The sink keeps the file open for appending; read it shared.
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
                yield return line;
        }
    }

    private async Task<long> SeedUserAsync()
    {
        using var client = await _factory.LoginAsAdminAsync();
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        return await db.Users.Select(u => u.Id).FirstAsync();
    }

    private async Task<List<long>> SeedItemsAsync(string prefix, int count)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
        var library = await db.Libraries.FirstOrDefaultAsync(l => l.PublicId == "racelib");
        if (library is null)
        {
            library = new LibraryEntity
            {
                PublicId = "racelib",
                DisplayName = "Race library",
                RootPath = "/tmp/race-test",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Libraries.Add(library);
            await db.SaveChangesAsync();
        }

        var ids = new List<long>();
        for (var i = 0; i < count; i++)
        {
            var name = $"{prefix}-{i}.cbz";
            var node = new CatalogNodeEntity
            {
                PublicId = $"{prefix}{i}",
                LibraryId = library.Id,
                Kind = 1,
                DisplayName = name,
                RelativePath = name,
                PathKey = name,
                SortKey = "1" + name,
                Availability = 0,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.CatalogNodes.Add(node);
            await db.SaveChangesAsync();
            db.ArchiveItems.Add(new ArchiveItemEntity
            {
                NodeId = node.Id,
                ArchiveFormat = 0,
                ByteLength = 100,
                ModificationTicks = 0,
                ContentVersion = 1,
                AnalysisState = 0,
                PageCount = 10,
            });
            await db.SaveChangesAsync();
            ids.Add(node.Id);
        }

        return ids;
    }
}
