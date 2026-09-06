namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Authorization service for library access. Admins manage all libraries.
/// Readers need explicit <see cref="LibraryGrantEntity"/> for each library.
/// </summary>
public sealed class LibraryAuthorizationService
{
    private readonly MangaPlexDbContext _db;

    public LibraryAuthorizationService(MangaPlexDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Checks if a user can access a specific library.
    /// Admins can access all libraries. Readers need an explicit grant.
    /// </summary>
    public async Task<bool> CanAccessLibraryAsync(long userId, long libraryId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return false;

        if (user.IsAdmin)
            return true;

        return await _db.LibraryGrants
            .AnyAsync(g => g.UserId == userId && g.LibraryId == libraryId, ct);
    }

    /// <summary>
    /// Checks if a user can access a specific item (via its library).
    /// </summary>
    public async Task<bool> CanAccessItemAsync(long userId, long itemId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return false;

        if (user.IsAdmin)
            return true;

        // Find the library for this item via the catalog node
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.Id == itemId, ct);
        if (node is null)
            return false;

        return await _db.LibraryGrants
            .AnyAsync(g => g.UserId == userId && g.LibraryId == node.LibraryId, ct);
    }

    /// <summary>
    /// Gets the list of library IDs the user can access.
    /// </summary>
    public async Task<IReadOnlyList<long>> GetAccessibleLibraryIdsAsync(long userId, CancellationToken ct = default)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null || !user.IsActive)
            return [];

        if (user.IsAdmin)
            return await _db.Libraries.Select(l => l.Id).ToListAsync(ct);

        return await _db.LibraryGrants
            .Where(g => g.UserId == userId)
            .Select(g => g.LibraryId)
            .ToListAsync(ct);
    }

    /// <summary>
    /// Grants a user access to a library. Admin only.
    /// </summary>
    public async Task<bool> GrantAccessAsync(long adminUserId, long userId, long libraryId, CancellationToken ct = default)
    {
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Id == adminUserId, ct);
        if (admin is null || !admin.IsAdmin || !admin.IsActive)
            return false;

        // Check if grant already exists
        var existing = await _db.LibraryGrants
            .FirstOrDefaultAsync(g => g.UserId == userId && g.LibraryId == libraryId, ct);
        if (existing is not null)
            return true; // Already granted

        _db.LibraryGrants.Add(new LibraryGrantEntity
        {
            UserId = userId,
            LibraryId = libraryId,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>
    /// Revokes a user's access to a library. Admin only.
    /// Cannot revoke the last admin's access (but admins don't need grants, so this is a no-op for admins).
    /// </summary>
    public async Task<bool> RevokeAccessAsync(long adminUserId, long userId, long libraryId, CancellationToken ct = default)
    {
        var admin = await _db.Users.FirstOrDefaultAsync(u => u.Id == adminUserId, ct);
        if (admin is null || !admin.IsAdmin || !admin.IsActive)
            return false;

        var grant = await _db.LibraryGrants
            .FirstOrDefaultAsync(g => g.UserId == userId && g.LibraryId == libraryId, ct);
        if (grant is null)
            return true; // Already revoked

        _db.LibraryGrants.Remove(grant);
        await _db.SaveChangesAsync(ct);
        return true;
    }
}

/// <summary>
/// Authorization requirement for library access.
/// </summary>
public sealed class LibraryAccessRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement
{
    public long LibraryId { get; init; }
}

/// <summary>
/// Authorization requirement for admin role.
/// </summary>
public sealed class AdminRoleRequirement : Microsoft.AspNetCore.Authorization.IAuthorizationRequirement
{
}

/// <summary>
/// Authorization handler for library access and admin role.
/// </summary>
public sealed class MangaPlexAuthorizationHandler : Microsoft.AspNetCore.Authorization.AuthorizationHandler<LibraryAccessRequirement>
{
    private readonly LibraryAuthorizationService _authService;

    public MangaPlexAuthorizationHandler(LibraryAuthorizationService authService)
    {
        _authService = authService;
    }

    protected override async Task HandleRequirementAsync(
        Microsoft.AspNetCore.Authorization.AuthorizationHandlerContext context,
        LibraryAccessRequirement requirement)
    {
        var userIdClaim = context.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !long.TryParse(userIdClaim.Value, out var userId))
            return;

        if (await _authService.CanAccessLibraryAsync(userId, requirement.LibraryId))
        {
            context.Succeed(requirement);
        }
    }
}
