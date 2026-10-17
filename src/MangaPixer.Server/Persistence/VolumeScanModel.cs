namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The volume contents scan tables (1.44.0, migration <c>AddVolumeContentScan</c>), configured from <see cref="MangaPixerDbContext"/>'s
/// <c>OnModelCreating</c>. Kept in its own file so the tracker and volume-scan lanes never edit the context's configuration list.
/// </summary>
internal static class VolumeScanModel
{
    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<ArchivePageHashEntity>(e =>
        {
            e.ToTable("archive_page_hashes");
            e.HasKey(x => x.NodeId);
            e.Property(x => x.NodeId).ValueGeneratedNever();
            e.Property(x => x.Hashes).IsRequired();
            e.Property(x => x.Unhashed).IsRequired();
            e.Property(x => x.Error).HasMaxLength(64);
            e.HasOne<ArchiveItemEntity>().WithMany().HasForeignKey(x => x.NodeId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<VolumeContentEntity>(e =>
        {
            e.ToTable("volume_contents");
            e.HasKey(x => x.VolumeNodeId);
            e.Property(x => x.VolumeNodeId).ValueGeneratedNever();
            e.Property(x => x.ChaptersJson).IsRequired();
            e.Property(x => x.CandidatesKey).HasMaxLength(64).IsRequired();
            e.Property(x => x.Error).HasMaxLength(64);
            e.HasIndex(x => x.FolderId);
            e.HasOne<ArchiveItemEntity>().WithMany().HasForeignKey(x => x.VolumeNodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CatalogNodeEntity>().WithMany().HasForeignKey(x => x.FolderId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<VolumeScanStateEntity>(e =>
        {
            e.ToTable("volume_scan_state");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
        });
    }
}
