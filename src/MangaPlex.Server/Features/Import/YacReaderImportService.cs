namespace com.lifepixer.mangaplex.Server.Features.Import.YacReader;

using com.lifepixer.mangaplex.Core.Api;
using com.lifepixer.mangaplex.Core.Catalog;
using com.lifepixer.mangaplex.Core.Reading;
using com.lifepixer.mangaplex.Server.Logging;
using com.lifepixer.mangaplex.Server.Persistence;
using com.lifepixer.mangaplex.Server.Persistence.Entities;
using com.lifepixer.mangaplex.Server.Storage;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Maps YACReader reading progress into MangaPlex for a chosen target user.
///
/// Design constraints (project plan §14.3 / feature backlog §2):
/// - Read-only against the source library: the ydb is opened read-only, or
///   (default) copied to an app-owned scratch snapshot first so the source
///   directory is never written to (no journal/wal/shm sidecars).
/// - Library-to-library mapping: each YACReader library corresponds to one
///   MangaPlex library sharing the same root directory; comics are matched to
///   MangaPlex archive catalog nodes by relative path.
/// - Dry-run first: <see cref="PreviewAsync"/> writes nothing.
/// - Never overwrite existing MangaPlex state without explicit opt-in
///   (<see cref="YacReaderImportRequest.Overwrite"/>).
/// - Privacy: source paths are never persisted or echoed; logs carry only
///   IDs, counts, and sanitized error codes.
/// </summary>
public sealed class YacReaderImportService
{
    private readonly MangaPlexDbContext _db;
    private readonly YacReaderLibraryReader _reader;
    private readonly AppRootOptions _appRoot;
    private readonly ILogger<YacReaderImportService> _logger;

    private const int PreviewSampleLimit = 50;

    public YacReaderImportService(
        MangaPlexDbContext db,
        YacReaderLibraryReader reader,
        AppRootOptions appRoot,
        ILogger<YacReaderImportService> logger)
    {
        _db = db;
        _reader = reader;
        _appRoot = appRoot;
        _logger = logger;
    }

    /// <summary>
    /// Produces a dry-run preview. Resolves the library and target user,
    /// reads the ydb (snapshot or read-only), maps comics to MangaPlex nodes,
    /// and reports counts + a bounded sample. Writes nothing.
    /// </summary>
    public async Task<YacReaderImportResult<YacReaderImportPreviewDto>> PreviewAsync(
        YacReaderImportRequest request, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(request, ct);
        if (resolved.Error is not null)
            return YacReaderImportResult<YacReaderImportPreviewDto>.Fail(resolved.Error.Value.Code, resolved.Error.Value.Message);

        using var snapshot = await PrepareSnapshotAsync(request, ct);
        if (snapshot.Error is not null)
            return YacReaderImportResult<YacReaderImportPreviewDto>.Fail(snapshot.Error.Value.Code, snapshot.Error.Value.Message);

        var version = _reader.ReadVersion(snapshot.DbPath, ct);
        var comics = _reader.ReadComics(snapshot.DbPath, ct);

        var plan = await BuildPlanAsync(resolved.Library!, resolved.User!, comics, request.Overwrite, ct);

        var items = plan.MappedItems
            .Where(i => i.State != "unread")
            .Take(PreviewSampleLimit)
            .Select(i => new YacReaderImportItemDto
            {
                ItemId = i.Node is null ? null : OpaqueId.Encode(i.Node.Id),
                DisplayName = i.Node?.DisplayName,
                Read = i.Read,
                HasBeenOpened = i.HasBeenOpened,
                CurrentPage = i.CurrentPage,
                State = i.State,
                Conflict = i.Conflict,
            })
            .ToList();

        _logger.LogInformation(LogEvents.Administration.YacReaderImportPreviewed,
            "YACReader import previewed for library {LibraryId} user {UserId}: {Total} comics, {Mapped} mapped, {Conflicts} conflicts",
            resolved.Library!.Id, resolved.User!.Id, comics.Count, plan.Mapped, plan.Conflicts);

        return YacReaderImportResult<YacReaderImportPreviewDto>.Ok(new YacReaderImportPreviewDto
        {
            LibraryId = request.LibraryId,
            TargetUserId = request.TargetUserId,
            DbVersion = version,
            TotalComics = comics.Count,
            Mapped = plan.Mapped,
            Unmapped = plan.Unmapped,
            Conflicts = plan.Conflicts,
            ToImport = plan.ToImport,
            Items = items,
        });
    }

    /// <summary>
    /// Applies the import: writes reading progress and sticky read-marks for
    /// mapped comics with a non-unread state, respecting the overwrite policy.
    /// </summary>
    public async Task<YacReaderImportResult<YacReaderImportResultDto>> ApplyAsync(
        YacReaderImportRequest request, CancellationToken ct = default)
    {
        var resolved = await ResolveAsync(request, ct);
        if (resolved.Error is not null)
            return YacReaderImportResult<YacReaderImportResultDto>.Fail(resolved.Error.Value.Code, resolved.Error.Value.Message);

        using var snapshot = await PrepareSnapshotAsync(request, ct);
        if (snapshot.Error is not null)
            return YacReaderImportResult<YacReaderImportResultDto>.Fail(snapshot.Error.Value.Code, snapshot.Error.Value.Message);

        var version = _reader.ReadVersion(snapshot.DbPath, ct);
        var comics = _reader.ReadComics(snapshot.DbPath, ct);

        var plan = await BuildPlanAsync(resolved.Library!, resolved.User!, comics, request.Overwrite, ct);

        // Load existing progress + read marks for the mapped items in bulk so
        // the write transaction does no extra round-trips.
        var mappedNodes = plan.MappedItems.Where(i => i.Node is not null && i.State != "unread").ToList();
        var itemIds = mappedNodes.Select(i => i.Node!.Id).ToList();

        var existingProgress = itemIds.Count == 0
            ? new Dictionary<long, ReadingProgressEntity>()
            : await _db.ReadingProgress
                .Where(p => p.UserId == resolved.User!.Id && itemIds.Contains(p.ItemId))
                .ToDictionaryAsync(p => p.ItemId, ct);

        var existingMarks = itemIds.Count == 0
            ? new HashSet<long>()
            : (await _db.ReadMarks
                .Where(m => m.UserId == resolved.User!.Id && itemIds.Contains(m.ItemId))
                .Select(m => m.ItemId)
                .ToListAsync(ct)).ToHashSet();

        // Archive content versions for clamping/ContentVersion stamping.
        var contentVersions = itemIds.Count == 0
            ? new Dictionary<long, (long Version, int? PageCount)>()
            : await _db.ArchiveItems
                .Where(a => itemIds.Contains(a.NodeId))
                .ToDictionaryAsync(a => a.NodeId, a => (a.ContentVersion, a.PageCount), ct);

        int imported = 0;
        int skipped = 0;
        int readMarks = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var item in mappedNodes)
        {
            var nodeId = item.Node!.Id;
            var (contentVersion, pageCount) = contentVersions.GetValueOrDefault(nodeId, (0, null));
            var pageIndex = ClampPage(item.CurrentPage - 1, pageCount);

            var hasExisting = existingProgress.ContainsKey(nodeId);
            if (hasExisting && !request.Overwrite)
            {
                skipped++;
                continue;
            }

            var state = item.Read
                ? (int)ReadingState.Completed
                : (int)ReadingState.InProgress;

            var mutationId = $"yacreader-import:{item.ComicId}";

            if (hasExisting)
            {
                var progress = existingProgress[nodeId];
                progress.ContentVersion = contentVersion;
                progress.EntryKey = OpaqueId.Encode(pageIndex);
                progress.Ordinal = pageIndex;
                progress.NormalizedAnchor = 0.0;
                progress.State = state;
                progress.Revision++;
                progress.LastMutationId = mutationId;
                progress.UpdatedAt = now;
                if (item.Read && !progress.CompletedAt.HasValue)
                    progress.CompletedAt = now;
            }
            else
            {
                existingProgress[nodeId] = new ReadingProgressEntity
                {
                    UserId = resolved.User!.Id,
                    ItemId = nodeId,
                    ContentVersion = contentVersion,
                    EntryKey = OpaqueId.Encode(pageIndex),
                    Ordinal = pageIndex,
                    NormalizedAnchor = 0.0,
                    State = state,
                    Revision = 1,
                    LastMutationId = mutationId,
                    UpdatedAt = now,
                    CompletedAt = item.Read ? now : null,
                };
                _db.ReadingProgress.Add(existingProgress[nodeId]);
            }

            imported++;

            if (item.Read)
            {
                if (!existingMarks.Contains(nodeId))
                {
                    _db.ReadMarks.Add(new ReadMarkEntity
                    {
                        UserId = resolved.User!.Id,
                        ItemId = nodeId,
                        MarkedAt = now,
                        Source = "import",
                    });
                    existingMarks.Add(nodeId);
                }
                readMarks++;
            }
        }

        if (imported > 0)
            await _db.SaveChangesAsync(ct);

        _logger.LogInformation(LogEvents.Administration.YacReaderImportApplied,
            "YACReader import applied for library {LibraryId} user {UserId}: {Imported} imported, {Skipped} skipped, {ReadMarks} read marks, {Unmapped} unmapped",
            resolved.Library!.Id, resolved.User!.Id, imported, skipped, readMarks, plan.Unmapped);

        return YacReaderImportResult<YacReaderImportResultDto>.Ok(new YacReaderImportResultDto
        {
            LibraryId = request.LibraryId,
            TargetUserId = request.TargetUserId,
            DbVersion = version,
            TotalComics = comics.Count,
            Mapped = plan.Mapped,
            Unmapped = plan.Unmapped,
            Imported = imported,
            Skipped = skipped,
            ReadMarks = readMarks,
        });
    }

    private async Task<ResolvedImport> ResolveAsync(YacReaderImportRequest request, CancellationToken ct)
    {
        var library = await _db.Libraries.FirstOrDefaultAsync(l => l.PublicId == request.LibraryId, ct);
        if (library is null)
            return ResolvedImport.Fail("library_not_found", "Library not found.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.PublicId == request.TargetUserId, ct);
        if (user is null)
            return ResolvedImport.Fail("user_not_found", "Target user not found.");
        if (!user.IsActive)
            return ResolvedImport.Fail("user_inactive", "Target user is inactive.");

        return ResolvedImport.Ok(library, user);
    }

    private async Task<SnapshotResult> PrepareSnapshotAsync(YacReaderImportRequest request, CancellationToken ct)
    {
        var dbPath = ResolveDbPath(request.YacDbPath);
        if (dbPath is null)
            return SnapshotResult.Fail("yac_db_not_found", "YACReader library.ydb was not found at the given path.");

        if (!request.Snapshot)
            return SnapshotResult.Ok(dbPath, null);

        var scratchRoot = string.IsNullOrWhiteSpace(_appRoot.ScratchRoot)
            ? Path.Combine(AppContext.BaseDirectory, "scratch")
            : _appRoot.ScratchRoot;
        var importDir = Path.Combine(scratchRoot, "yacreader-import");
        Directory.CreateDirectory(importDir);
        var snapshotPath = Path.Combine(importDir, Guid.NewGuid().ToString("N") + ".ydb");

        try
        {
            File.Copy(dbPath, snapshotPath, overwrite: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(LogEvents.Administration.YacReaderImportFailed,
                "YACReader import snapshot copy failed: {Error}", ex.GetType().Name);
            return SnapshotResult.Fail("snapshot_failed", "Could not copy the YACReader database to a scratch snapshot.");
        }

        return SnapshotResult.Ok(snapshotPath, snapshotPath);
    }

    private static string? ResolveDbPath(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        try
        {
            var full = Path.GetFullPath(input);
            if (Directory.Exists(full))
            {
                // The admin may pass the library root (containing
                // .yacreaderlibrary/) or the .yacreaderlibrary directory itself.
                var direct = Path.Combine(full, "library.ydb");
                if (File.Exists(direct))
                    return direct;

                var inHidden = Path.Combine(full, ".yacreaderlibrary", "library.ydb");
                return File.Exists(inHidden) ? inHidden : null;
            }
            if (File.Exists(full))
                return full;
        }
        catch
        {
            // Invalid path characters, etc.
        }
        return null;
    }

    private async Task<ImportPlan> BuildPlanAsync(
        LibraryEntity library,
        UserEntity user,
        IReadOnlyList<YacReaderComicRecord> comics,
        bool overwrite,
        CancellationToken ct)
    {
        // Build a normalized-relative-path → archive node lookup for the library.
        var nodes = await _db.CatalogNodes
            .Where(n => n.LibraryId == library.Id && n.Kind == 1 && n.Availability != 5)
            .ToListAsync(ct);

        var caseInsensitive = !string.Equals(library.CaseComparisonPolicy, "ordinal", StringComparison.Ordinal);
        var byPath = new Dictionary<string, CatalogNodeEntity>(caseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            var key = NormalizeRelativePath(node.RelativePath);
            if (!string.IsNullOrEmpty(key))
                byPath.TryAdd(key, node);
        }

        var existingProgressItemIds = comics.Count == 0
            ? new HashSet<long>()
            : (await _db.ReadingProgress
                .Where(p => p.UserId == user.Id)
                .Select(p => p.ItemId)
                .ToListAsync(ct)).ToHashSet();

        var plan = new ImportPlan();
        var mappedItems = new List<MappedItem>();

        foreach (var comic in comics)
        {
            ct.ThrowIfCancellationRequested();
            var node = MatchNode(comic, byPath);
            var state = ResolveState(comic);

            if (node is null)
            {
                plan.Unmapped++;
                continue;
            }

            plan.Mapped++;

            var conflict = state != "unread" && existingProgressItemIds.Contains(node.Id);
            if (conflict)
                plan.Conflicts++;

            if (state != "unread" && (!conflict || overwrite))
                plan.ToImport++;

            mappedItems.Add(new MappedItem
            {
                ComicId = comic.ComicId,
                Node = node,
                Read = comic.Read,
                HasBeenOpened = comic.HasBeenOpened,
                CurrentPage = comic.CurrentPage,
                State = state,
                Conflict = conflict,
            });
        }

        plan.MappedItems = mappedItems;
        return plan;
    }

    private static CatalogNodeEntity? MatchNode(YacReaderComicRecord comic, Dictionary<string, CatalogNodeEntity> byPath)
    {
        // YACReader's comic.path is the full library-root-relative path
        // including the filename (e.g. "/Series/Volume 1.cbz"), so it is
        // the primary match candidate. fileName is a fallback for the rare
        // case where path is empty or stores only a folder. We do NOT try
        // path+fileName — that would double the filename.
        var candidates = new List<string>(2);
        if (!string.IsNullOrEmpty(comic.Path))
            candidates.Add(NormalizeRelativePath(comic.Path));
        if (!string.IsNullOrEmpty(comic.FileName))
            candidates.Add(NormalizeRelativePath(comic.FileName));

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrEmpty(candidate))
                continue;
            if (byPath.TryGetValue(candidate, out var node))
                return node;
        }
        return null;
    }

    private static string ResolveState(YacReaderComicRecord comic)
    {
        if (comic.Read)
            return "completed";
        if (comic.HasBeenOpened)
            return "inProgress";
        return "unread";
    }

    private static string NormalizeRelativePath(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        return path.Replace('\\', '/').Trim('/');
    }

    private static int ClampPage(int zeroBased, int? pageCount)
    {
        if (zeroBased < 0)
            zeroBased = 0;
        if (pageCount is { } count && count > 0 && zeroBased >= count)
            zeroBased = count - 1;
        return zeroBased;
    }

    private sealed class ImportPlan
    {
        public int Mapped;
        public int Unmapped;
        public int Conflicts;
        public int ToImport;
        public List<MappedItem> MappedItems = [];
    }

    private sealed record MappedItem
    {
        public long ComicId { get; init; }
        public CatalogNodeEntity? Node { get; init; }
        public bool Read { get; init; }
        public bool HasBeenOpened { get; init; }
        public int CurrentPage { get; init; }
        public string State { get; init; } = "unread";
        public bool Conflict { get; init; }
    }

    private sealed record ResolvedImport(LibraryEntity? Library, UserEntity? User, (string Code, string Message)? Error)
    {
        public static ResolvedImport Ok(LibraryEntity library, UserEntity user) => new(library, user, null);
        public static ResolvedImport Fail(string code, string message) => new(null, null, (code, message));
    }

    private sealed record SnapshotResult(string DbPath, string? SnapshotPath, (string Code, string Message)? Error) : IDisposable
    {
        public static SnapshotResult Ok(string dbPath, string? snapshotPath) => new(dbPath, snapshotPath, null);
        public static SnapshotResult Fail(string code, string message) => new(string.Empty, null, (code, message));

        public void Dispose()
        {
            if (SnapshotPath is not null && File.Exists(SnapshotPath))
            {
                try { File.Delete(SnapshotPath); } catch { }
            }
        }
    }
}

/// <summary>
/// Result of an import operation: either a populated DTO on success, or an
/// error code/message pair on failure (never throws for expected errors).
/// </summary>
public sealed class YacReaderImportResult<T> where T : class
{
    public T? Value { get; init; }
    public string? Error { get; init; }
    public string? Message { get; init; }
    public bool Success => Value is not null && Error is null;

    public static YacReaderImportResult<T> Ok(T value) => new() { Value = value };
    public static YacReaderImportResult<T> Fail(string error, string message) => new() { Error = error, Message = message };
}
