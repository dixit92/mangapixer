namespace com.lifepixer.mangapixer.Server.Persistence;

using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// The ratings table (1.52.0 lane R, migration <c>AddUserRatings</c>), configured from <see cref="MangaPixerDbContext"/>'s
/// <c>OnModelCreating</c>. Additive only.
/// </summary>
internal static class RatingModel
{
    /// <summary>The score's range check, also enforced by the API (400 outside it).</summary>
    public const string ScoreCheckName = "CK_user_ratings_Score";

    public static void Configure(ModelBuilder mb)
    {
        mb.Entity<UserRatingEntity>(e =>
        {
            e.ToTable("user_ratings", t => t.HasCheckConstraint(ScoreCheckName,
                $"\"Score\" >= {UserRatingEntity.MinScore} AND \"Score\" <= {UserRatingEntity.MaxScore}"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedOnAdd();

            // One rating per (user, node) - PUT is an upsert.
            e.HasIndex(x => new { x.UserId, x.CatalogNodeId }).IsUnique();
            // The "My rating" sorts and the rating: search term read a user's ratings by score.
            e.HasIndex(x => new { x.UserId, x.Score });
            // The node-side cascade (a scan removing a node) finds its ratings without a table scan.
            e.HasIndex(x => x.CatalogNodeId);

            e.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Node)
                .WithMany()
                .HasForeignKey(x => x.CatalogNodeId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
