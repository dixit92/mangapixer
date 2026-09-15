namespace com.lifepixer.mangaplex.Server.Persistence;

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

/// <summary>
/// Design-time factory so EF Core tooling (<c>dotnet ef migrations add</c>) can
/// construct the context without booting the application host (which would try to
/// open the real database, start the worker pool, etc.). The database path is
/// irrelevant when scaffolding a migration — no database is opened — so a
/// placeholder path is used purely to satisfy the SQLite provider.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MangaPlexDbContext>
{
    public MangaPlexDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .ConfigureSqlite("mangaplex-design-time.db")
            .Options;
        return new MangaPlexDbContext(options);
    }
}
