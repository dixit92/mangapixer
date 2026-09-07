namespace com.lifepixer.mangaplex.Server.Features.Admin;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.Server.Scanning;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Linq;
using System.Security.Cryptography;

/// <summary>
/// Administration endpoints for library and user management.
/// All endpoints require the Admin role. Root paths are never echoed
/// in any response. Password hashes are never returned.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[Authorize(Policy = "Admin")]
public sealed class AdminController : ControllerBase
{
    private readonly LibraryRegistrationService _registration;
    private readonly FilesystemBrowseService _browse;
    private readonly ScanLeaseService _leaseService;
    private readonly LibraryMaintenanceService _maintenance;
    private readonly LibraryScanPolicy _scanPolicy;
    private readonly ScanRunRegistry _scanRunRegistry;
    private readonly UserManager<UserEntity> _userManager;
    private readonly LastAdminProtectionService _lastAdminProtection;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly SessionService _sessionService;
    private readonly MangaPlexDbContext _db;
    private readonly ILogger<AdminController> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IServiceScopeFactory _scopeFactory;

    public AdminController(
        LibraryRegistrationService registration,
        FilesystemBrowseService browse,
        ScanLeaseService leaseService,
        LibraryMaintenanceService maintenance,
        LibraryScanPolicy scanPolicy,
        ScanRunRegistry scanRunRegistry,
        UserManager<UserEntity> userManager,
        LastAdminProtectionService lastAdminProtection,
        LibraryAuthorizationService libraryAuth,
        SessionService sessionService,
        MangaPlexDbContext db,
        ILogger<AdminController> logger,
        ILoggerFactory loggerFactory,
        IServiceScopeFactory scopeFactory)
    {
        _registration = registration;
        _browse = browse;
        _leaseService = leaseService;
        _maintenance = maintenance;
        _scanPolicy = scanPolicy;
        _scanRunRegistry = scanRunRegistry;
        _userManager = userManager;
        _lastAdminProtection = lastAdminProtection;
        _libraryAuth = libraryAuth;
        _sessionService = sessionService;
        _db = db;
        _logger = logger;
        _loggerFactory = loggerFactory;
        _scopeFactory = scopeFactory;
    }

    // --- Libraries ---

    /// <summary>
    /// Lists directories under the configured media browse root so an admin can
    /// pick a library root instead of typing a server-side path. Confined to the
    /// browse root (traversal-proof); directories only. Admin-only via the
    /// controller policy.
    /// </summary>
    [HttpGet("libraries/browse")]
    public IActionResult BrowseLibraryPaths([FromQuery] string? path)
    {
        return Ok(_browse.Browse(path));
    }

    [HttpPost("libraries")]
    public async Task<IActionResult> RegisterLibrary([FromBody] RegisterLibraryRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.RootPath))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "DisplayName and RootPath are required." });

        var result = await _registration.RegisterAsync(request.DisplayName, request.RootPath, ct: ct);
        if (!result.Success)
            return BadRequest(new ApiError { Error = result.Error ?? "registration_failed", Message = result.Message ?? "Registration failed." });

        var library = result.Library!;
        return Ok(ToLibraryDto(library));
    }

    [HttpGet("libraries/{id}")]
    public async Task<IActionResult> GetLibrary(string id, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        var itemCount = await _db.CatalogNodes
            .CountAsync(n => n.LibraryId == library.Id && n.Kind == 1 && n.Availability != 5, ct);

        var isScanning = await _db.ScanRuns
            .AnyAsync(s => s.LibraryId == library.Id && s.Status == 1, ct);

        return Ok(ToLibraryDto(library, itemCount, isScanning));
    }

    [HttpPost("libraries/{id}/update")]
    public async Task<IActionResult> UpdateLibrary(string id, [FromBody] UpdateLibraryRequest request, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        if (string.IsNullOrWhiteSpace(request.DisplayName))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "DisplayName is required." });

        library.DisplayName = request.DisplayName;
        await _db.SaveChangesAsync(ct);
        return Ok(ToLibraryDto(library));
    }

    [HttpDelete("libraries/{id}")]
    public async Task<IActionResult> UnregisterLibrary(string id, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        var success = await _registration.UnregisterAsync(library.Id, ct);
        if (!success) return NotFound();
        return NoContent();
    }

    // --- Scanning ---

    [HttpPost("libraries/{id}/scan")]
    public async Task<IActionResult> TriggerScan(string id, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var lease = await _leaseService.AcquireLeaseAsync(library.Id, $"server:{userId.Value}", TimeSpan.FromMinutes(30), ct);
        if (lease is null)
            return Conflict(new ApiError { Error = "scan_in_progress", Message = "A scan is already running for this library." });

        // Register a cancellation token so CancelScan can cooperatively
        // cancel the background scan (audit defect D34).
        var scanCt = _scanRunRegistry.Register(lease.Id);

        // Run scan on a background task — the HTTP request returns 202 immediately.
        // Use a dedicated DI scope so scoped services (DbContext, etc.) are not
        // disposed when the controller's request scope ends.
        var libraryId = library.Id;
        var libraryRootPath = library.RootPath;
        var leaseId = lease.Id;
        var scanRevision = lease.ScanRevision;
        var leaseOwner = lease.LeaseOwner ?? "server";

        _ = Task.Run(async () =>
        {
            using var scope = _scopeFactory.CreateScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<MangaPlexDbContext>();
            var scopedMaintenance = scope.ServiceProvider.GetRequiredService<LibraryMaintenanceService>();
            var scopedLeaseService = scope.ServiceProvider.GetRequiredService<ScanLeaseService>();
            var scopedJobScheduler = scope.ServiceProvider.GetRequiredService<JobScheduler>();
            try
            {
                await scopedMaintenance.EnterMaintenanceAsync(libraryId);
                var fs = new ReadOnlyLibraryFileSystem(libraryRootPath);
                var coordinator = new LibraryScanCoordinator(
                    scopedDb, fs, _scanPolicy, libraryId, scanRevision, leaseOwner,
                    _loggerFactory.CreateLogger<LibraryScanCoordinator>());
                var result = await coordinator.ScanAsync(scanCt);

                // Persist scan counters to ScanRunEntity (audit defect D8).
                // Previously TriggerScan logged result.NodesAdded but never
                // wrote counts to the ScanRun, so GET /scans reported zeros.
                var scanRun = await scopedDb.ScanRuns.FirstOrDefaultAsync(s => s.Id == leaseId, scanCt);
                if (scanRun is not null)
                {
                    scanRun.NodesObserved = result.NodesObserved;
                    scanRun.NodesAdded = result.NodesAdded;
                    scanRun.NodesUpdated = result.NodesUpdated;
                    scanRun.NodesTombstoned = result.NodesTombstoned;
                    scanRun.Status = result.Success ? 2 : 3;
                    scanRun.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync(scanCt);
                }

                await scopedLeaseService.ReleaseLeaseAsync(leaseId, result.Success, result.Error);
                await scopedMaintenance.ExitMaintenanceAsync(libraryId);
                _logger.LogInformation("Scan completed for library {LibraryId}: {Added} added, {Tombstoned} tombstoned",
                    libraryId, result.NodesAdded, result.NodesTombstoned);

                // Enqueue analysis for pending archive items after a successful
                // scan (audit defect D33). Previously items were only analyzed
                // when a reader opened them, so page counts never appeared
                // after a scan.
                if (result.Success)
                {
                    await EnqueueAnalysisForPendingItemsAsync(scopedDb, scopedJobScheduler, libraryId, scanCt);
                }
            }
            catch (OperationCanceledException)
            {
                // Scan was cancelled via CancelScan (audit defect D34).
                var scanRun = await scopedDb.ScanRuns.FirstOrDefaultAsync(s => s.Id == leaseId);
                if (scanRun is not null)
                {
                    scanRun.Status = 4; // cancelled
                    scanRun.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync();
                }
                await scopedLeaseService.ReleaseLeaseAsync(leaseId, false, "cancelled");
                await scopedMaintenance.ExitMaintenanceAsync(libraryId);
                _logger.LogInformation("Scan cancelled for library {LibraryId}", libraryId);
            }
            catch (Exception ex)
            {
                var scanRun = await scopedDb.ScanRuns.FirstOrDefaultAsync(s => s.Id == leaseId);
                if (scanRun is not null)
                {
                    scanRun.Status = 3; // failed
                    scanRun.SanitizedError = ex.GetType().Name;
                    scanRun.CompletedAt = DateTimeOffset.UtcNow;
                    await scopedDb.SaveChangesAsync();
                }
                await scopedLeaseService.ReleaseLeaseAsync(leaseId, false, ex.GetType().Name);
                await scopedMaintenance.ExitMaintenanceAsync(libraryId);
                _logger.LogWarning("Scan failed for library {LibraryId}: {Error}", libraryId, ex.GetType().Name);
            }
            finally
            {
                _scanRunRegistry.Complete(leaseId);
            }
        }, CancellationToken.None);

        return Accepted(new ScanTriggeredDto { ScanRunId = OpaqueId.Encode(lease.Id) });
    }

    /// <summary>
    /// Enqueues analysis jobs for archive items in a library that are in the
    /// pending analysis state (audit defect D33). Bounded to parallelism 2
    /// to avoid flooding the worker pool.
    /// </summary>
    private async Task EnqueueAnalysisForPendingItemsAsync(
        MangaPlexDbContext db,
        JobScheduler scheduler,
        long libraryId,
        CancellationToken ct)
    {
        try
        {
            var pendingItems = await db.CatalogNodes
                .Where(n => n.LibraryId == libraryId && n.Kind == 1 && n.Availability != 5)
                .Join(db.ArchiveItems.Where(a => a.AnalysisState == 1),
                      n => n.Id, a => a.NodeId,
                      (n, a) => new { Node = n, Item = a })
                .ToListAsync(ct);

            if (pendingItems.Count == 0)
                return;

            _logger.LogInformation("Enqueuing analysis for {Count} pending items in library {LibraryId}",
                pendingItems.Count, libraryId);

            // Enqueue at Background priority — scan-triggered analysis is not
            // interactive and should not block reader-triggered analysis.
            foreach (var entry in pendingItems)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    // Resolve the source path via the library root + relative path.
                    // The scheduler stores this for the worker; the path is never
                    // echoed in any API response.
                    var library = await db.Libraries.FirstAsync(l => l.Id == entry.Node.LibraryId, ct);
                    var sourcePath = Path.Combine(library.RootPath, entry.Node.RelativePath);
                    var fileInfo = new FileInfo(sourcePath);
                    if (!fileInfo.Exists)
                        continue;

                    // Fire-and-forget: EnqueueAsync returns a Task that only
                    // completes when the job is *analyzed*. Awaiting it here would
                    // serialize the whole loop on each job's completion (and block
                    // the background scan on the very first job). We only need the
                    // job queued; the pool drains the queue at its own pace. Observe
                    // the task so a faulted job does not raise UnobservedTaskException.
                    _ = scheduler.EnqueueAsync(
                        itemId: entry.Node.Id,
                        contentVersion: entry.Item.ContentVersion,
                        operation: JobOperation.Analyze,
                        priority: JobPriority.Background,
                        archivePath: sourcePath,
                        expectedLastWriteTicks: fileInfo.LastWriteTimeUtc.Ticks,
                        expectedByteLength: fileInfo.Length,
                        callerToken: CancellationToken.None)
                        .ContinueWith(static t => { _ = t.Exception; },
                            CancellationToken.None,
                            TaskContinuationOptions.OnlyOnFaulted,
                            TaskScheduler.Default);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning("Failed to enqueue analysis for item {ItemId}: {Error}",
                        entry.Node.Id, ex.GetType().Name);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning("Post-scan analysis enqueue failed for library {LibraryId}: {Error}",
                libraryId, ex.GetType().Name);
        }
    }

    [HttpPost("scans/{scanRunId}/cancel")]
    public async Task<IActionResult> CancelScan(string scanRunId, CancellationToken ct)
    {
        var runId = OpaqueId.Decode(scanRunId);
        var scanRun = await _db.ScanRuns.FirstOrDefaultAsync(s => s.Id == runId, ct);
        if (scanRun is null) return NotFound();

        if (scanRun.Status != 1)
            return Conflict(new ApiError { Error = "not_running", Message = "Scan is not currently running." });

        // Cooperatively cancel the background scan via the registry (audit defect D34).
        // The background task's catch (OperationCanceledException) marks the run
        // cancelled, releases the lease, and exits maintenance.
        _scanRunRegistry.Cancel(runId);

        return NoContent();
    }

    [HttpGet("libraries/{id}/scans")]
    public async Task<IActionResult> GetScanHistory(string id, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        var scans = await _db.ScanRuns
            .Where(s => s.LibraryId == library.Id)
            .OrderByDescending(s => s.StartedAt)
            .Take(20)
            .Select(s => new ScanRunDto
            {
                Id = OpaqueId.Encode(s.Id),
                Status = ScanStatusToString(s.Status),
                StartedAt = s.StartedAt,
                CompletedAt = s.CompletedAt,
                NodesObserved = s.NodesObserved,
                NodesAdded = s.NodesAdded,
                NodesTombstoned = s.NodesTombstoned,
                Error = s.SanitizedError,
            })
            .ToListAsync(ct);

        return Ok(scans);
    }

    // --- Users ---

    [HttpGet("users")]
    public async Task<IActionResult> ListUsers(CancellationToken ct)
    {
        var users = await _db.Users
            .OrderBy(u => u.UserName)
            .Select(u => new AdminUserDto
            {
                Id = u.PublicId,
                Username = u.UserName,
                IsAdmin = u.IsAdmin,
                IsActive = u.IsActive,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Username and password are required." });

        if (request.Password.Length < 8)
            return BadRequest(new ApiError { Error = "weak_password", Message = "Password must be at least 8 characters." });

        var existing = await _userManager.FindByNameAsync(request.Username);
        if (existing is not null)
            return Conflict(new ApiError { Error = "duplicate_user", Message = "Username already exists." });

        var user = new UserEntity
        {
            UserName = request.Username,
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            IsAdmin = request.IsAdmin,
            IsActive = true,
            ForcePasswordChange = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        var result = await _userManager.CreateAsync(user, request.Password);
        if (!result.Succeeded)
            return BadRequest(new ApiError { Error = "create_failed", Message = string.Join("; ", result.Errors.Select(e => e.Description)) });

        return Ok(new AdminUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            IsAdmin = user.IsAdmin,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt,
            LastLoginAt = null,
        });
    }

    [HttpGet("users/{id}")]
    public async Task<IActionResult> GetUser(string id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, ct);
        if (user is null) return NotFound();

        return Ok(new AdminUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            IsAdmin = user.IsAdmin,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
        });
    }

    [HttpPost("users/{id}/update")]
    public async Task<IActionResult> UpdateUser(string id, [FromBody] UpdateUserRequest request, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, ct);
        if (user is null) return NotFound();

        if (request.IsActive.HasValue && !request.IsActive.Value)
        {
            if (!await _lastAdminProtection.CanDisableUserAsync(user.Id, ct))
                return BadRequest(new ApiError { Error = "last_admin", Message = "Cannot disable the last active admin." });
            user.IsActive = false;
            await _userManager.UpdateSecurityStampAsync(user);
        }

        if (request.IsActive.HasValue && request.IsActive.Value)
            user.IsActive = true;

        if (request.IsAdmin.HasValue && !request.IsAdmin.Value)
        {
            if (!await _lastAdminProtection.CanRemoveAdminRoleAsync(user.Id, ct))
                return BadRequest(new ApiError { Error = "last_admin", Message = "Cannot remove admin role from the last active admin." });
            user.IsAdmin = false;
        }

        if (request.IsAdmin.HasValue && request.IsAdmin.Value)
            user.IsAdmin = true;

        await _userManager.UpdateAsync(user);
        return Ok(new AdminUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            IsAdmin = user.IsAdmin,
            IsActive = user.IsActive,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
        });
    }

    [HttpPost("users/{id}/reset-password")]
    public async Task<IActionResult> ResetPassword(string id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, ct);
        if (user is null) return NotFound();

        var tempPassword = GenerateTempPassword();
        var passwordHasher = HttpContext.RequestServices.GetRequiredService<Microsoft.AspNetCore.Identity.IPasswordHasher<UserEntity>>();
        user.PasswordHash = passwordHasher.HashPassword(user, tempPassword);
        user.ForcePasswordChange = true;
        user.SecurityStamp = Guid.NewGuid().ToString("N");
        await _db.SaveChangesAsync(ct);

        // Revoke all sessions — user must re-login with the temp password.
        await _sessionService.RevokeAllSessionsAsync(user.Id, ct);

        _logger.LogInformation("Admin reset password for user {UserName}", user.UserName);

        return Ok(new ResetPasswordResponse { TemporaryPassword = tempPassword });
    }

    [HttpDelete("users/{id}/sessions")]
    public async Task<IActionResult> RevokeUserSessions(string id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, ct);
        if (user is null) return NotFound();

        await _userManager.UpdateSecurityStampAsync(user);
        await _sessionService.RevokeAllSessionsAsync(user.Id, ct);

        return NoContent();
    }

    // --- Grants ---

    [HttpPut("users/{userId}/grants/{libraryId}")]
    public async Task<IActionResult> GrantAccess(string userId, string libraryId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId, ct);
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == libraryId, ct);
        if (user is null || library is null)
            return NotFound(new ApiError { Error = "not_found", Message = "User or library not found." });

        var adminId = GetUserId();
        if (adminId is null) return Unauthorized();

        var success = await _libraryAuth.GrantAccessAsync(adminId.Value, user.Id, library.Id, ct);
        if (!success)
            return NotFound(new ApiError { Error = "not_found", Message = "User or library not found." });

        return NoContent();
    }

    [HttpDelete("users/{userId}/grants/{libraryId}")]
    public async Task<IActionResult> RevokeAccess(string userId, string libraryId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId, ct);
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == libraryId, ct);
        if (user is null || library is null)
            return NotFound(new ApiError { Error = "not_found", Message = "Grant not found." });

        var adminId = GetUserId();
        if (adminId is null) return Unauthorized();

        var success = await _libraryAuth.RevokeAccessAsync(adminId.Value, user.Id, library.Id, ct);
        if (!success)
            return NotFound(new ApiError { Error = "not_found", Message = "Grant not found." });

        return NoContent();
    }

    // --- Helpers ---

    private long? GetUserId()
    {
        var claim = User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (claim is null || !long.TryParse(claim.Value, out var id))
            return null;
        return id;
    }

    private static LibraryDto ToLibraryDto(LibraryEntity library, int? itemCount = null, bool? isScanning = null) => new()
    {
        Id = library.PublicId,
        Name = library.DisplayName,
        IsScanning = isScanning ?? false,
        ItemCount = itemCount,
        LastScanCompleted = library.LastScanCompleted,
    };

    private static string ScanStatusToString(int status) => status switch
    {
        0 => "pending",
        1 => "running",
        2 => "completed",
        3 => "failed",
        4 => "cancelled",
        5 => "interrupted",
        _ => "unknown",
    };

    private static string GenerateTempPassword()
    {
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$";
        var bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        var charsArr = new char[16];
        for (var i = 0; i < bytes.Length; i++)
            charsArr[i] = chars[bytes[i] % chars.Length];
        return new string(charsArr);
    }
}
