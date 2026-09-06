namespace com.lifepixer.mangaplex.Server.Features.Auth;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;

/// <summary>
/// Authentication endpoints: login, logout, current user, password change, CSRF token.
/// Uses cookie authentication with XSRF protection.
/// </summary>
[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly SignInManager<UserEntity> _signInManager;
    private readonly UserManager<UserEntity> _userManager;
    private readonly SessionService _sessionService;
    private readonly LoginRateLimiter _rateLimiter;
    private readonly LastAdminProtectionService _lastAdminProtection;
    private readonly MangaPlexDbContext _db;
    private readonly ILogger<AuthController> _logger;

    public AuthController(
        SignInManager<UserEntity> signInManager,
        UserManager<UserEntity> userManager,
        SessionService sessionService,
        LoginRateLimiter rateLimiter,
        LastAdminProtectionService lastAdminProtection,
        MangaPlexDbContext db,
        ILogger<AuthController> logger)
    {
        _signInManager = signInManager;
        _userManager = userManager;
        _sessionService = sessionService;
        _rateLimiter = rateLimiter;
        _lastAdminProtection = lastAdminProtection;
        _db = db;
        _logger = logger;
    }

    [HttpGet("csrf")]
    public IActionResult GetCsrfToken()
    {
        var token = GenerateCsrfToken();
        Response.Cookies.Append(".MangaPlex.Csrf", token, new CookieOptions
        {
            HttpOnly = false, // JavaScript needs to read this to send it in headers
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddHours(1),
        });
        return Ok(new CsrfTokenDto { Token = token });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(request.Username) || string.IsNullOrEmpty(request.Password))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Username and password are required." });

        var ipAddress = GetClientIpAddress();

        // Rate limit check
        if (!_rateLimiter.AllowAttempt(ipAddress, request.Username))
        {
            var retryAfter = _rateLimiter.GetRetryAfter(ipAddress, request.Username);
            Response.Headers["Retry-After"] = ((int?)retryAfter?.TotalSeconds ?? 60).ToString();
            return StatusCode(429, new ApiError { Error = "rate_limited", Message = "Too many login attempts. Please try again later." });
        }

        var user = await _userManager.FindByNameAsync(request.Username);
        if (user is null)
        {
            _rateLimiter.RecordFailure(ipAddress, request.Username);
            _logger.LogInformation("Login failed for unknown user {UserName}", request.Username);
            return Unauthorized(new ApiError { Error = "invalid_credentials", Message = "Invalid username or password." });
        }

        if (!user.IsActive)
        {
            _rateLimiter.RecordFailure(ipAddress, request.Username);
            _logger.LogInformation("Login failed for disabled user {UserName}", request.Username);
            return Unauthorized(new ApiError { Error = "account_disabled", Message = "This account has been disabled." });
        }

        var result = await _signInManager.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            _rateLimiter.RecordFailure(ipAddress, request.Username);
            if (result.IsLockedOut)
                return StatusCode(429, new ApiError { Error = "locked_out", Message = "Account is temporarily locked due to too many failed attempts." });
            return Unauthorized(new ApiError { Error = "invalid_credentials", Message = "Invalid username or password." });
        }

        // Success
        _rateLimiter.RecordSuccess(ipAddress, request.Username);
        user.LastLoginAt = DateTimeOffset.UtcNow;
        await _userManager.UpdateAsync(user);

        // Create session
        var session = await _sessionService.CreateSessionAsync(user, ct);

        // Set auth cookie
        Response.Cookies.Append(".MangaPlex.Auth", session.TicketId, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = session.ExpiresAt,
        });

        _logger.LogInformation("User {UserName} logged in successfully", request.Username);

        return Ok(new AuthUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            Role = user.IsAdmin ? "admin" : "reader",
            IsAdmin = user.IsAdmin,
        });
    }

    [HttpPost("logout")]
    [Authorize]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        var ticketId = Request.Cookies[".MangaPlex.Auth"];
        if (!string.IsNullOrEmpty(ticketId))
        {
            await _sessionService.RevokeSessionAsync(ticketId, ct);
            Response.Cookies.Delete(".MangaPlex.Auth");
        }
        return NoContent();
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<IActionResult> GetCurrentUser(CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null)
            return Unauthorized(new ApiError { Error = "not_authenticated", Message = "Not authenticated." });

        return Ok(new AuthUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            Role = user.IsAdmin ? "admin" : "reader",
            IsAdmin = user.IsAdmin,
        });
    }

    [HttpPost("change-password")]
    [Authorize]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null)
            return Unauthorized(new ApiError { Error = "not_authenticated", Message = "Not authenticated." });

        if (string.IsNullOrEmpty(request.CurrentPassword) || string.IsNullOrEmpty(request.NewPassword))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Current and new passwords are required." });

        if (request.NewPassword.Length < 8)
            return BadRequest(new ApiError { Error = "weak_password", Message = "New password must be at least 8 characters." });

        var result = await _userManager.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
            return BadRequest(new ApiError { Error = "password_change_failed", Message = string.Join("; ", result.Errors.Select(e => e.Description)) });

        // Update security stamp to invalidate all other sessions
        await _userManager.UpdateSecurityStampAsync(user);

        // Clear forced password change flag
        if (user.ForcePasswordChange)
        {
            user.ForcePasswordChange = false;
            await _userManager.UpdateAsync(user);
        }

        // Revoke all sessions except the current one (force re-login on other devices)
        await _sessionService.RevokeAllSessionsAsync(user.Id, ct);

        _logger.LogInformation("User {UserName} changed password", user.UserName);

        return NoContent();
    }

    [HttpGet("force-password-change")]
    [Authorize]
    public async Task<IActionResult> CheckForcePasswordChange(CancellationToken ct)
    {
        var user = await GetCurrentUserAsync(ct);
        if (user is null)
            return Unauthorized();

        return Ok(new { required = user.ForcePasswordChange, message = user.ForcePasswordChange ? DefaultAdminDefaults.ForcePasswordChangeMessage : null });
    }

    private async Task<UserEntity?> GetCurrentUserAsync(CancellationToken ct)
    {
        var userIdClaim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (userIdClaim is null || !long.TryParse(userIdClaim.Value, out var userId))
            return null;

        return await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    private string GetClientIpAddress()
    {
        return HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
    }

    private static string GenerateCsrfToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
