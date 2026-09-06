namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

/// <summary>
/// Custom ASP.NET Core Identity user store backed by <see cref="MangaPlexDbContext"/>.
/// Implements the core interfaces needed for cookie auth: IUserStore, IUserPasswordStore,
/// IUserSecurityStampStore, IUserLockoutStore, IUserClaimStore, IUserRoleStore.
/// </summary>
public sealed class MangaPlexUserStore :
    IUserStore<UserEntity>,
    IUserPasswordStore<UserEntity>,
    IUserSecurityStampStore<UserEntity>,
    IUserLockoutStore<UserEntity>,
    IUserClaimStore<UserEntity>,
    IUserRoleStore<UserEntity>,
    IDisposable
{
    private readonly MangaPlexDbContext _db;
    private bool _disposed;

    public MangaPlexUserStore(MangaPlexDbContext db, IdentityErrorDescriber? errorDescriber = null)
    {
        _db = db;
        ErrorDescriber = errorDescriber ?? new IdentityErrorDescriber();
    }

    public IdentityErrorDescriber ErrorDescriber { get; set; }

    public IQueryable<UserEntity> Users => _db.Users;

    public async Task<IdentityResult> CreateAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (user is null) throw new ArgumentNullException(nameof(user));

        user.PublicId ??= OpaqueId.Encode(user.Id > 0 ? user.Id : Random.Shared.NextInt64(1, long.MaxValue));
        if (string.IsNullOrEmpty(user.SecurityStamp))
            user.SecurityStamp = Guid.NewGuid().ToString("N");
        user.CreatedAt = DateTimeOffset.UtcNow;

        _db.Users.Add(user);
        await _db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> UpdateAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (user is null) throw new ArgumentNullException(nameof(user));

        _db.Users.Update(user);
        await _db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    public async Task<IdentityResult> DeleteAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (user is null) throw new ArgumentNullException(nameof(user));

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        return IdentityResult.Success;
    }

    public Task<UserEntity?> FindByIdAsync(string userId, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (long.TryParse(userId, out var id))
            return _db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        return Task.FromResult<UserEntity?>(null);
    }

    public Task<UserEntity?> FindByNameAsync(string normalizedUserName, CancellationToken ct)
    {
        ThrowIfDisposed();
        return _db.Users.FirstOrDefaultAsync(u => u.NormalizedUserName == normalizedUserName, ct);
    }

    public Task<string> GetUserIdAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public Task<string?> GetUserNameAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult<string?>(user.UserName);
    }

    public Task SetUserNameAsync(UserEntity user, string? userName, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.UserName = userName ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<string?> GetNormalizedUserNameAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult<string?>(user.NormalizedUserName);
    }

    public Task SetNormalizedUserNameAsync(UserEntity user, string? normalizedName, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.NormalizedUserName = normalizedName ?? string.Empty;
        return Task.CompletedTask;
    }

    // IUserPasswordStore
    public Task<string?> GetPasswordHashAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult<string?>(user.PasswordHash);
    }

    public Task SetPasswordHashAsync(UserEntity user, string? passwordHash, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.PasswordHash = passwordHash ?? string.Empty;
        return Task.CompletedTask;
    }

    public Task<bool> HasPasswordAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(!string.IsNullOrEmpty(user.PasswordHash));
    }

    // IUserSecurityStampStore
    public Task<string?> GetSecurityStampAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult<string?>(user.SecurityStamp);
    }

    public Task SetSecurityStampAsync(UserEntity user, string? stamp, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.SecurityStamp = stamp ?? Guid.NewGuid().ToString("N");
        return Task.CompletedTask;
    }

    // IUserLockoutStore
    public Task<int> GetAccessFailedCountAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task<bool> GetLockoutEnabledAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(user.LockoutEnabled);
    }

    public Task SetLockoutEnabledAsync(UserEntity user, bool enabled, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.LockoutEnabled = enabled;
        return Task.CompletedTask;
    }

    public Task<DateTimeOffset?> GetLockoutEndDateAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(user.LockoutEnd);
    }

    public Task SetLockoutEndDateAsync(UserEntity user, DateTimeOffset? lockoutEnd, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.LockoutEnd = lockoutEnd;
        return Task.CompletedTask;
    }

    public Task<int> IncrementAccessFailedCountAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.AccessFailedCount++;
        return Task.FromResult(user.AccessFailedCount);
    }

    public Task ResetAccessFailedCountAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        user.AccessFailedCount = 0;
        return Task.CompletedTask;
    }

    // IUserClaimStore — MangaPlex uses simple claims (admin role, user ID)
    public async Task<IList<Claim>> GetClaimsAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new(ClaimTypes.Name, user.UserName),
        };
        if (user.IsAdmin)
            claims.Add(new Claim(ClaimTypes.Role, "admin"));
        else
            claims.Add(new Claim(ClaimTypes.Role, "reader"));

        return await Task.FromResult(claims);
    }

    public Task AddClaimsAsync(UserEntity user, IEnumerable<Claim> claims, CancellationToken ct)
    {
        // Claims are derived from entity state, not stored separately
        return Task.CompletedTask;
    }

    public Task ReplaceClaimAsync(UserEntity user, Claim claim, Claim newClaim, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task RemoveClaimsAsync(UserEntity user, IEnumerable<Claim> claims, CancellationToken ct)
    {
        return Task.CompletedTask;
    }

    public Task<IList<UserEntity>> GetUsersForClaimAsync(Claim claim, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (claim.Type == ClaimTypes.Role && claim.Value == "admin")
            return Task.FromResult<IList<UserEntity>>(_db.Users.Where(u => u.IsAdmin).ToList());
        return Task.FromResult<IList<UserEntity>>(new List<UserEntity>());
    }

    // IUserRoleStore — roles are stored as boolean flags on UserEntity
    public Task AddToRoleAsync(UserEntity user, string roleName, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (roleName == "admin") user.IsAdmin = true;
        return Task.CompletedTask;
    }

    public Task RemoveFromRoleAsync(UserEntity user, string roleName, CancellationToken ct)
    {
        ThrowIfDisposed();
        if (roleName == "admin") user.IsAdmin = false;
        return Task.CompletedTask;
    }

    public Task<IList<string>> GetRolesAsync(UserEntity user, CancellationToken ct)
    {
        ThrowIfDisposed();
        var roles = new List<string> { user.IsAdmin ? "admin" : "reader" };
        return Task.FromResult<IList<string>>(roles);
    }

    public Task<bool> IsInRoleAsync(UserEntity user, string roleName, CancellationToken ct)
    {
        ThrowIfDisposed();
        return Task.FromResult(roleName switch
        {
            "admin" => user.IsAdmin,
            "reader" => !user.IsAdmin,
            _ => false,
        });
    }

    public Task<IList<UserEntity>> GetUsersInRoleAsync(string roleName, CancellationToken ct)
    {
        ThrowIfDisposed();
        var users = roleName == "admin"
            ? _db.Users.Where(u => u.IsAdmin).ToList()
            : _db.Users.Where(u => !u.IsAdmin).ToList();
        return Task.FromResult<IList<UserEntity>>(users);
    }

    public void Dispose()
    {
        _disposed = false; // DbContext is owned by DI, don't dispose it here
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}
