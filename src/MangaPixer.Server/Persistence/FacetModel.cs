namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Core.Metadata.Facets;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The tag index and canonical creator tables (1.45.0 seam I0, migration <c>AddFacetIndex</c>; D1 2.2 / 2.3), configured from
/// <see cref="MangaPixerDbContext"/>'s <c>OnModelCreating</c>. Kept in its own file so the facet lanes never edit the context's
/// configuration list. Deletes: a library, node, record or value going takes its derived rows with it; creators are never deleted
/// by a rebuild (stable ids), and an admin override keeps its creator (restrict).
/// </summary>
internal static class FacetModel
{
    public const int NameLength = 256;
    public const int NameKeyLength = 256;
    public const int SubjectLength = 300;
    public const int ValueKeyLength = 300;
    public const int TitleLength = 512;
    public const int InputsKeyLength = 128;

    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<CreatorEntity>(e =>
        {
            e.ToTable("creators");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.PublicId).IsRequired().HasMaxLength(FacetPublicIds.MaxLength);
            e.Property(x => x.DisplayName).IsRequired().HasMaxLength(NameLength);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => x.MergedIntoId);
            e.HasOne<CreatorEntity>().WithMany().HasForeignKey(x => x.MergedIntoId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<CreatorNameEntity>(e =>
        {
            e.ToTable("creator_names");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Name).IsRequired().HasMaxLength(NameLength);
            e.Property(x => x.NameKey).IsRequired().HasMaxLength(NameKeyLength);
            e.HasIndex(x => new { x.CreatorId, x.Name }).IsUnique();
            e.HasIndex(x => x.NameKey);
            e.HasOne<CreatorEntity>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<CreatorProviderIdEntity>(e =>
        {
            e.ToTable("creator_provider_ids");
            e.HasKey(x => new { x.Provider, x.ExternalId });
            e.Property(x => x.Provider).IsRequired().HasMaxLength(32);
            e.Property(x => x.ExternalId).IsRequired().HasMaxLength(64);
            e.HasIndex(x => x.CreatorId);
            e.HasOne<CreatorEntity>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<CreatorOverrideEntity>(e =>
        {
            e.ToTable("creator_overrides");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.Subject).IsRequired().HasMaxLength(SubjectLength);
            e.Property(x => x.Value).HasMaxLength(NameLength);
            e.HasIndex(x => new { x.Kind, x.Subject });
            e.HasIndex(x => x.TargetCreatorId);
            e.HasOne<CreatorEntity>().WithMany().HasForeignKey(x => x.TargetCreatorId).OnDelete(DeleteBehavior.Restrict);
        });

        mb.Entity<FacetWorkEntity>(e =>
        {
            e.ToTable("facet_works");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.WorkKey).IsRequired().HasMaxLength(FacetWorkKey.MaxLength);
            e.Property(x => x.Title).IsRequired().HasMaxLength(TitleLength);
            e.Property(x => x.SortKey).IsRequired();
            e.HasIndex(x => x.WorkKey).IsUnique();
            e.HasIndex(x => x.RecordId);
            e.HasOne<MetadataRecordEntity>().WithMany().HasForeignKey(x => x.RecordId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<FacetValueEntity>(e =>
        {
            e.ToTable("facet_values");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.Property(x => x.PublicId).IsRequired().HasMaxLength(FacetPublicIds.MaxLength);
            e.Property(x => x.ValueKey).IsRequired().HasMaxLength(ValueKeyLength);
            e.Property(x => x.Display).IsRequired().HasMaxLength(NameLength);
            e.HasIndex(x => x.PublicId).IsUnique();
            e.HasIndex(x => new { x.Bucket, x.ValueKey }).IsUnique();
            e.HasIndex(x => x.CreatorId);
            e.HasOne<CreatorEntity>().WithMany().HasForeignKey(x => x.CreatorId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<FacetEntryEntity>(e =>
        {
            e.ToTable("facet_entries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();
            e.HasIndex(x => new { x.ValueId, x.LocationNodeId }).IsUnique();
            // Covering index of the per-viewer count (D1 2.5): values x visible libraries -> distinct works.
            e.HasIndex(x => new { x.ValueId, x.LibraryId, x.WorkId });
            e.HasIndex(x => x.LocationNodeId);
            e.HasIndex(x => x.WorkId);
            e.HasIndex(x => x.LibraryId);
            e.HasOne<FacetWorkEntity>().WithMany().HasForeignKey(x => x.WorkId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<CatalogNodeEntity>().WithMany().HasForeignKey(x => x.LocationNodeId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<LibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FacetValueEntity>().WithMany().HasForeignKey(x => x.ValueId).OnDelete(DeleteBehavior.Cascade);
        });

        mb.Entity<FacetIndexLibraryEntity>(e =>
        {
            e.ToTable("facet_index_libraries");
            e.HasKey(x => x.LibraryId);
            e.Property(x => x.LibraryId).ValueGeneratedNever();
            e.Property(x => x.InputsKey).IsRequired().HasMaxLength(InputsKeyLength);
            e.HasOne<LibraryEntity>().WithMany().HasForeignKey(x => x.LibraryId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
