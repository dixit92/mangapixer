namespace com.lifepixer.mangaplex.Tests.Server.Features.Auth;

using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// Integration tests for the default admin bootstrap, authentication, sessions,
/// authorization, and last-admin protection.
/// Uses real file-backed SQLite.
/// </summary>
public sealed class AuthIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPlexDbContext> _options;

    public AuthIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-auth-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "auth.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPlexDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPlexDbContext db, UserManager<UserEntity> userManager)> SetupAsync()
    {
        var db = new MangaPlexDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);

        var userStore = new MangaPlexUserStore(db);
        var identityOptions = Microsoft.Extensions.Options.Options.Create(new IdentityOptions
        {
            Password = { RequiredLength = 8, RequireDigit = false, RequireUppercase = false, RequireNonAlphanumeric = false },
            Lockout = { DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15), MaxFailedAccessAttempts = 5 },
            User = { RequireUniqueEmail = false },
        });
        var userManager = new UserManager<UserEntity>(
            userStore,
            identityOptions,
            new PasswordHasher<UserEntity>(),
            Enumerable.Empty<IUserValidator<UserEntity>>(),
            Enumerable.Empty<IPasswordValidator<UserEntity>>(),
            new UpperInvariantLookupNormalizer(),
            new IdentityErrorDescriber(),
            new ServiceCollection().BuildServiceProvider(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<UserManager<UserEntity>>.Instance);

        return (db, userManager);
    }

    [Fact]
    public async Task DefaultAdminBootstrap_CreatesAdmin_WhenNoUsersExist()
    {
        var (db, userManager) = await SetupAsync();

        var bootstrap = new DefaultAdminBootstrap(db, userManager, new DefaultAdminOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultAdminBootstrap>.Instance);

        var created = await bootstrap.BootstrapAsync();
        Assert.True(created);

        var admin = await db.Users.FirstOrDefaultAsync(u => u.UserName == "admin");
        Assert.NotNull(admin);
        Assert.True(admin!.IsAdmin);
        Assert.True(admin.ForcePasswordChange);
        Assert.True(admin.IsActive);
    }

    [Fact]
    public async Task DefaultAdminBootstrap_DoesNotCreateAdmin_WhenUsersExist()
    {
        var (db, userManager) = await SetupAsync();

        // Create a user first
        var existingUser = new UserEntity
        {
            PublicId = "u-existing",
            UserName = "existing",
            NormalizedUserName = "EXISTING",
            PasswordHash = "hash",
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(existingUser);
        await db.SaveChangesAsync();

        var bootstrap = new DefaultAdminBootstrap(db, userManager, new DefaultAdminOptions(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<DefaultAdminBootstrap>.Instance);

        var created = await bootstrap.BootstrapAsync();
        Assert.False(created);

        var adminCount = await db.Users.CountAsync(u => u.UserName == "admin");
        Assert.Equal(0, adminCount);
    }

    [Fact]
    public async Task Session_CreateAndValidate_RoundTrip()
    {
        var (db, _) = await SetupAsync();
        var sessionService = new SessionService(db);

        var user = new UserEntity
        {
            PublicId = "u-test",
            UserName = "test",
            NormalizedUserName = "TEST",
            PasswordHash = "hash",
            SecurityStamp = "stamp123",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var session = await sessionService.CreateSessionAsync(user);
        Assert.NotNull(session.TicketId);
        Assert.False(session.IsRevoked);

        var validated = await sessionService.ValidateSessionAsync(session.TicketId);
        Assert.NotNull(validated);
        Assert.Equal(user.Id, validated!.Id);
    }

    [Fact]
    public async Task Session_InvalidatedWhenSecurityStampChanges()
    {
        var (db, _) = await SetupAsync();
        var sessionService = new SessionService(db);

        var user = new UserEntity
        {
            PublicId = "u-test",
            UserName = "test",
            NormalizedUserName = "TEST",
            PasswordHash = "hash",
            SecurityStamp = "stamp123",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var session = await sessionService.CreateSessionAsync(user);

        // Change security stamp (simulates password change)
        user.SecurityStamp = "newstamp456";
        db.Users.Update(user);
        await db.SaveChangesAsync();

        var validated = await sessionService.ValidateSessionAsync(session.TicketId);
        Assert.Null(validated);
    }

    [Fact]
    public async Task Session_InvalidatedWhenUserDisabled()
    {
        var (db, _) = await SetupAsync();
        var sessionService = new SessionService(db);

        var user = new UserEntity
        {
            PublicId = "u-test",
            UserName = "test",
            NormalizedUserName = "TEST",
            PasswordHash = "hash",
            SecurityStamp = "stamp123",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var session = await sessionService.CreateSessionAsync(user);

        // Disable user
        user.IsActive = false;
        db.Users.Update(user);
        await db.SaveChangesAsync();

        var validated = await sessionService.ValidateSessionAsync(session.TicketId);
        Assert.Null(validated);
    }

    [Fact]
    public async Task Session_RevokeAllSessions_InvalidatesAll()
    {
        var (db, _) = await SetupAsync();
        var sessionService = new SessionService(db);

        var user = new UserEntity
        {
            PublicId = "u-test",
            UserName = "test",
            NormalizedUserName = "TEST",
            PasswordHash = "hash",
            SecurityStamp = "stamp123",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var session1 = await sessionService.CreateSessionAsync(user);
        var session2 = await sessionService.CreateSessionAsync(user);

        await sessionService.RevokeAllSessionsAsync(user.Id);

        Assert.Null(await sessionService.ValidateSessionAsync(session1.TicketId));
        Assert.Null(await sessionService.ValidateSessionAsync(session2.TicketId));
    }

    [Fact]
    public async Task LibraryAuthorization_AdminCanAccessAllLibraries()
    {
        var (db, _) = await SetupAsync();
        var authService = new LibraryAuthorizationService(db);

        var admin = new UserEntity
        {
            PublicId = "u-admin",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsAdmin = true,
            IsActive = true,
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin);

        var library = new LibraryEntity
        {
            PublicId = "lib1",
            DisplayName = "Test",
            RootPath = "/tmp/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        Assert.True(await authService.CanAccessLibraryAsync(admin.Id, library.Id));
    }

    [Fact]
    public async Task LibraryAuthorization_ReaderNeedsGrant()
    {
        var (db, _) = await SetupAsync();
        var authService = new LibraryAuthorizationService(db);

        var admin = new UserEntity
        {
            PublicId = "u-admin",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsAdmin = true,
            IsActive = true,
            SecurityStamp = "stamp-admin",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var reader = new UserEntity
        {
            PublicId = "u-reader",
            UserName = "reader",
            NormalizedUserName = "READER",
            IsAdmin = false,
            IsActive = true,
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.AddRange(admin, reader);

        var library1 = new LibraryEntity
        {
            PublicId = "lib1",
            DisplayName = "Lib1",
            RootPath = "/tmp/lib1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var library2 = new LibraryEntity
        {
            PublicId = "lib2",
            DisplayName = "Lib2",
            RootPath = "/tmp/lib2",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.AddRange(library1, library2);
        await db.SaveChangesAsync();

        // Admin grants reader access to library1 only
        Assert.True(await authService.GrantAccessAsync(admin.Id, reader.Id, library1.Id));

        Assert.True(await authService.CanAccessLibraryAsync(reader.Id, library1.Id));
        Assert.False(await authService.CanAccessLibraryAsync(reader.Id, library2.Id));
    }

    [Fact]
    public async Task LibraryAuthorization_InactiveUserDenied()
    {
        var (db, _) = await SetupAsync();
        var authService = new LibraryAuthorizationService(db);

        var disabled = new UserEntity
        {
            PublicId = "u-disabled",
            UserName = "disabled",
            NormalizedUserName = "DISABLED",
            IsAdmin = false,
            IsActive = false,
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(disabled);

        var library = new LibraryEntity
        {
            PublicId = "lib1",
            DisplayName = "Test",
            RootPath = "/tmp/test",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.Add(library);
        await db.SaveChangesAsync();

        // Even with a grant, inactive user is denied
        db.LibraryGrants.Add(new LibraryGrantEntity
        {
            UserId = disabled.Id,
            LibraryId = library.Id,
            GrantedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        Assert.False(await authService.CanAccessLibraryAsync(disabled.Id, library.Id));
    }

    [Fact]
    public async Task LastAdminProtection_PreventsDisablingLastAdmin()
    {
        var (db, _) = await SetupAsync();
        var protection = new LastAdminProtectionService(db);

        var admin = new UserEntity
        {
            PublicId = "u-admin1",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsAdmin = true,
            IsActive = true,
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        // Only one admin — cannot disable
        Assert.False(await protection.CanDisableUserAsync(admin.Id));
        Assert.True(await protection.IsLastAdminAsync(admin.Id));

        // Add a second admin
        var admin2 = new UserEntity
        {
            PublicId = "u-admin2",
            UserName = "admin2",
            NormalizedUserName = "ADMIN2",
            IsAdmin = true,
            IsActive = true,
            SecurityStamp = "stamp2",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin2);
        await db.SaveChangesAsync();

        // Now can disable the first admin
        Assert.True(await protection.CanDisableUserAsync(admin.Id));
        Assert.False(await protection.IsLastAdminAsync(admin.Id));
    }

    [Fact]
    public async Task LastAdminProtection_PreventsRemovingLastAdminRole()
    {
        var (db, _) = await SetupAsync();
        var protection = new LastAdminProtectionService(db);

        var admin = new UserEntity
        {
            PublicId = "u-admin-r1",
            UserName = "admin",
            NormalizedUserName = "ADMIN",
            IsAdmin = true,
            IsActive = true,
            SecurityStamp = "stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin);
        await db.SaveChangesAsync();

        Assert.False(await protection.CanRemoveAdminRoleAsync(admin.Id));

        // Add a second admin
        var admin2 = new UserEntity
        {
            PublicId = "u-admin-r2",
            UserName = "admin2",
            NormalizedUserName = "ADMIN2",
            IsAdmin = true,
            IsActive = true,
            SecurityStamp = "stamp2",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.Add(admin2);
        await db.SaveChangesAsync();

        Assert.True(await protection.CanRemoveAdminRoleAsync(admin.Id));
    }

    [Fact]
    public async Task LoginRateLimiter_BlocksExcessiveAttempts()
    {
        var limiter = new LoginRateLimiter(new LoginRateLimitOptions
        {
            MaxAttemptsPerIp = 3,
            MaxAttemptsPerUser = 3,
            Window = TimeSpan.FromMinutes(5),
        });

        var ip = "127.0.0.1";
        var username = "testuser";

        // First 3 attempts should be allowed
        Assert.True(limiter.AllowAttempt(ip, username));
        limiter.RecordFailure(ip, username);

        Assert.True(limiter.AllowAttempt(ip, username));
        limiter.RecordFailure(ip, username);

        Assert.True(limiter.AllowAttempt(ip, username));
        limiter.RecordFailure(ip, username);

        // 4th attempt should be blocked
        Assert.False(limiter.AllowAttempt(ip, username));
    }

    [Fact]
    public async Task LoginRateLimiter_ResetsOnSuccess()
    {
        var limiter = new LoginRateLimiter(new LoginRateLimitOptions
        {
            MaxAttemptsPerIp = 3,
            MaxAttemptsPerUser = 3,
            Window = TimeSpan.FromMinutes(5),
        });

        var ip = "127.0.0.1";
        var username = "testuser";

        // 2 failed attempts
        limiter.RecordFailure(ip, username);
        limiter.RecordFailure(ip, username);

        // Success resets the counter
        limiter.RecordSuccess(ip, username);

        // Should be allowed again
        Assert.True(limiter.AllowAttempt(ip, username));
    }

    [Fact]
    public async Task PasswordChange_UpdatesSecurityStamp()
    {
        var (db, userManager) = await SetupAsync();

        var user = new UserEntity
        {
            PublicId = "u-pwchange",
            UserName = "testuser",
            NormalizedUserName = "TESTUSER",
            IsActive = true,
            ForcePasswordChange = false,
            SecurityStamp = "original-stamp",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var result = await userManager.CreateAsync(user, "InitialPassword123!");
        Assert.True(result.Succeeded);

        var originalStamp = user.SecurityStamp;

        // Update security stamp
        await userManager.UpdateSecurityStampAsync(user);

        Assert.NotEqual(originalStamp, user.SecurityStamp);
    }

    [Fact]
    public async Task TwoUserIsolation_ReaderCannotAccessOtherLibrary()
    {
        var (db, _) = await SetupAsync();
        var authService = new LibraryAuthorizationService(db);

        var reader1 = new UserEntity
        {
            PublicId = "u-reader1",
            UserName = "reader1",
            NormalizedUserName = "READER1",
            IsAdmin = false,
            IsActive = true,
            SecurityStamp = "stamp1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var reader2 = new UserEntity
        {
            PublicId = "u-reader2",
            UserName = "reader2",
            NormalizedUserName = "READER2",
            IsAdmin = false,
            IsActive = true,
            SecurityStamp = "stamp2",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Users.AddRange(reader1, reader2);

        var lib1 = new LibraryEntity
        {
            PublicId = "lib1",
            DisplayName = "Lib1",
            RootPath = "/tmp/lib1",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        var lib2 = new LibraryEntity
        {
            PublicId = "lib2",
            DisplayName = "Lib2",
            RootPath = "/tmp/lib2",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Libraries.AddRange(lib1, lib2);
        await db.SaveChangesAsync();

        // Grant reader1 to lib1, reader2 to lib2
        db.LibraryGrants.Add(new LibraryGrantEntity { UserId = reader1.Id, LibraryId = lib1.Id, GrantedAt = DateTimeOffset.UtcNow });
        db.LibraryGrants.Add(new LibraryGrantEntity { UserId = reader2.Id, LibraryId = lib2.Id, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();

        // Each reader can access their own library
        Assert.True(await authService.CanAccessLibraryAsync(reader1.Id, lib1.Id));
        Assert.True(await authService.CanAccessLibraryAsync(reader2.Id, lib2.Id));

        // But not the other's
        Assert.False(await authService.CanAccessLibraryAsync(reader1.Id, lib2.Id));
        Assert.False(await authService.CanAccessLibraryAsync(reader2.Id, lib1.Id));
    }
}
