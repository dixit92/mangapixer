namespace com.lifepixer.mangapixer.Server.Features.Metadata.AutoMatch;

using com.lifepixer.mangapixer.Core.Metadata.AutoMatch;
using com.lifepixer.mangapixer.Server.Persistence;

/// <summary>
/// Gathers a work's local comics signs into <see cref="MatchContext.Comics"/> before any lookup (1.32.0 step 0). Local data
/// only - display names, the declaration, stored ComicInfo and page counts - nothing is sent to compute it. Step 0 (stub)
/// reads the declared type and the category folder; lane A (comics signals) adds the stored ComicInfo publisher / web links /
/// notes and the page-count shape of <paramref name="archiveIds"/>, and the detectors in <see cref="ComicsSignals"/>.
/// </summary>
public static class ComicsSignalReader
{
    public static Task<MatchQuery> ApplyAsync(
        MangaPixerDbContext db, MatchQuery query, FolderShape shape, IReadOnlyList<long> archiveIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(shape);
        ArgumentNullException.ThrowIfNull(archiveIds);
        ct.ThrowIfCancellationRequested();
        var signal = ComicsSignals.Of(new ComicsSignalInput(query.Context.DeclaredType, shape.CategoryHint, shape.ArchiveNames));
        return Task.FromResult(signal.Kinds == ComicsSignalKind.None
            ? query
            : query with { Context = query.Context with { Comics = signal } });
    }
}
