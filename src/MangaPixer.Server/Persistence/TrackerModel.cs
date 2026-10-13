namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The tracker sync tables (1.43.0), configured from <see cref="MangaPixerDbContext"/>'s <c>OnModelCreating</c>. Kept in its own file
/// so the tracker and volume-scan lanes never edit the context's configuration list. Migration <c>AddTrackerSync</c> (lane E).
/// </summary>
internal static class TrackerModel
{
    public const int SlugLength = 32;
    public const int ProtectedLength = 8192;

    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<TrackerSettingsEntity>(e =>
        {
            e.ToTable("tracker_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Tracker).HasMaxLength(SlugLength).IsRequired();
            e.Property(x => x.ClientId).HasMaxLength(256);
            e.Property(x => x.ClientSecretProtected).HasMaxLength(ProtectedLength);
            e.Property(x => x.RedirectUri).HasMaxLength(1024);
            e.HasIndex(x => x.Tracker).IsUnique();
        });

        mb.Entity<TrackerAccountEntity>(e =>
        {
            e.ToTable("tracker_accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Tracker).HasMaxLength(SlugLength).IsRequired();
            e.Property(x => x.RemoteUserId).HasMaxLength(128);
            e.Property(x => x.DisplayName).HasMaxLength(256);
            e.Property(x => x.AccessTokenProtected).HasMaxLength(ProtectedLength);
            e.Property(x => x.RefreshTokenProtected).HasMaxLength(ProtectedLength);
            e.Property(x => x.LastFailureCode).HasMaxLength(32);
            e.Property(x => x.RequestsDay).HasMaxLength(10);
            e.HasIndex(x => new { x.UserId, x.Tracker }).IsUnique();
            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<TrackerEntryEntity>(e =>
        {
            e.ToTable("tracker_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Tracker).HasMaxLength(SlugLength).IsRequired();
            e.Property(x => x.RemoteId).HasMaxLength(64);
            e.Property(x => x.ErrorCode).HasMaxLength(32);
            e.HasIndex(x => new { x.UserId, x.Tracker, x.RecordId }).IsUnique();
            e.HasIndex(x => x.RecordId);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MetadataRecordEntity>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<TrackerRetryEntity>(e =>
        {
            e.ToTable("tracker_retries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Tracker).HasMaxLength(SlugLength).IsRequired();
            e.HasIndex(x => new { x.UserId, x.Tracker, x.RecordId }).IsUnique();
            e.HasIndex(x => x.NotBefore);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<MetadataRecordEntity>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<TrackerNodeOverrideEntity>(e =>
        {
            e.ToTable("tracker_node_overrides");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.UserId, x.NodeId }).IsUnique();
            e.HasIndex(x => x.NodeId);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CatalogNodeEntity>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<TrackerIdOverrideEntity>(e =>
        {
            e.ToTable("tracker_id_overrides");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Tracker).HasMaxLength(SlugLength).IsRequired();
            e.Property(x => x.RemoteId).HasMaxLength(64).IsRequired();
            e.HasIndex(x => new { x.RecordId, x.Tracker }).IsUnique();
            e.HasOne<MetadataRecordEntity>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<TrackerOAuthStateEntity>(e =>
        {
            e.ToTable("tracker_oauth_states");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Tracker).HasMaxLength(SlugLength).IsRequired();
            e.Property(x => x.StateHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.SessionHash).HasMaxLength(64).IsRequired();
            e.Property(x => x.VerifierProtected).HasMaxLength(ProtectedLength).IsRequired();
            e.HasIndex(x => x.StateHash).IsUnique();
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ExpiresAt);
            e.HasOne<UserEntity>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
