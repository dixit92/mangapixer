namespace com.lifepixer.mangapixer.Server.Persistence;

using Microsoft.EntityFrameworkCore;

/// <summary>
/// The tracker sync tables (1.43.0), configured from <see cref="MangaPixerDbContext"/>'s <c>OnModelCreating</c>. Kept in its own file
/// so the tracker and volume-scan lanes never edit the context's configuration list.
/// </summary>
internal static class TrackerModel
{
    public static void Configure(ModelBuilder mb)
    {
    }
}
