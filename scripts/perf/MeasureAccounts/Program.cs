// Measurement-only tool (1.46.0 lane M). Adds two measurement accounts to a throwaway COPY of a MangaPixer database, through
// the app's own Identity user manager, user store and password hasher (the same AddIdentityCore setup as
// AuthServicesExtensions): "perf-admin" (admin) and "perf-reader" (reader, granted every library). It reads only the library
// ids and its own two user names; it never reads or changes any other user, session or token. It can also point the copy's
// backup settings at the data root (so nothing is written outside the throwaway instance) and switch the update check off.
// Prints counts only. The passwords go to the file named on the command line (mode 600), never to the output.
//
// With --synthetic-tracking it also gives perf-reader a SYNTHETIC tracker account (state Ok, current consent, no token - nothing
// can be sent: the instance runs with Trackers:NetworkDisabled) and one entry per linked series record on it, so the browse
// listings pay the tracker-mark cost of a reader who tracks every linked series (an upper bound).
//
//   MeasureAccounts <copy.db> <password-file> [--local-backups] [--synthetic-tracking]
using System.Security.Cryptography;
using com.lifepixer.mangapixer.Core.Catalog;
using com.lifepixer.mangapixer.Core.Trackers;
using com.lifepixer.mangapixer.Server.Features.Auth;
using com.lifepixer.mangapixer.Server.Persistence;
using com.lifepixer.mangapixer.Server.Persistence.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: MeasureAccounts <copy.db> <password-file> [--local-backups]");
    return 2;
}

var dbPath = args[0];
var passwordFile = args[1];
var localBackups = args.Contains("--local-backups");
var syntheticTracking = args.Contains("--synthetic-tracking");

var services = new ServiceCollection();
services.AddLogging();
services.AddDbContext<MangaPixerDbContext>(o => o.ConfigureSqlite(dbPath));
services.AddIdentityCore<UserEntity>(options =>
{
    options.Password.RequiredLength = 8;
    options.Password.RequireDigit = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireNonAlphanumeric = false;
    options.User.RequireUniqueEmail = false;
})
.AddUserStore<MangaPixerUserStore>();

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();
var db = scope.ServiceProvider.GetRequiredService<MangaPixerDbContext>();
var users = scope.ServiceProvider.GetRequiredService<UserManager<UserEntity>>();

var lines = new List<string>();
var added = 0;
foreach (var (name, isAdmin) in new[] { ("perf-admin", true), ("perf-reader", false) })
{
    if (await users.FindByNameAsync(name) is not null)
    {
        Console.Error.WriteLine($"{name} exists already - nothing changed for it");
        continue;
    }

    var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
    var user = new UserEntity
    {
        UserName = name,
        PublicId = OpaqueId.Encode(Random.Shared.NextInt64(1, long.MaxValue)),
        IsAdmin = isAdmin,
        IsActive = true,
        CreatedAt = DateTimeOffset.UtcNow,
    };
    var result = await users.CreateAsync(user, password);
    if (!result.Succeeded)
    {
        Console.Error.WriteLine($"{name}: create failed ({result.Errors.Count()} errors)");
        return 1;
    }

    added++;
    lines.Add($"{name} {password}");
    if (!isAdmin)
    {
        var libraryIds = await db.Libraries.Select(l => l.Id).ToListAsync();
        foreach (var libraryId in libraryIds)
            db.LibraryGrants.Add(new LibraryGrantEntity { UserId = user.Id, LibraryId = libraryId, GrantedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        Console.WriteLine($"{name}: granted {libraryIds.Count} libraries");
    }
}

if (lines.Count > 0)
{
    await File.AppendAllLinesAsync(passwordFile, lines);
    if (!OperatingSystem.IsWindows())
        File.SetUnixFileMode(passwordFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
}
Console.WriteLine($"added {added} measurement accounts");

if (localBackups)
{
    // Backups of the throwaway instance stay in its own data root: no custom backup folder, update check off.
    var settings = await db.AppSettings.SingleOrDefaultAsync(s => s.Id == AppSettingsEntity.SingletonId);
    if (settings is not null)
    {
        settings.UpdateCheckEnabled = false;
        settings.BackupLocation = null;
        await db.SaveChangesAsync();
        Console.WriteLine("settings: update check off, backups in the data root");
    }
}

if (syntheticTracking)
{
    var reader = await users.FindByNameAsync("perf-reader");
    // The tracker the admin allowed on this instance (an admin setting, not user data); the first known one otherwise.
    var enabledTrackers = await db.TrackerSettings.AsNoTracking().Where(t => t.Enabled).Select(t => t.Tracker).ToListAsync();
    var tracker = enabledTrackers.FirstOrDefault(TrackerSlugs.All.Contains) ?? TrackerSlugs.MyAnimeList;
    if (reader is not null && !await db.TrackerAccounts.AnyAsync(a => a.UserId == reader.Id))
    {
        var now = DateTimeOffset.UtcNow;
        db.TrackerAccounts.Add(new TrackerAccountEntity
        {
            UserId = reader.Id,
            Tracker = tracker,
            State = (int)TrackerAccountState.Ok,
            ConsentVersion = TrackerConsent.UserVersion(tracker),
            ConsentAt = now,
            CreatedAt = now,
        });
        int[] linked = [0, 1]; // SeriesLinkState confirmed, auto
        var recordIds = await db.NodeSeriesLinks.AsNoTracking()
            .Where(l => linked.Contains(l.State) && l.RecordId != null).Select(l => l.RecordId!.Value).Distinct().ToListAsync();
        foreach (var recordId in recordIds)
        {
            db.TrackerEntries.Add(new TrackerEntryEntity
            {
                UserId = reader.Id,
                Tracker = tracker,
                RecordId = recordId,
                RemoteChapters = 100000,
                RemoteVolumes = 1000,
                RemoteSeenAt = now,
                UpdatedAt = now,
            });
        }
        await db.SaveChangesAsync();
        Console.WriteLine($"synthetic tracking: 1 account, {recordIds.Count} entries ({enabledTrackers.Count} trackers enabled by the admin)");
    }
}

return 0;
