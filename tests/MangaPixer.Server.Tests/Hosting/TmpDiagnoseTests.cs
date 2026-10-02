namespace com.lifepixer.mangapixer.Tests.Server.Hosting;

using com.lifepixer.mangapixer.Server.Features.Catalog;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

public sealed class TmpDiagnoseTests
{
    private sealed class ProbeSink : ILogEventSink
    {
        public List<string> Lines { get; } = new();
        public void Emit(LogEvent e)
        {
            if (e.Level < LogEventLevel.Error) return;
            var ex = e.Exception;
            var chain = new List<string>();
            for (var x = ex; x is not null; x = x.InnerException)
                chain.Add(x.GetType().FullName + (x is Microsoft.Data.Sqlite.SqliteException s ? $"[code={s.SqliteErrorCode},ext={s.SqliteExtendedErrorCode}]" : ""));
            e.Properties.TryGetValue("SourceContext", out var sc);
            lock (Lines)
                Lines.Add($"scope={ExpectedRaceScope.IsActive} filterEnabled={new RecoveredRaceNoiseFilter().IsEnabled(e)} sc={sc?.GetType().Name}:{sc} ex={string.Join(">", chain)} msg={e.MessageTemplate.Text[..Math.Min(60, e.MessageTemplate.Text.Length)]}");
        }
    }

    [Fact]
    public async Task Diagnose()
    {
        var probe = new ProbeSink();
        await using var factory = new C00WebApplicationFactory(new CollectingSink(), probe);
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
            var lib = new LibraryEntity { PublicId = "dl", DisplayName = "d", RootPath = "/tmp/d", CreatedAt = DateTimeOffset.UtcNow };
            db.Libraries.Add(lib);
            db.Users.Add(new UserEntity { PublicId = "du", UserName = "u", NormalizedUserName = "U", IsActive = true, IsAdmin = true, PasswordHash = "h", SecurityStamp = "s", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            db.CatalogNodes.Add(new CatalogNodeEntity { PublicId = "dn", LibraryId = lib.Id, Kind = 1, DisplayName = "n", RelativePath = "n", PathKey = "n", SortKey = "1n", Availability = 0, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        long userId;
        using (var scope = factory.Services.CreateScope())
            userId = await scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>().Users.Select(u => u.Id).FirstAsync();
        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            using var scope = factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<FavoritesService>().AddFavoriteAsync(userId, "dn");
        })).ToArray();
        start.SetResult();
        await Task.WhenAll(tasks);
        Assert.Fail("PROBE " + probe.Lines.Count + ": " + string.Join(" ## ", probe.Lines.Take(6)));
    }
}
