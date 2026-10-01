namespace com.lifepixer.mangapixer.Server.Scanning;

using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>
/// Tombstoned nodes the trash must not purge yet, whatever their age (1.31.0). The contract between cross-library move
/// recognition, which holds a tombstone while it still has work to do (an open move conflict an admin has not resolved, a
/// pairing waiting for the new copy's analysis), and the trash, which purges tombstones past the retention window
/// (<see cref="com.lifepixer.mangapixer.Core.Catalog.TrashRetention"/>) except the held ones. Returned as a query so the trash
/// can compose it into one set-based statement.
/// </summary>
public interface ITombstoneHolds
{
    /// <summary>Ids of tombstoned nodes held back from the trash (a node id may appear more than once).</summary>
    IQueryable<long> HeldNodeIds();
}

/// <summary>Holds nothing - the registration until move recognition provides its own.</summary>
public sealed class NoTombstoneHolds : ITombstoneHolds
{
    private readonly MangaPixerDbContext _db;

    public NoTombstoneHolds(MangaPixerDbContext db) => _db = db;

    public IQueryable<long> HeldNodeIds() => _db.CatalogNodes.Where(n => false).Select(n => n.Id);
}
