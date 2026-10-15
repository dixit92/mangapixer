namespace com.lifepixer.mangapixer.Server.Features.Trackers;

/// <summary>Whether a read mark was created or removed.</summary>
public enum ReadMarkChangeKind
{
    Added = 0,
    Removed = 1,
}

/// <summary>
/// One committed change to <c>read_marks</c> (1.43.0, the tracker seam). <see cref="Source"/> is the mark's own source:
/// <c>completion</c> (finished in the reader), <c>manual</c> (marked read by hand), <c>bulk</c> (a folder marked read),
/// <c>import</c> (YACReader import), <c>upgrade</c> (carried over to a volume file that replaced its chapters) - for a removal,
/// the source of the mark that was removed.
/// </summary>
public sealed record ReadMarkChange(ReadMarkChangeKind Kind, long UserId, long ItemId, string Source, DateTimeOffset MarkedAt);

/// <summary>
/// Told about read marks after they are COMMITTED (1.43.0). Fed by <see cref="Persistence.ReadMarkChangeInterceptor"/> from every
/// tracked insert / delete of a <c>ReadMarkEntity</c>, whichever service made it, so no call site needs to remember it.
/// <para>
/// Contract: called on the saving thread right after a successful <c>SaveChanges</c>, once per save with every change of that
/// save; never for a save that failed (a first-write race that found the mark already there signals nothing - the retry that
/// succeeds signals its own rows). It must be fast and must not touch the database or the network (enqueue and return), and it
/// should not throw (a throw is logged and swallowed - the read mark is already saved). A change is a HINT: a save inside an
/// explicit transaction that is later rolled back still signals, so consumers recompute from committed state.
/// </para>
/// <para>
/// Not signalled: bulk deletes that bypass the change tracker - <c>ExecuteDelete</c> on user delete
/// (<c>AdminController.DeleteUser</c>) and on trash purge (<c>NodePurger</c>). Neither may change a tracker (trackers are never
/// lowered automatically); user delete cleans the tracker tables itself.
/// </para>
/// </summary>
public interface IReadMarkObserver
{
    void OnReadMarksChanged(IReadOnlyList<ReadMarkChange> changes);
}

/// <summary>The default observer until tracker sync registers its own: does nothing.</summary>
public sealed class NoReadMarkObserver : IReadMarkObserver
{
    public void OnReadMarksChanged(IReadOnlyList<ReadMarkChange> changes)
    {
    }
}
