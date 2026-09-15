namespace com.lifepixer.mangapixer.Server.Scanning;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Manages per-library maintenance state during scans.
/// When a library is in maintenance, authorized clients see a specific state
/// and Retry-After header. Other libraries remain available.
/// </summary>
public sealed class LibraryMaintenanceService
{
    private readonly MangaPixerDbContext _db;

    public LibraryMaintenanceService(MangaPixerDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Enters maintenance mode for a library.
    /// </summary>
    public async Task EnterMaintenanceAsync(long libraryId, CancellationToken ct = default)
    {
        var library = await _db.Libraries.FirstAsync(l => l.Id == libraryId, ct);
        library.State = "maintenance";
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Exits maintenance mode for a library.
    /// </summary>
    public async Task ExitMaintenanceAsync(long libraryId, CancellationToken ct = default)
    {
        var library = await _db.Libraries.FirstAsync(l => l.Id == libraryId, ct);
        library.State = "active";
        await _db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Returns true if a library is in maintenance mode.
    /// </summary>
    public async Task<bool> IsInMaintenanceAsync(long libraryId, CancellationToken ct = default)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.Id == libraryId, ct);
        return library?.State == "maintenance";
    }

    /// <summary>
    /// Returns the Retry-After duration for a library in maintenance.
    /// </summary>
    public TimeSpan GetRetryAfter()
    {
        return TimeSpan.FromSeconds(30);
    }
}
