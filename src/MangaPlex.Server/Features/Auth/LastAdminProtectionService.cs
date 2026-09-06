namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Protects against accidental lockout by preventing the last admin from being
/// disabled, deleted, or demoted. This is a critical safety invariant.
/// </summary>
public sealed class LastAdminProtectionService
{
    private readonly MangaPlexDbContext _db;

    public LastAdminProtectionService(MangaPlexDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the count of active admin users.
    /// </summary>
    public async Task<int> GetActiveAdminCountAsync(CancellationToken ct = default)
    {
        return await _db.Users.CountAsync(u => u.IsAdmin && u.IsActive, ct);
    }

    /// <summary>
    /// Checks if disabling a user would remove the last active admin.
    /// </summary>
    public async Task<bool> CanDisableUserAsync(long userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return true;
        if (!user.IsAdmin || !user.IsActive) return true;

        // Would this leave zero active admins?
        var activeAdminCount = await GetActiveAdminCountAsync(ct);
        return activeAdminCount > 1;
    }

    /// <summary>
    /// Checks if deleting a user would remove the last active admin.
    /// </summary>
    public async Task<bool> CanDeleteUserAsync(long userId, CancellationToken ct = default)
    {
        return await CanDisableUserAsync(userId, ct);
    }

    /// <summary>
    /// Checks if removing admin role from a user would leave no admins.
    /// </summary>
    public async Task<bool> CanRemoveAdminRoleAsync(long userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsAdmin) return true;

        var activeAdminCount = await GetActiveAdminCountAsync(ct);
        return activeAdminCount > 1;
    }

    /// <summary>
    /// Checks if a user is the last active admin.
    /// </summary>
    public async Task<bool> IsLastAdminAsync(long userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsAdmin || !user.IsActive) return false;

        return await GetActiveAdminCountAsync(ct) == 1;
    }
}
