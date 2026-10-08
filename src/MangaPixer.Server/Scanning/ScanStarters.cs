namespace com.lifepixer.mangapixer.Server.Scanning;

using System.Globalization;
using com.lifepixer.mangapixer.Core.Api;
using com.lifepixer.mangapixer.Server.Features.Export;
using com.lifepixer.mangapixer.Server.Persistence;
using Microsoft.EntityFrameworkCore;

/// <summary>
/// Who started each library's last completed scan (1.37.0), read from the scan run's lease owner - <c>server:&lt;userId&gt;</c> (an
/// admin's Scan), <see cref="LibraryScanScheduler.LeaseOwner"/> (the schedule) or <c>token:&lt;publicId&gt;</c> (an API token, 1.36.0) -
/// and resolved to the admin's user name or the token's name. For admin responses only; a removed account or token keeps its kind with
/// no name. Read-only: nothing is stored.
/// </summary>
public static class ScanStarters
{
    public const string Schedule = "schedule";
    public const string Admin = "admin";
    public const string Token = "token";

    private const int Completed = 2;
    private const string ServerPrefix = "server:";

    public static async Task<Dictionary<long, ScanStarterDto>> LastCompletedAsync(MangaPixerDbContext db, IReadOnlyCollection<long> libraryIds,
        CancellationToken ct = default)
    {
        if (libraryIds.Count == 0)
            return [];
        var lastIds = await db.ScanRuns.AsNoTracking()
            .Where(r => libraryIds.Contains(r.LibraryId) && r.Status == Completed)
            .GroupBy(r => r.LibraryId)
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);
        var owners = await db.ScanRuns.AsNoTracking()
            .Where(r => lastIds.Contains(r.Id) && r.LeaseOwner != null)
            .Select(r => new { r.LibraryId, r.LeaseOwner })
            .ToListAsync(ct);

        var userIds = owners.Select(o => UserIdOf(o.LeaseOwner!)).OfType<long>().Distinct().ToList();
        var tokenIds = owners.Select(o => TokenIdOf(o.LeaseOwner!)).OfType<string>().Distinct().ToList();
        var users = userIds.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => userIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.UserName, ct);
        var tokens = tokenIds.Count == 0 ? [] : await db.ApiTokens.AsNoTracking().Where(t => tokenIds.Contains(t.PublicId))
            .ToDictionaryAsync(t => t.PublicId, t => t.Name, StringComparer.Ordinal, ct);

        var result = new Dictionary<long, ScanStarterDto>();
        foreach (var o in owners)
        {
            var owner = o.LeaseOwner!;
            ScanStarterDto? starter = owner == LibraryScanScheduler.LeaseOwner ? new() { Kind = Schedule }
                : UserIdOf(owner) is { } userId ? new() { Kind = Admin, Name = users.GetValueOrDefault(userId) }
                : TokenIdOf(owner) is { } tokenId ? new() { Kind = Token, Name = tokens.GetValueOrDefault(tokenId) }
                : null;
            if (starter is not null)
                result[o.LibraryId] = starter;
        }
        return result;
    }

    private static long? UserIdOf(string owner) =>
        owner.StartsWith(ServerPrefix, StringComparison.Ordinal)
        && long.TryParse(owner.AsSpan(ServerPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;

    private static string? TokenIdOf(string owner)
    {
        var prefix = ExportScanController.LeaseOwnerOf(string.Empty);
        return owner.StartsWith(prefix, StringComparison.Ordinal) && owner.Length > prefix.Length ? owner[prefix.Length..] : null;
    }
}
