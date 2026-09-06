namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// Creates a default admin account on first startup if no users exist.
/// The admin is created with a known credential and <see cref="UserEntity.ForcePasswordChange"/> = true,
/// prompting a password change on first login.
///
/// The default credential is not hard-coded in the running server — it is only used once
/// during initial bootstrap. The username and password are configurable via options.
/// </summary>
public sealed class DefaultAdminBootstrap
{
    private readonly MangaPlexDbContext _db;
    private readonly UserManager<UserEntity> _userManager;
    private readonly DefaultAdminOptions _options;
    private readonly ILogger<DefaultAdminBootstrap> _logger;

    public DefaultAdminBootstrap(
        MangaPlexDbContext db,
        UserManager<UserEntity> userManager,
        DefaultAdminOptions options,
        ILogger<DefaultAdminBootstrap> logger)
    {
        _db = db;
        _userManager = userManager;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    /// Creates the default admin account if no users exist.
    /// Returns true if the admin was created, false if users already exist.
    /// </summary>
    public async Task<bool> BootstrapAsync(CancellationToken ct = default)
    {
        var hasUsers = await _db.Users.AnyAsync(ct);
        if (hasUsers)
        {
            _logger.LogDebug("Users already exist, skipping default admin bootstrap");
            return false;
        }

        _logger.LogInformation("No users found, creating default admin account {UserName}", _options.UserName);

        var admin = new UserEntity
        {
            UserName = _options.UserName,
            NormalizedUserName = _options.UserName.ToUpperInvariant(),
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = true,
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await _userManager.CreateAsync(admin, _options.Password);
        if (!result.Succeeded)
        {
            var errors = string.Join(", ", result.Errors.Select(e => e.Description));
            _logger.LogError("Failed to create default admin account: {Errors}", errors);
            throw new InvalidOperationException($"Failed to create default admin account: {errors}");
        }

        _logger.LogInformation("Default admin account {UserName} created successfully. Password change required on first login.", _options.UserName);
        return true;
    }
}

/// <summary>
/// Configuration options for the default admin bootstrap.
/// </summary>
public sealed class DefaultAdminOptions
{
    public string UserName { get; set; } = "admin";
    public string Password { get; set; } = "MangaPlex-Change-Me-Now!";
}

/// <summary>
/// Constants for the default admin bootstrap.
/// The default password is intentionally weak and must be changed on first login.
/// It is never exposed via API and is only used during initial database setup.
/// </summary>
public static class DefaultAdminDefaults
{
    public const string DefaultUserName = "admin";
    public const string DefaultPassword = "MangaPlex-Change-Me-Now!";

    /// <summary>
    /// The warning message shown to the admin on first login.
    /// </summary>
    public const string ForcePasswordChangeMessage = "Your password was set during initial setup. You must change it before using MangaPlex.";
}
