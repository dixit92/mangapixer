namespace com.lifepixer.mangapixer.Server.Features.Auth;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

/// <summary>
/// First-run setup. The server ships <b>no default credential</b> (audit finding
/// F2): on a fresh instance no user is created at startup. The first admin is
/// created only by an explicit, user-driven call to
/// <see cref="CreateFirstAdminAsync"/> (via <c>POST /api/v1/auth/setup</c>),
/// which succeeds exactly once — while no user exists.
/// </summary>
public sealed class FirstRunSetupService
{
    /// <summary>
    /// Minimum admin password length, matching the Identity password policy.
    /// </summary>
    public const int MinPasswordLength = 8;

    /// <summary>
    /// Message shown when a forced password change is pending. Retained for
    /// admin-initiated resets; it is NOT used for the first admin, which is
    /// created with the user's own password and no forced change.
    /// </summary>
    public const string ForcePasswordChangeMessage =
        "Your password was set during initial setup. You must change it before using MangaPixer.";

    private readonly MangaPixerDbContext _db;
    private readonly UserManager<UserEntity> _userManager;
    private readonly ILogger<FirstRunSetupService>? _logger;

    public FirstRunSetupService(
        MangaPixerDbContext db,
        UserManager<UserEntity> userManager,
        ILogger<FirstRunSetupService>? logger = null)
    {
        _db = db;
        _userManager = userManager;
        _logger = logger;
    }

    /// <summary>True when the instance has no users and setup must be run.</summary>
    public async Task<bool> IsSetupRequiredAsync(CancellationToken ct = default) =>
        !await _db.Users.AnyAsync(ct);

    /// <summary>
    /// Creates the first admin account. Succeeds only while no user exists, so it
    /// cannot be reused to create a second admin or reset credentials.
    /// </summary>
    public async Task<FirstAdminResult> CreateFirstAdminAsync(
        string username, string password, CancellationToken ct = default)
    {
        if (await _db.Users.AnyAsync(ct))
        {
            _logger?.LogDebug(LogEvents.Auth.SetupAlreadyInitialized, "First-admin creation rejected: instance already initialized");
            return FirstAdminResult.AlreadyInitialized();
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            _logger?.LogDebug(LogEvents.Auth.SetupUsernameMissing, "First-admin creation rejected: username empty");
            return FirstAdminResult.Invalid("Username is required.");
        }

        if (string.IsNullOrEmpty(password) || password.Length < MinPasswordLength)
        {
            _logger?.LogDebug(LogEvents.Auth.SetupPasswordTooShort, "First-admin creation rejected: password too short ({Length} < {Min})",
                password?.Length ?? 0, MinPasswordLength);
            return FirstAdminResult.Invalid($"Password must be at least {MinPasswordLength} characters.");
        }

        var admin = new UserEntity
        {
            UserName = username,
            NormalizedUserName = username.ToUpperInvariant(),
            IsActive = true,
            IsAdmin = true,
            ForcePasswordChange = false,
            LockoutEnabled = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await _userManager.CreateAsync(admin, password);
        if (!result.Succeeded)
        {
            var errors = string.Join("; ", result.Errors.Select(e => e.Description));
            _logger?.LogWarning(LogEvents.Auth.SetupCreationRejected, "First-admin creation rejected: {Errors}", errors);
            return FirstAdminResult.Invalid(errors);
        }

        _logger?.LogInformation(LogEvents.Auth.FirstAdminCreated, "First admin account {UserName} created via first-run setup", username);
        return FirstAdminResult.Success(admin);
    }
}

/// <summary>Outcome of a first-admin creation attempt.</summary>
public sealed record FirstAdminResult
{
    public required FirstAdminStatus Status { get; init; }
    public UserEntity? User { get; init; }
    public string? Error { get; init; }

    public static FirstAdminResult Success(UserEntity user) =>
        new() { Status = FirstAdminStatus.Created, User = user };

    public static FirstAdminResult AlreadyInitialized() =>
        new() { Status = FirstAdminStatus.AlreadyInitialized, Error = "Setup has already been completed." };

    public static FirstAdminResult Invalid(string error) =>
        new() { Status = FirstAdminStatus.Invalid, Error = error };
}

public enum FirstAdminStatus
{
    Created,
    AlreadyInitialized,
    Invalid,
}
