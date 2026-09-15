namespace com.lifepixer.mangaplex.Core.Reading;

/// <summary>
/// Derived, display-only read state of a folder, rolled up over every readable
/// descendant archive for the current user (1.6.0 folder rollup). NOT a stored
/// flag: computed at browse time from the same two per-item signals the archive
/// cards already show (the sticky read-mark and in-progress reading progress), so
/// a folder never contradicts the items inside it.
/// </summary>
public enum FolderReadRollup
{
    /// <summary>No descendant archive is read or in progress.</summary>
    Unread = 0,

    /// <summary>Partially read: at least one descendant is read or in progress, but not all are read.</summary>
    Reading = 1,

    /// <summary>Every readable descendant archive carries a read-mark.</summary>
    Read = 2,
}

/// <summary>
/// Pure classification rule behind <see cref="FolderReadRollup"/>. Kept in Core so
/// the tri-state decision is unit-testable and shared by any surface that needs it
/// (the browse aggregate query only produces the three counts).
/// </summary>
public static class FolderReadRollupRules
{
    /// <summary>
    /// Classifies a folder from its descendant-archive counts.
    /// </summary>
    /// <param name="total">Readable (non-tombstoned) descendant archives.</param>
    /// <param name="read">Of those, how many carry the user's sticky read-mark.</param>
    /// <param name="inProgress">Of the NOT-read ones, how many have in-progress reading progress.</param>
    /// <returns>
    /// <c>null</c> when the folder has nothing to roll up (no readable descendant
    /// archives) - an empty folder is neither read nor unread and renders no badge,
    /// which also avoids the vacuous "all zero of zero are read" trap.
    /// </returns>
    public static FolderReadRollup? Classify(int total, int read, int inProgress)
    {
        if (total <= 0)
            return null;
        if (read >= total)
            return FolderReadRollup.Read;
        if (read > 0 || inProgress > 0)
            return FolderReadRollup.Reading;
        return FolderReadRollup.Unread;
    }
}
