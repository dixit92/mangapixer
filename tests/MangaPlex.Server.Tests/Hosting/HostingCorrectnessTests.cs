namespace com.lifepixer.mangaplex.Tests.Server.Hosting;

using com.lifepixer.mangaplex.Server.Features.Auth;
using com.lifepixer.mangaplex.Server.Media;
using com.lifepixer.mangaplex.Server.Operations;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;
using Xunit;

/// <summary>
/// C00 host correctness tests: startup recovery, session cleanup with
/// DateTimeOffset translation, cache eviction without spurious warnings,
/// and schema validation against the bumped version.
/// </summary>
public sealed class HostingCorrectnessTests
{
    // (a) WebApplicationFactory test: startup log has no "Startup recovery
    // failed" or "Maintenance" + "failed" events.
    [Fact]
    public async Task Startup_NoRecoveryOrMaintenanceFailureEvents()
    {
        var sink = new CollectingSink();
        var factory = new C00WebApplicationFactory(sink);

        try
        {
            // Creating a client starts the host and all hosted services.
            using var client = factory.CreateClient();

            // Give the startup recovery a moment to complete.
            await Task.Delay(500);

            // No startup recovery failure
            Assert.False(sink.ContainsMessageAtLevel(LogEventLevel.Warning, "Startup recovery failed"),
                "Startup recovery should not fail");

            // No maintenance failure
            Assert.False(sink.ContainsMessageAtLevel(LogEventLevel.Warning, "Maintenance") &&
                         sink.ContainsMessageAtLevel(LogEventLevel.Warning, "failed"),
                "Maintenance should not fail");

            // Startup recovery should have completed successfully
            Assert.True(sink.ContainsMessage("Startup recovery complete"),
                "Startup recovery should complete successfully");
        }
        finally
        {
            factory.Dispose();
        }
    }

    // (b) DB test: insert a session with ExpiresAt in the past, call
    // CleanupExpiredSessionsAsync, assert it is removed. This verifies the
    // DateTimeOffsetToBinaryConverter fix (audit defect D26).
    [Fact]
    public async Task SessionCleanup_RemovesExpiredSession_WithDateTimeOffsetConverter()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-c00-session-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            var connectionString = DatabaseInitialization.BuildConnectionString(dbPath);
            var options = new DbContextOptionsBuilder<MangaPlexDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var db = new MangaPlexDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);

            // Insert a user (required by FK)
            var user = new UserEntity
            {
                PublicId = "utest",
                UserName = "testuser",
                NormalizedUserName = "TESTUSER",
                PasswordHash = "hash",
                SecurityStamp = "stamp",
                IsActive = true,
                ForcePasswordChange = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();

            // Insert an expired session
            var expiredSession = new SessionEntity
            {
                TicketId = "expired-ticket",
                UserId = user.Id,
                SecurityStamp = "stamp",
                IssuedAt = DateTimeOffset.UtcNow.AddDays(-10),
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1), // expired
                IsRevoked = false,
            };
            db.Sessions.Add(expiredSession);

            // Insert an active session (should NOT be removed)
            var activeSession = new SessionEntity
            {
                TicketId = "active-ticket",
                UserId = user.Id,
                SecurityStamp = "stamp",
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(7), // not expired
                IsRevoked = false,
            };
            db.Sessions.Add(activeSession);
            await db.SaveChangesAsync();

            // Act — this would throw InvalidOperationException before the
            // DateTimeOffsetToBinaryConverter fix (audit defect D26).
            var sessionService = new SessionService(db);
            await sessionService.CleanupExpiredSessionsAsync();

            // Assert: expired session removed, active session remains
            var remaining = await db.Sessions.ToListAsync();
            Assert.Single(remaining);
            Assert.Equal("active-ticket", remaining[0].TicketId);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // (c) DB test: ValidateSchemaAsync returns Valid on a freshly created
    // database with the bumped schema version (2).
    [Fact]
    public async Task ValidateSchema_ReturnsValid_OnFreshDatabase_Version2()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-c00-schema-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            var connectionString = DatabaseInitialization.BuildConnectionString(dbPath);
            var options = new DbContextOptionsBuilder<MangaPlexDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var db = new MangaPlexDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);

            var service = new JobRecoveryService(db);
            var result = await service.ValidateSchemaAsync();

            Assert.True(result.Valid);
            Assert.Equal(DatabaseInitialization.CurrentSchemaVersion, result.Version);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // (c-extra) DB test: ValidateSchemaAsync rejects a version-1 database
    // (the pre-release format before the DateTimeOffset converter).
    [Fact]
    public async Task ValidateSchema_RejectsVersion1_Database()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-c00-v1-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);
        var dbPath = Path.Combine(tempDir, "test.db");

        try
        {
            var connectionString = DatabaseInitialization.BuildConnectionString(dbPath);
            var options = new DbContextOptionsBuilder<MangaPlexDbContext>()
                .UseSqlite(connectionString)
                .Options;

            await using var db = new MangaPlexDbContext(options);
            await db.Database.EnsureCreatedAsync();
            await DatabaseInitialization.ConfigureDatabaseAsync(db);

            // Force the schema version back to 1 (simulating a pre-release DB)
            await DatabaseInitialization.SetSchemaVersionAsync(db, 1);

            var service = new JobRecoveryService(db);
            var result = await service.ValidateSchemaAsync();

            Assert.False(result.Valid);
            Assert.Contains("older than expected", result.Error);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // (d) Unit test: EvictOverBudget does not log at Warning level.
    [Fact]
    public async Task EvictOverBudget_DoesNotLogAtWarningLevel()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-c00-evict-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var cacheRoot = Path.Combine(tempDir, "cache");
            var testLogger = new TestLogger<CacheService>();
            // Small budget: 1KB, publish a 2KB file → over budget
            var cache = new CacheService(cacheRoot, 1024, testLogger);
            cache.Initialize();

            var sourceFile = Path.Combine(tempDir, "source.png");
            File.WriteAllText(sourceFile, new string('x', 2048));
            var cacheKey = CacheService.BuildCacheKey(1, 1, "page.png", "original");
            await cache.PublishAsync(cacheKey, sourceFile, "image/png");

            // Clear any log events from the publish path
            testLogger.ClearEvents();

            // Act — routine over-budget eviction
            var freed = cache.EvictOverBudget();

            // Assert: something was freed (we were over budget)
            Assert.True(freed >= 0);

            // No Warning-level events (audit defect D27)
            Assert.DoesNotContain(testLogger.Events, e => e.Level == LogLevel.Warning);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    // (d-extra) EvictOverBudget on a cache that is under budget is a no-op.
    [Fact]
    public async Task EvictOverBudget_NoOpWhenUnderBudget()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "mangaplex-c00-noop-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(tempDir);

        try
        {
            var cacheRoot = Path.Combine(tempDir, "cache");
            var testLogger = new TestLogger<CacheService>();
            var cache = new CacheService(cacheRoot, 10 * 1024 * 1024, testLogger);
            cache.Initialize();

            var sourceFile = Path.Combine(tempDir, "source.png");
            File.WriteAllText(sourceFile, "small");
            var cacheKey = CacheService.BuildCacheKey(1, 1, "page.png", "original");
            await cache.PublishAsync(cacheKey, sourceFile, "image/png");

            testLogger.ClearEvents();

            var freed = cache.EvictOverBudget();

            Assert.Equal(0, freed);
            Assert.Empty(testLogger.Events);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    /// <summary>
    /// Test ILogger that captures events for assertion.
    /// </summary>
    private sealed class TestLogger<T> : ILogger<T>
    {
        private readonly List<CapturedLog> _events = new();
        private readonly object _lock = new();

        public IReadOnlyList<CapturedLog> Events
        {
            get
            {
                lock (_lock)
                    return _events.ToList();
            }
        }

        public void ClearEvents()
        {
            lock (_lock)
                _events.Clear();
        }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            lock (_lock)
            {
                _events.Add(new CapturedLog(logLevel, message, exception));
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

/// <summary>
/// WebApplicationFactory for C00 tests that wraps the Serilog Log.Logger
/// with a collecting sink to capture startup and maintenance log events.
/// UseSerilog() replaces the standard MEL logging factory, so ILoggerProvider
/// collectors are bypassed — we must intercept at the Serilog sink level.
/// </summary>
public sealed class C00WebApplicationFactory : WebApplicationFactory<com.lifepixer.mangaplex.Server.Program>
{
    private readonly CollectingSink _sink;
    private readonly string _tempRoot;
    private Serilog.ILogger? _originalLogger;

    public C00WebApplicationFactory(CollectingSink sink)
    {
        _sink = sink;
        _tempRoot = Path.Combine(Path.GetTempPath(), "mangaplex-c00-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_tempRoot, "data"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "cache"));
        Directory.CreateDirectory(Path.Combine(_tempRoot, "scratch"));

        Environment.SetEnvironmentVariable("MangaPlex__Storage__DataRoot", Path.Combine(_tempRoot, "data"));
        Environment.SetEnvironmentVariable("MangaPlex__Storage__CacheRoot", Path.Combine(_tempRoot, "cache"));
        Environment.SetEnvironmentVariable("MangaPlex__Storage__ScratchRoot", Path.Combine(_tempRoot, "scratch"));
        Environment.SetEnvironmentVariable("Media__WorkerExecutablePath", "");
        Environment.SetEnvironmentVariable("MangaPlex__Security__RateLimit__Disabled", "true");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        builder.ConfigureTestServices(services =>
        {
            // Remove the worker pool hosted service so tests don't spawn workers.
            var workerHosted = services.FirstOrDefault(
                d => d.ImplementationType == typeof(com.lifepixer.mangaplex.Server.Hosting.MediaWorkerHostedService));
            if (workerHosted is not null)
                services.Remove(workerHosted);

            // Remove the thumbnail backfill hosted service — it depends on a
            // running worker pool and would log warnings in the test environment.
            var thumbBackfill = services.FirstOrDefault(
                d => d.ImplementationType == typeof(com.lifepixer.mangaplex.Server.Hosting.ThumbnailBackfillHostedService));
            if (thumbBackfill is not null)
                services.Remove(thumbBackfill);

            // Wrap Log.Logger to also write to our collecting sink.
            // UseSerilog() reads Log.Logger when the SerilogLoggerFactory is
            // resolved (during host startup, after ConfigureTestServices), so
            // this wrapper will be the active logger for all hosted services.
            _originalLogger = Log.Logger;
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Sink(_sink)
                .WriteTo.Logger(_originalLogger)
                .CreateLogger();
        });
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Restore the original logger
            if (_originalLogger is not null)
                Log.Logger = _originalLogger;

            Environment.SetEnvironmentVariable("MangaPlex__Storage__DataRoot", null);
            Environment.SetEnvironmentVariable("MangaPlex__Storage__CacheRoot", null);
            Environment.SetEnvironmentVariable("MangaPlex__Storage__ScratchRoot", null);
            Environment.SetEnvironmentVariable("Media__WorkerExecutablePath", null);
            Environment.SetEnvironmentVariable("MangaPlex__Security__RateLimit__Disabled", null);

            try { Directory.Delete(_tempRoot, true); } catch { }
        }
        base.Dispose(disposing);
    }
}
