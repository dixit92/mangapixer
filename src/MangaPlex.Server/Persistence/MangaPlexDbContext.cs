namespace com.lifepixer.mangaplex.Server.Persistence;

using com.lifepixer.mangaplex.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

/// <summary>
/// EF Core DbContext for MangaPlex. Uses SQLite with WAL mode, foreign keys,
/// and FTS5 trigram search index.
///
/// Concurrency rules:
/// - Do not hold DbContext across threads/jobs.
/// - No lazy loading.
/// - Page every list query.
/// - Project only needed columns.
/// - No filesystem I/O inside write transactions.
/// </summary>
public sealed class MangaPlexDbContext : DbContext
{
    public MangaPlexDbContext(DbContextOptions<MangaPlexDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Configures model conventions. Every <see cref="DateTimeOffset"/> property
    /// is stored as a comparable <c>long</c> (binary representation) so that
    /// EF Core SQLite can translate <c>OrderBy</c>/<c>Where</c> comparisons on
    /// DateTimeOffset columns server-side. Without this, queries like
    /// <c>s.ExpiresAt &lt; now</c> throw <c>InvalidOperationException</c>
    /// during SQL translation (audit defect D26).
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    public DbSet<LibraryEntity> Libraries => Set<LibraryEntity>();
    public DbSet<LibraryGrantEntity> LibraryGrants => Set<LibraryGrantEntity>();
    public DbSet<CatalogNodeEntity> CatalogNodes => Set<CatalogNodeEntity>();
    public DbSet<ArchiveItemEntity> ArchiveItems => Set<ArchiveItemEntity>();
    public DbSet<PageEntryEntity> PageEntries => Set<PageEntryEntity>();
    public DbSet<ReadingProgressEntity> ReadingProgress => Set<ReadingProgressEntity>();
    public DbSet<ReaderPreferencesEntity> ReaderPreferences => Set<ReaderPreferencesEntity>();
    public DbSet<ItemReaderOverridesEntity> ItemReaderOverrides => Set<ItemReaderOverridesEntity>();
    public DbSet<BookmarkEntity> Bookmarks => Set<BookmarkEntity>();
    public DbSet<JobEntity> Jobs => Set<JobEntity>();
    public DbSet<ScanRunEntity> ScanRuns => Set<ScanRunEntity>();
    public DbSet<ScanObservationEntity> ScanObservations => Set<ScanObservationEntity>();
    public DbSet<CacheEntryEntity> CacheEntries => Set<CacheEntryEntity>();
    public DbSet<AuditEventEntity> AuditEvents => Set<AuditEventEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<SessionEntity> Sessions => Set<SessionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUsers(modelBuilder);
        ConfigureSessions(modelBuilder);
        ConfigureLibraries(modelBuilder);
        ConfigureCatalogNodes(modelBuilder);
        ConfigureArchiveItems(modelBuilder);
        ConfigurePageEntries(modelBuilder);
        ConfigureReadingProgress(modelBuilder);
        ConfigurePreferences(modelBuilder);
        ConfigureBookmarks(modelBuilder);
        ConfigureJobs(modelBuilder);
        ConfigureCacheEntries(modelBuilder);
        ConfigureAuditEvents(modelBuilder);
    }

    private static void ConfigureUsers(ModelBuilder mb)
    {
        mb.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.PublicId).IsRequired().HasMaxLength(64);
            e.Property(x => x.UserName).IsRequired().HasMaxLength(64);
            e.Property(x => x.NormalizedUserName).IsRequired().HasMaxLength(64);
            e.Property(x => x.PasswordHash).IsRequired();
            e.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(64);
            e.HasIndex(x => x.NormalizedUserName).IsUnique();
            e.HasIndex(x => x.PublicId).IsUnique();
        });
    }

    private static void ConfigureSessions(ModelBuilder mb)
    {
        mb.Entity<SessionEntity>(e =>
        {
            e.ToTable("sessions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.TicketId).IsRequired().HasMaxLength(128);
            e.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(64);
            e.HasIndex(x => x.TicketId).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
        });
    }

    private static void ConfigureLibraries(ModelBuilder mb)
    {
        mb.Entity<LibraryEntity>(e =>
        {
            e.ToTable("libraries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.PublicId).IsRequired().HasMaxLength(64);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(256);
            e.Property(x => x.RootPath).IsRequired();
            e.Property(x => x.CaseComparisonPolicy).IsRequired().HasMaxLength(32);
            e.Property(x => x.State).IsRequired().HasMaxLength(32);
            e.HasIndex(x => x.PublicId).IsUnique();
        });

        mb.Entity<LibraryGrantEntity>(e =>
        {
            e.ToTable("library_grants");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.LibraryId }).IsUnique();
            e.HasIndex(x => x.LibraryId);
        });
    }

    private static void ConfigureCatalogNodes(ModelBuilder mb)
    {
        mb.Entity<CatalogNodeEntity>(e =>
        {
            e.ToTable("catalog_nodes");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.PublicId).IsRequired().HasMaxLength(64);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(512);
            e.Property(x => x.RelativePath).IsRequired();
            e.Property(x => x.PathKey).IsRequired().HasMaxLength(1024);
            e.Property(x => x.SortKey).IsRequired();

            // Unique path within a library
            e.HasIndex(x => new { x.LibraryId, x.PathKey }).IsUnique();

            // Browsing indexes
            e.HasIndex(x => new { x.ParentId, x.Kind, x.SortKey });
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => x.LibraryId);

            // Self-referencing parent
            e.HasOne(x => x.Parent)
                .WithMany()
                .HasForeignKey(x => x.ParentId)
                .OnDelete(DeleteBehavior.Restrict);

            e.HasOne(x => x.Library)
                .WithMany(l => l.Nodes)
                .HasForeignKey(x => x.LibraryId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureArchiveItems(ModelBuilder mb)
    {
        mb.Entity<ArchiveItemEntity>(e =>
        {
            e.ToTable("archive_items");
            e.HasKey(x => x.NodeId);
            e.Property(x => x.AnalysisError).HasMaxLength(1024);
            e.Property(x => x.StrongHash).HasMaxLength(128);

            e.HasOne(x => x.Node)
                .WithOne(n => n.ArchiveItem)
                .HasForeignKey<ArchiveItemEntity>(x => x.NodeId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasIndex(x => x.AnalysisState);
            e.HasIndex(x => x.ContentVersion);
        });
    }

    private static void ConfigurePageEntries(ModelBuilder mb)
    {
        mb.Entity<PageEntryEntity>(e =>
        {
            e.ToTable("page_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EntryKey).IsRequired().HasMaxLength(64);
            e.Property(x => x.SourceEntryLocator).IsRequired();
            e.Property(x => x.MediaType).IsRequired().HasMaxLength(64);

            // Unique ordinal within an item+content version
            e.HasIndex(x => new { x.ItemId, x.ContentVersion, x.Ordinal }).IsUnique();
            e.HasIndex(x => new { x.ItemId, x.ContentVersion, x.EntryKey }).IsUnique();
            e.HasIndex(x => x.ItemId);

            e.HasOne(x => x.Item)
                .WithMany(i => i.Pages)
                .HasForeignKey(x => x.ItemId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }

    private static void ConfigureReadingProgress(ModelBuilder mb)
    {
        mb.Entity<ReadingProgressEntity>(e =>
        {
            e.ToTable("reading_progress");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EntryKey).IsRequired().HasMaxLength(64);

            // One progress record per user+item
            e.HasIndex(x => new { x.UserId, x.ItemId }).IsUnique();
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
        });
    }

    private static void ConfigurePreferences(ModelBuilder mb)
    {
        mb.Entity<ReaderPreferencesEntity>(e =>
        {
            e.ToTable("reader_preferences");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.UserId).IsUnique();
        });

        mb.Entity<ItemReaderOverridesEntity>(e =>
        {
            e.ToTable("item_reader_overrides");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.ItemId }).IsUnique();
        });
    }

    private static void ConfigureBookmarks(ModelBuilder mb)
    {
        mb.Entity<BookmarkEntity>(e =>
        {
            e.ToTable("bookmarks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.EntryKey).IsRequired().HasMaxLength(64);
            e.Property(x => x.Label).HasMaxLength(256);
            e.HasIndex(x => new { x.UserId, x.ItemId });
        });
    }

    private static void ConfigureJobs(ModelBuilder mb)
    {
        mb.Entity<JobEntity>(e =>
        {
            e.ToTable("jobs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.JobType).IsRequired().HasMaxLength(64);
            e.Property(x => x.LeaseOwner).HasMaxLength(128);
            e.Property(x => x.SanitizedError).HasMaxLength(1024);
            e.HasIndex(x => new { x.Status, x.LeaseExpiry });
            e.HasIndex(x => x.LibraryId);
        });

        mb.Entity<ScanRunEntity>(e =>
        {
            e.ToTable("scan_runs");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => x.LibraryId);
            e.HasIndex(x => x.Status);
        });

        mb.Entity<ScanObservationEntity>(e =>
        {
            e.ToTable("scan_observations");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.RelativePath).IsRequired();
            e.Property(x => x.PathKey).IsRequired().HasMaxLength(1024);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(512);
            e.HasIndex(x => x.ScanRunId);
            e.HasIndex(x => new { x.LibraryId, x.PathKey });
        });
    }

    private static void ConfigureCacheEntries(ModelBuilder mb)
    {
        mb.Entity<CacheEntryEntity>(e =>
        {
            e.ToTable("cache_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.CacheKey).IsRequired().HasMaxLength(256);
            e.Property(x => x.CacheFilePath).IsRequired();
            e.Property(x => x.MediaType).IsRequired().HasMaxLength(64);
            e.HasIndex(x => x.CacheKey).IsUnique();
            e.HasIndex(x => new { x.State, x.LastAccessedAt });
        });
    }

    private static void ConfigureAuditEvents(ModelBuilder mb)
    {
        mb.Entity<AuditEventEntity>(e =>
        {
            e.ToTable("audit_events");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Action).IsRequired().HasMaxLength(64);
            e.Property(x => x.Result).IsRequired().HasMaxLength(32);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.ActorUserId);
        });
    }
}
