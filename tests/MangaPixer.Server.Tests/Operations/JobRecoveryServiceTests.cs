namespace com.lifepixer.mangapixer.Tests.Server.Operations;

using com.lifepixer.mangapixer.Server.Operations;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

/// <summary>
/// Tests for JobRecoveryService — interrupted job recovery, analysis
/// recovery, and schema validation.
/// </summary>
public sealed class JobRecoveryServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _dbPath;
    private readonly DbContextOptions<MangaPixerDbContext> _options;

    public JobRecoveryServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "mangapixer-recovery-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _dbPath = Path.Combine(_tempDir, "test.db");
        var connectionString = DatabaseInitialization.BuildConnectionString(_dbPath);
        _options = new DbContextOptionsBuilder<MangaPixerDbContext>()
            .UseSqlite(connectionString)
            .Options;
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); } catch { }
    }

    private async Task<(MangaPixerDbContext db, JobRecoveryService service)> SetupAsync()
    {
        var db = new MangaPixerDbContext(_options);
        await db.Database.EnsureCreatedAsync();
        await DatabaseInitialization.ConfigureDatabaseAsync(db);
        var service = new JobRecoveryService(db);
        return (db, service);
    }

    [Fact]
    public async Task RecoverInterruptedJobs_MarksPendingAsFailed()
    {
        var (db, service) = await SetupAsync();
        try
        {
            db.Jobs.Add(new JobEntity
            {
                JobType = "scan",
                Status = 0, // pending
                QueuedAt = DateTimeOffset.UtcNow,
            });
            db.Jobs.Add(new JobEntity
            {
                JobType = "analyze",
                Status = 1, // running
                QueuedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var recovered = await service.RecoverInterruptedJobsAsync();

            Assert.Equal(2, recovered);
            var jobs = await db.Jobs.ToListAsync();
            Assert.All(jobs, j => Assert.Equal(3, j.Status)); // all failed
            Assert.All(jobs, j => Assert.NotNull(j.SanitizedError));
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task RecoverInterruptedJobs_LeavesCompletedJobsAlone()
    {
        var (db, service) = await SetupAsync();
        try
        {
            db.Jobs.Add(new JobEntity
            {
                JobType = "scan",
                Status = 2, // completed
                QueuedAt = DateTimeOffset.UtcNow,
                CompletedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();

            var recovered = await service.RecoverInterruptedJobsAsync();

            Assert.Equal(0, recovered);
            var job = await db.Jobs.FirstAsync();
            Assert.Equal(2, job.Status); // still completed
        }
        finally { await db.DisposeAsync(); }
    }

    [Fact]
    public async Task ValidateSchema_ReturnsValidForCurrentVersion()
    {
        var (db, service) = await SetupAsync();
        try
        {
            var result = await service.ValidateSchemaAsync();
            Assert.True(result.Valid);
            Assert.NotNull(result.Version);
        }
        finally { await db.DisposeAsync(); }
    }
}
