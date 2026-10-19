namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The new-chapter freshness tables (1.45.0 lane R, migration <c>AddMangaDexLatestUpload</c>), configured from
/// <see cref="MangaPixerDbContext"/>'s <c>OnModelCreating</c>. Additive only.
/// </summary>
internal static class FreshnessModel
{
    /// <summary>A MangaDex id (a UUID: 36 characters).</summary>
    public const int MangaDexIdLength = 36;

    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<MangaDexLatestUploadEntity>(e =>
        {
            e.ToTable("mangadex_latest_uploads");
            e.HasKey(x => x.RecordId);
            e.Property(x => x.RecordId).ValueGeneratedNever();
            e.Property(x => x.ChapterId).HasMaxLength(MangaDexIdLength);
            e.HasOne<MetadataRecordEntity>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<LibraryFreshnessEntity>(e =>
        {
            e.ToTable("library_freshness");
            e.HasKey(x => x.LibraryId);
            e.Property(x => x.LibraryId).ValueGeneratedNever();
            e.HasOne<LibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<FreshnessStateEntity>(e =>
        {
            e.ToTable("freshness_state");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.SweepOutcome).HasMaxLength(32);
            e.Property(x => x.FeedOutcome).HasMaxLength(32);
        });

        mb.Entity<MetadataRefreshNudgeEntity>(e =>
        {
            e.ToTable("metadata_refresh_nudges");
            e.HasKey(x => x.RecordId);
            e.Property(x => x.RecordId).ValueGeneratedNever();
            e.HasOne<MetadataRecordEntity>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
