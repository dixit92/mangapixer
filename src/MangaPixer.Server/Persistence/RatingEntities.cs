namespace com.lifepixer.mangapixer.Server.Persistence.Entities;

// 1.52.0 ratings (lane R, migration AddUserRatings). Configured in Persistence/RatingModel.cs. Ids, a score and a time only.

/// <summary>
/// One user's own rating of one catalog node (a folder or an archive - one node id space, like <see cref="FavoriteEntity"/>). Stored as
/// an integer 1-10 (owner decision 5a: shown as 5 stars with halves, so 7 = 3.5 stars; 1-10 keeps a later tracker sync lossless).
/// Per user and private: no other user, admin view, export or log line ever shows it. No rollup - rating a series rates the folder
/// only. Unique (UserId, CatalogNodeId); cascade-deleted with the node and with the user, the same lifetime as a favorite.
/// </summary>
public sealed class UserRatingEntity
{
    /// <summary>The lowest stored score (half a star).</summary>
    public const int MinScore = 1;

    /// <summary>The highest stored score (five stars).</summary>
    public const int MaxScore = 10;

    public long Id { get; set; }
    public long UserId { get; set; }
    public long CatalogNodeId { get; set; }

    /// <summary>The score, 1-10 (a CHECK constraint holds the range in the database too).</summary>
    public int Score { get; set; }

    /// <summary>When the user last set or changed the score (UTC).</summary>
    public DateTimeOffset RatedAt { get; set; }

    public UserEntity? User { get; set; }
    public CatalogNodeEntity? Node { get; set; }
}
