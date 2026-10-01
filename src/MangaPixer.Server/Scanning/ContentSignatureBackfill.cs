namespace com.lifepixer.mangapixer.Server.Scanning;

using com.lifepixer.mangapixer.Core.Media;
using com.lifepixer.mangapixer.Server.Logging;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;

/// <summary>
/// Writes the missing content signature of live, analysed archives (1.31.1). A signature is written by an analysis
/// (<see cref="com.lifepixer.mangapixer.Server.Media.AnalysisResultPersister"/>), so archives analysed before 1.5.0 - and never again since - have none, and a
/// move or rename of such an archive is not recognised (an unsigned row never counts as a move: in-library moves, cross-library
/// moves and the after-the-fact pairing all need it). This reads the same 128 KiB per file the scanner reads
/// (<see cref="ContentSignature.Compute"/>) from the library's read-only root, re-checks the file's stamp against the stored one
/// before and after hashing (a changed or still-written file is left to the next scan and analysis), and stores the signature only
/// while the row still has none and still describes those bytes. Source media is only read.
/// </summary>
public sealed class ContentSignatureBackfill
{
    public const int BatchSize = 200;

    private readonly MangaPixerDbContext _db;

    public ContentSignatureBackfill(MangaPixerDbContext db) => _db = db;

    /// <summary>One batch, by ascending node id after <paramref name="afterNodeId"/>.</summary>
    public async Task<SignatureBackfillBatch> RunBatchAsync(long afterNodeId, TimeSpan perFileDelay, CancellationToken ct)
    {
        var rows = await (
            from n in _db.CatalogNodes.AsNoTracking()
            join a in _db.ArchiveItems.AsNoTracking() on n.Id equals a.NodeId
            join l in _db.Libraries.AsNoTracking() on n.LibraryId equals l.Id
            where n.Kind == 1 && n.Availability != 5 && a.ContentSignature == null && a.AnalysisState == 0 && n.Id > afterNodeId
            orderby n.Id
            select new { n.Id, n.RelativePath, l.RootPath, a.ByteLength, a.ModificationTicks }
        ).Take(BatchSize).ToListAsync(ct);

        var signed = 0;
        var skipped = 0;
        var roots = new Dictionary<string, ReadOnlyLibraryFileSystem?>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            if (!roots.TryGetValue(row.RootPath, out var fs))
            {
                var candidate = new ReadOnlyLibraryFileSystem(row.RootPath);
                roots[row.RootPath] = fs = candidate.RootExists() ? candidate : null;
            }
            var signature = fs is null ? null : SignatureOf(fs, row.RelativePath, row.ByteLength, row.ModificationTicks);
            if (signature is null)
            {
                skipped++;
                continue;
            }
            var updated = await _db.ArchiveItems
                .Where(a => a.NodeId == row.Id && a.ContentSignature == null
                    && a.ByteLength == row.ByteLength && a.ModificationTicks == row.ModificationTicks)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.ContentSignature, signature), ct);
            if (updated == 1)
                signed++;
            else
                skipped++;
            if (perFileDelay > TimeSpan.Zero)
                await Task.Delay(perFileDelay, ct);
        }
        return new SignatureBackfillBatch(rows.Count, signed, skipped, rows.Count == 0 ? afterNodeId : rows[^1].Id);
    }

    /// <summary>The file's signature, or null when it is unreadable or its stamp differs from the stored one (before or after).</summary>
    private static string? SignatureOf(IReadOnlyLibraryFileSystem fs, string relativePath, long byteLength, long modificationTicks)
    {
        try
        {
            var before = fs.GetSourceStamp(relativePath);
            if (before.ByteLength != byteLength || before.LastWriteTicks != modificationTicks)
                return null;
            string signature;
            using (var stream = fs.OpenRead(relativePath))
                signature = ContentSignature.Compute(stream);
            var after = fs.GetSourceStamp(relativePath);
            return after == before && ContentSignature.TryGetByteLength(signature) == byteLength ? signature : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (ArgumentException) { return null; }
    }
}

/// <summary>A backfill batch: rows looked at, signatures written, rows left alone, and the last node id (the next cursor).</summary>
public readonly record struct SignatureBackfillBatch(int Rows, int Signed, int Skipped, long LastNodeId);

/// <summary>
/// Runs <see cref="ContentSignatureBackfill"/> in the background (1.31.1): a while after start-up, then every few hours (a cheap query
/// when nothing is left). Paced per file so a large library's first pass stays gentle on the disks; failures are logged (type only)
/// and never crash the host. Logs carry counts only.
/// </summary>
public sealed class ContentSignatureBackfillHostedService : BackgroundService
{
    public static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PassInterval = TimeSpan.FromHours(6);
    public static readonly TimeSpan PerFileDelay = TimeSpan.FromMilliseconds(20);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<ContentSignatureBackfillHostedService> _logger;

    public ContentSignatureBackfillHostedService(IServiceScopeFactory scopes, ILogger<ContentSignatureBackfillHostedService> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RunPassAsync(stoppingToken);
                await Task.Delay(PassInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task RunPassAsync(CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        long cursor = 0;
        int signed = 0, skipped = 0;
        try
        {
            while (true)
            {
                using var scope = _scopes.CreateScope();
                var batch = await scope.ServiceProvider.GetRequiredService<ContentSignatureBackfill>().RunBatchAsync(cursor, PerFileDelay, ct);
                signed += batch.Signed;
                skipped += batch.Skipped;
                if (batch.Rows < ContentSignatureBackfill.BatchSize)
                    break;
                cursor = batch.LastNodeId;
            }
            if (signed > 0 || skipped > 0)
                _logger.LogInformation(LogEvents.Scanning.SignatureBackfillPass,
                    "Content signature backfill: {Signed} archives signed, {Skipped} left for the next scan, in {Seconds} s",
                    signed, skipped, (int)started.Elapsed.TotalSeconds);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(LogEvents.Scanning.SignatureBackfillFailed, "Content signature backfill failed: {Error}", ex.GetType().Name);
        }
    }
}
