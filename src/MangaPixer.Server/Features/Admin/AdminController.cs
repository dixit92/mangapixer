namespace com.lifepixer.mangapixer.Server.Features.Admin;

using com.lifepixer.mangapixer.Server.Logging;

using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Reading;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Media;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using com.lifepixer.mangapixer.Server.Scanning;
using com.lifepixer.mangapixer.Server.Storage;
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
    private readonly LibraryMaintenanceService _maintenance;
    private readonly LibraryScanLauncher _scanLauncher;
    private readonly LibraryScanScheduler _scanScheduler;
    private readonly ScanRunRegistry _scanRunRegistry;
    private readonly UserManager<UserEntity> _userManager;
    private readonly LastAdminProtectionService _lastAdminProtection;
    private readonly LibraryAuthorizationService _libraryAuth;
    private readonly SessionService _sessionService;
    private readonly MangaPixerDbContext _db;
    private readonly AuditService _audit;
    private readonly ILogger<AdminController> _logger;
    private readonly IServiceScopeFactory _scopeFactory;

    public AdminController(
        LibraryRegistrationService registration,
        FilesystemBrowseService browse,
        LibraryMaintenanceService maintenance,
        LibraryScanLauncher scanLauncher,
        LibraryScanScheduler scanScheduler,
        ScanRunRegistry scanRunRegistry,
        UserManager<UserEntity> userManager,
        LastAdminProtectionService lastAdminProtection,
        LibraryAuthorizationService libraryAuth,
        SessionService sessionService,
        MangaPixerDbContext db,
        AuditService audit,
        ILogger<AdminController> logger,
        IServiceScopeFactory scopeFactory)
    {
        _registration = registration;
        _browse = browse;
        _maintenance = maintenance;
        _scanLauncher = scanLauncher;
        _scanScheduler = scanScheduler;
        _scanRunRegistry = scanRunRegistry;
        _userManager = userManager;
        _lastAdminProtection = lastAdminProtection;
        _libraryAuth = libraryAuth;
        _sessionService = sessionService;
        _db = db;
        _audit = audit;
        _logger = logger;
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

        // Registering a library writes to the catalog while a scan is also writing;
        // SQLite is single-writer, so refuse cleanly rather than let the write contend.
        // Registering a library is rare, so blocking it during a scan is an acceptable
        // trade-off. Reads are unaffected (WAL snapshots).
        var scanActive = await _db.ScanRuns.AnyAsync(s => s.Status == 0 || s.Status == 1, ct);
        if (scanActive)
            return Conflict(new ApiError
            {
                Error = "scan_in_progress",
                Message = "A library scan is in progress. Please register the new library after it completes.",
            });

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

        // Deleting a library is a large multi-table write; SQLite is single-writer,
        // so refuse cleanly while any scan is active (mirrors the register guard).
        // Reads are unaffected (WAL snapshots).
        var scanActive = await _db.ScanRuns.AnyAsync(s => s.Status == 0 || s.Status == 1, ct);
        if (scanActive)
            return Conflict(new ApiError
            {
                Error = "scan_in_progress",
                Message = "A library scan is in progress. Please delete the library after it completes.",
            });

        var success = await _registration.DeleteAsync(library.Id, ct);
        if (!success) return NotFound();
        return NoContent();
    }

    // --- Global default reader mode (1.2.0) ---
    // Admin-set defaults applied to all users; overridable per folder, and by a
    // user's own per-item override. See ReaderModeResolver for precedence.

    [HttpPut("libraries/{id}/reader-default")]
    public async Task<IActionResult> SetLibraryReaderDefault(string id, [FromBody] SetReaderModeRequest request, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        library.DefaultReaderMode = (int)request.ReaderMode;
        await _db.SaveChangesAsync(ct);
        return Ok(ToLibraryDto(library));
    }

    [HttpDelete("libraries/{id}/reader-default")]
    public async Task<IActionResult> ClearLibraryReaderDefault(string id, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        library.DefaultReaderMode = null;
        await _db.SaveChangesAsync(ct);
        return Ok(ToLibraryDto(library));
    }

    // --- Library icon (1.22.0) ---
    // Admin-picked icon name from the curated allowlist; null clears back to the
    // client-derived default. See LibraryIcons (Core) for the allowlist.

    [HttpPut("libraries/{id}/icon")]
    public async Task<IActionResult> SetLibraryIcon(string id, [FromBody] SetLibraryIconRequest request, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        if (!LibraryIcons.IsValid(request.Icon))
            return BadRequest(new ApiError { Error = "invalid_icon", Message = "Icon must be one of the allowlisted names, or null." });

        library.Icon = request.Icon;
        await _db.SaveChangesAsync(ct);
        return Ok(ToLibraryDto(library));
    }

    // --- Library scan schedule (1.23.0) ---
    // Admin-picked automatic scan preset; null clears back to the default
    // (daily). See LibraryScanSchedules (Core) for the tokens and
    // LibraryScanScheduler for how due libraries are scanned.

    [HttpPut("libraries/{id}/scan-schedule")]
    public async Task<IActionResult> SetLibraryScanSchedule(string id, [FromBody] SetLibraryScanScheduleRequest request, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        if (!LibraryScanSchedules.IsValid(request.ScanSchedule))
            return BadRequest(new ApiError { Error = "invalid_scan_schedule", Message = "ScanSchedule must be one of off, 1h, 6h, 1d, 7d, or null." });

        library.ScanSchedule = request.ScanSchedule;
        await _db.SaveChangesAsync(ct);
        return Ok(ToLibraryDto(library));
    }

    [HttpPut("folders/{nodeId}/reader-default")]
    public async Task<IActionResult> SetFolderReaderDefault(string nodeId, [FromBody] SetReaderModeRequest request, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null) return NotFound();
        if (node.Kind != (int)CatalogNodeKind.Folder)
            return BadRequest(new ApiError { Error = "not_a_folder", Message = "A reader-mode override can only be set on a folder." });

        var existing = await _db.FolderReaderDefaults.FirstOrDefaultAsync(f => f.NodeId == node.Id, ct);
        if (existing is null)
            _db.FolderReaderDefaults.Add(new FolderReaderDefaultEntity { NodeId = node.Id, ReaderMode = (int)request.ReaderMode });
        else
            existing.ReaderMode = (int)request.ReaderMode;

        await _db.SaveChangesAsync(ct);
        return Ok(new SetReaderModeRequest { ReaderMode = request.ReaderMode });
    }

    [HttpDelete("folders/{nodeId}/reader-default")]
    public async Task<IActionResult> ClearFolderReaderDefault(string nodeId, CancellationToken ct)
    {
        var node = await _db.CatalogNodes.FirstOrDefaultAsync(n => n.PublicId == nodeId, ct);
        if (node is null) return NotFound();

        var existing = await _db.FolderReaderDefaults.FirstOrDefaultAsync(f => f.NodeId == node.Id, ct);
        if (existing is not null)
        {
            _db.FolderReaderDefaults.Remove(existing);
            await _db.SaveChangesAsync(ct);
        }
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

        var launch = await _scanLauncher.StartAsync(library, $"server:{userId}", ct);
        if (launch is null)
            return Conflict(new ApiError { Error = "scan_in_progress", Message = "A scan is already running for this library." });

        return Accepted(new ScanTriggeredDto { ScanRunId = OpaqueId.Encode(launch.Run.Id) });
    }

    /// <summary>
    /// Triggers a scan for every registered library at once (1.8.0). Libraries
    /// already scanning are skipped (per-library guard) rather than failing the
    /// whole batch; the response reports how many started vs. skipped. Each
    /// started scan runs on the same background path as <see cref="TriggerScan"/>
    /// (<see cref="LibraryScanLauncher"/>: lease + <see cref="ScanRunRegistry"/> +
    /// maintenance), so cancel/history semantics are identical to a per-library scan.
    /// </summary>
    [HttpPost("libraries/scan-all")]
    public async Task<IActionResult> ScanAllLibraries(CancellationToken ct)
    {
        var userId = GetUserId();
        if (userId is null) return Unauthorized();

        var libraries = await _db.Libraries.OrderBy(l => l.Id).ToListAsync(ct);

        var started = new List<string>();
        var skipped = 0;
        foreach (var library in libraries)
        {
            var launch = await _scanLauncher.StartAsync(library, $"server:{userId}", ct);
            if (launch is not null)
                started.Add(OpaqueId.Encode(launch.Run.Id));
            else
                skipped++;
        }

        _logger.LogInformation(LogEvents.Scanning.AdminScanCompleted, "Scan-all triggered: {Started} started, {Skipped} skipped (of {Total} libraries)",
            started.Count, skipped, libraries.Count);

        return Accepted(new ScanAllResultDto
        {
            StartedCount = started.Count,
            SkippedCount = skipped,
            ScanRunIds = started,
        });
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

    // --- Thumbnail backfill (1.2.0) ---

    /// <summary>
    /// Triggers a continuous, throttled backfill that generates durable
    /// thumbnails for ALL ready archive items in a library that lack a current
    /// thumbnail (post-1.2.0: uncapped, replaces the old 10k fire-and-forget
    /// loop). Returns a count of items queued; generation runs in the
    /// background and does not block the request. This backfills items that
    /// were analyzed before the persistent-thumbnail feature existed, or whose
    /// source changed. The runner yields when the worker pool is saturated or
    /// analysis work is pending, so interactive reader reads are not starved.
    /// </summary>
    [HttpPost("libraries/{id}/thumbnails/regenerate")]
    public async Task<IActionResult> RegenerateThumbnails(string id, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == id, ct);
        if (library is null) return NotFound();

        var queuedCount = await ThumbnailGenerationService.CountItemsNeedingThumbnailsAsync(_db, library.Id, ct);

        if (queuedCount == 0)
        {
            _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillBatch, "Thumbnail regenerate: no items need thumbnails in library {LibraryId}", library.Id);
            return Ok(new ThumbnailRegenerateResponse { QueuedCount = 0 });
        }

        _logger.LogInformation(LogEvents.Worker.ThumbnailBackfillEnqueued, "Thumbnail regenerate: enqueuing {Count} items in library {LibraryId}",
            queuedCount, library.Id);

        // Fire-and-forget: the continuous runner processes all items needing
        // thumbnails (uncapped), throttled against reader demand. Each item is
        // attempted once per pass; the runner stops when none remain.
        var regenerateLibraryId = library.Id;
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var thumbService = scope.ServiceProvider.GetRequiredService<ThumbnailGenerationService>();
                await thumbService.RunContinuousBackfillAsync(regenerateLibraryId, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(LogEvents.Worker.ThumbnailGenerationFailed, ex, "Thumbnail regenerate failed (library {LibraryId}): {Error}", regenerateLibraryId, ex.GetType().Name);
            }
        }, CancellationToken.None);

        return Accepted(new ThumbnailRegenerateResponse { QueuedCount = queuedCount });
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
                IsPendingActivation = u.IsPendingActivation,
                CreatedAt = u.CreatedAt,
                LastLoginAt = u.LastLoginAt,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    [HttpPost("users")]
    public async Task<IActionResult> CreateUser([FromBody] CreateUserRequest request, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(request.Username))
            return BadRequest(new ApiError { Error = "invalid_request", Message = "Username is required." });

        var hasPassword = !string.IsNullOrWhiteSpace(request.Password);

        if (hasPassword && request.Password!.Length < 8)
            return BadRequest(new ApiError { Error = "weak_password", Message = "Password must be at least 8 characters." });

        var existing = await _userManager.FindByNameAsync(request.Username);
        if (existing is not null)
            return Conflict(new ApiError { Error = "duplicate_user", Message = "Username already exists." });

        string? rawToken = null;

        var user = new UserEntity
        {
            UserName = request.Username,
            PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
            IsAdmin = request.IsAdmin,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        if (hasPassword)
        {
            user.ForcePasswordChange = true;
            user.IsPendingActivation = false;
        }
        else
        {
            user.IsPendingActivation = true;
            user.ForcePasswordChange = false;
            rawToken = IssueActivationToken(user);
        }

        IdentityResult result;
        if (hasPassword)
        {
            result = await _userManager.CreateAsync(user, request.Password!);
        }
        else
        {
            result = await _userManager.CreateAsync(user);
        }

        if (!result.Succeeded)
            return BadRequest(new ApiError { Error = "create_failed", Message = string.Join("; ", result.Errors.Select(e => e.Description)) });

        _logger.LogInformation(
            hasPassword ? LogEvents.Auth.FirstAdminCreated : LogEvents.Administration.ActivationTokenCreated,
            "User {PublicId} created (pending-activation: {Pending})", user.PublicId, !hasPassword);

        var userDto = new AdminUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            IsAdmin = user.IsAdmin,
            IsActive = user.IsActive,
            IsPendingActivation = user.IsPendingActivation,
            CreatedAt = user.CreatedAt,
            LastLoginAt = null,
        };

        string? activationUrl = null;
        if (rawToken is not null)
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            activationUrl = $"{baseUrl}/activate?token={rawToken}";
        }

        return Ok(new CreateUserResponse { User = userDto, ActivationUrl = activationUrl });
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
            IsPendingActivation = user.IsPendingActivation,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
        });
    }

    /// <summary>
    /// Deletes a user and every row they own (1.17.0). Guarded by the last-admin
    /// protection — refuses with 409 rather than leaving the instance adminless.
    /// Owned rows are deleted explicitly, in the same transaction as the user
    /// row, rather than relying solely on the database's <c>ON DELETE CASCADE</c>
    /// foreign keys: SQLite enforces those only on connections that have run
    /// <c>PRAGMA foreign_keys = ON</c>, which this app currently sets once at
    /// startup on a single connection (see
    /// <see cref="com.lifepixer.mangapixer.Server.Persistence.DatabaseInitialization.ConfigureDatabaseAsync"/>)
    /// rather than on every pooled connection Microsoft.Data.Sqlite may hand out
    /// under concurrent load — so the DB-level cascade is not guaranteed to fire
    /// on every delete. Never touches rows the user does not own (library/catalog
    /// data, other users' rows, audit history).
    /// </summary>
    [HttpDelete("users/{id}")]
    public async Task<IActionResult> DeleteUser(string id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, ct);
        if (user is null) return NotFound();

        if (!await _lastAdminProtection.CanDeleteUserAsync(user.Id, ct))
            return Conflict(new ApiError { Error = "last_admin", Message = "Cannot delete the last active admin." });

        await using var tx = await _db.Database.BeginTransactionAsync(ct);

        await _db.Sessions.Where(s => s.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.LibraryGrants.Where(g => g.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.PrivateLibraries.Where(p => p.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.HomeExcludedLibraries.Where(h => h.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.ReadingProgress.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.ReadMarks.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.ReaderPreferences.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.ItemReaderOverrides.Where(r => r.UserId == user.Id).ExecuteDeleteAsync(ct);
        await _db.Bookmarks.Where(b => b.UserId == user.Id).ExecuteDeleteAsync(ct);

        _db.Users.Remove(user);
        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        _logger.LogInformation(LogEvents.Administration.UserDeleted, "Admin deleted user {PublicId}", user.PublicId);

        await _audit.RecordAsync(AuditActions.UserDeleted, AuditResults.Success,
            User.Identity?.Name, targetUserId: user.Id, correlationId: null, ct);

        return NoContent();
    }

    /// <summary>
    /// Reissues a one-time activation link (1.17.0) for a user created
    /// passwordless (the 1.5.0 activation onboarding flow) who has not yet
    /// activated their account — e.g. the original link expired or was lost.
    /// Reuses the same token-issuing path as <see cref="CreateUser"/> via
    /// <see cref="IssueActivationToken"/>; the previous token is invalidated
    /// because it is overwritten, not merely superseded. Only valid while the
    /// user is still pending activation; an already-activated (or password-
    /// created) user gets a clear 400 rather than a silently reissued token.
    /// </summary>
    [HttpPost("users/{id}/reissue-activation")]
    public async Task<IActionResult> ReissueActivation(string id, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == id, ct);
        if (user is null) return NotFound();

        if (!user.IsPendingActivation)
            return BadRequest(new ApiError { Error = "not_pending_activation", Message = "User has already activated their account." });

        var rawToken = IssueActivationToken(user);
        await _db.SaveChangesAsync(ct);

        _logger.LogInformation(LogEvents.Administration.ActivationReissued, "Activation link reissued for user {PublicId}", user.PublicId);

        await _audit.RecordAsync(AuditActions.UserActivationReissued, AuditResults.Success,
            User.Identity?.Name, targetUserId: user.Id, correlationId: null, ct);

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var userDto = new AdminUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            IsAdmin = user.IsAdmin,
            IsActive = user.IsActive,
            IsPendingActivation = user.IsPendingActivation,
            CreatedAt = user.CreatedAt,
            LastLoginAt = user.LastLoginAt,
        };

        return Ok(new ReissueActivationResponse
        {
            User = userDto,
            ActivationUrl = $"{baseUrl}/activate?token={rawToken}",
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

        var roleChanged = false;

        if (request.IsAdmin.HasValue && !request.IsAdmin.Value)
        {
            if (!await _lastAdminProtection.CanRemoveAdminRoleAsync(user.Id, ct))
                return BadRequest(new ApiError { Error = "last_admin", Message = "Cannot remove admin role from the last active admin." });
            if (user.IsAdmin) roleChanged = true;
            user.IsAdmin = false;
        }

        if (request.IsAdmin.HasValue && request.IsAdmin.Value)
        {
            if (!user.IsAdmin) roleChanged = true;
            user.IsAdmin = true;
        }

        // A role change (grant OR remove) must rotate the security stamp. The
        // role travels in the auth cookie's claims, minted at sign-in; existing
        // sessions carry the OLD role until they expire. Rotating the stamp
        // makes ValidateSessionAsync reject those sessions on the very next
        // request, so a demoted admin loses admin access immediately instead of
        // retaining it for up to the session lifetime (privilege-persistence
        // gap). Previously only the account-disable path rotated the stamp.
        // Also revoke the session rows (the reset-password / disable pattern) so
        // the DB reflects the forced re-login.
        if (roleChanged)
        {
            await _userManager.UpdateSecurityStampAsync(user);
            await _sessionService.RevokeAllSessionsAsync(user.Id, ct);
        }

        await _userManager.UpdateAsync(user);
        return Ok(new AdminUserDto
        {
            Id = user.PublicId,
            Username = user.UserName,
            IsAdmin = user.IsAdmin,
            IsActive = user.IsActive,
            IsPendingActivation = user.IsPendingActivation,
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

        _logger.LogInformation(LogEvents.Administration.AdminPasswordReset, "Admin reset password for user {UserName}", user.UserName);

        await _audit.RecordAsync(AuditActions.UserPasswordReset, AuditResults.Success,
            User.Identity?.Name, targetUserId: user.Id, correlationId: null, ct);

        return Ok(new ResetPasswordResponse { TemporaryPassword = tempPassword });
    }

    // --- Audit trail ---

    /// <summary>
    /// Returns one page of the administrative audit trail, newest first
    /// (1.18.0). Admin-only. The audit store predates this endpoint but was
    /// write-only; this is the read side that backs the audit-trail UI. Rows
    /// carry action/result verbs, resolved actor user name, numeric ids and a
    /// timestamp only — never paths, secrets, or DB contents.
    /// </summary>
    [HttpGet("audit")]
    public async Task<IActionResult> GetAuditTrail([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var result = await _audit.GetPageAsync(page, pageSize, ct);
        return Ok(result);
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

    /// <summary>
    /// Lists the libraries a user is currently granted access to (by public id).
    /// Admins implicitly access every library; the returned <c>isAdmin</c> flag
    /// signals that, so the client can present grants as read-only for admins.
    /// </summary>
    [HttpGet("users/{userId}/grants")]
    public async Task<IActionResult> GetUserGrants(string userId, CancellationToken ct)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == userId, ct);
        if (user is null)
            return NotFound(new ApiError { Error = "not_found", Message = "User not found." });

        // Project grant rows to the corresponding library public ids. A join keeps
        // this to a single query and naturally drops grants whose library no longer
        // exists.
        var libraryIds = await _db.LibraryGrants
            .Where(g => g.UserId == user.Id)
            .Join(_db.Libraries, g => g.LibraryId, l => l.Id, (g, l) => l.PublicId)
            .ToListAsync(ct);

        return Ok(new UserGrantsDto
        {
            UserId = user.PublicId,
            IsAdmin = user.IsAdmin,
            LibraryIds = libraryIds,
        });
    }

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

    private LibraryDto ToLibraryDto(LibraryEntity library, int? itemCount = null, bool? isScanning = null) => new()
    {
        Id = library.PublicId,
        Name = library.DisplayName,
        IsScanning = isScanning ?? false,
        ItemCount = itemCount,
        LastScanCompleted = library.LastScanCompleted,
        DefaultReaderMode = (ReaderMode?)library.DefaultReaderMode,
        Icon = library.Icon,
        ScanSchedule = LibraryScanSchedules.Resolve(library.ScanSchedule),
        NextScheduledScanAt = _scanScheduler.EstimateNextScan(library.ScanSchedule, library.LastScanCompleted),
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

    private static string GenerateActivationToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    /// <summary>
    /// Generates a fresh one-time activation token, hashes it onto the user
    /// (overwriting any previous token so it can no longer be used), and
    /// returns the raw token for a one-time response. Shared by the initial
    /// passwordless user-create path and admin-triggered reissue.
    /// </summary>
    private static string IssueActivationToken(UserEntity user)
    {
        var rawToken = GenerateActivationToken();
        user.ActivationTokenHash = HashToken(rawToken);
        user.ActivationTokenExpiry = DateTimeOffset.UtcNow.AddHours(48);
        user.ActivationTokenConsumed = false;
        return rawToken;
    }

    internal static string HashToken(string rawToken)
    {
        var tokenBytes = System.Text.Encoding.UTF8.GetBytes(rawToken);
        var hashBytes = System.Security.Cryptography.SHA256.HashData(tokenBytes);
        return Convert.ToHexStringLower(hashBytes);
    }
}
