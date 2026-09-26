# Configuration reference

This page lists every setting you can change. Most people only need the storage paths, and the Docker image already sets those for you.

## How to set values

Settings are hierarchical keys such as `MangaPixer:Storage:DataRoot`. Set them as **environment variables**, replacing each `:` with a double underscore `__`:

```yaml
# docker compose override
services:
  mangapixer:
    environment:
      MangaPixer__Backups__RetentionCount: "14"
      MangaPixer__Media__MaxConcurrentJobs: "1"
```

The server also reads `appsettings.json` next to the server binary. In the container image that file is baked in and the filesystem is read-only, so use environment variables there.

Invalid or out-of-range values (for example a negative number) are ignored and the default is used.

Every setting on this page is read once at start-up. Restart the container after you change one. The sections below are grouped by the part of the server that uses them.

## Container user

These are read by the container's start-up script, not by the server.

| Variable | Default | Meaning |
|---|---|---|
| `PUID` | `1000` | User ID the server runs as. The script gives this user ownership of `/data`, `/cache`, `/scratch` and `/config` (whichever exist) on every start. |
| `PGID` | `1000` | Group ID the server runs as. |

Set them to the owner of your host folders so the files on the host belong to you. See [Install on Unraid](install-unraid.md#step-2-set-puid-and-pgid).

## Network

| Variable | Default | Meaning |
|---|---|---|
| `ASPNETCORE_URLS` | `http://+:8080` (set in the image) | Address and port the server listens on inside the container. |

| `MangaPixer:Network:KnownProxies` (`MangaPixer__Network__KnownProxies`) | not set | Extra reverse-proxy IP addresses to trust, comma-separated (for example `203.0.113.7`). |
| `MangaPixer:Network:KnownNetworks` (`MangaPixer__Network__KnownNetworks`) | not set | Extra reverse-proxy networks to trust, as comma-separated CIDR ranges (for example `203.0.113.0/24`). |

The server always trusts proxies on loopback and the private IPv4 ranges (`10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`); the two keys above add to that list, they do not replace it. Only set them if your reverse proxy connects from some other address. See [Reverse proxy and HTTPS](reverse-proxy-and-https.md#what-the-server-sees-behind-a-proxy).

Leave `ASPNETCORE_URLS` alone in Docker. The image's health check calls `http://localhost:8080/health`. To use a different port, change the host side of the port mapping instead (see [Install with Docker](install-docker.md#reaching-the-server-from-other-devices)). The server speaks plain HTTP only. For HTTPS, see [Reverse proxy and HTTPS](reverse-proxy-and-https.md).

## Storage

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Storage:DataRoot` (`MangaPixer__Storage__DataRoot`) | `/data` in the image; otherwise a `data` folder next to the server binary | Database (`mangapixer.db`), sign-in keys (`keys/`), logs (`logs/`), backups (`backups/`) and thumbnails (`thumbnails/`). Keep this safe. |
| `MangaPixer:Storage:CacheRoot` (`MangaPixer__Storage__CacheRoot`) | `/cache` in the image; otherwise `cache` next to the binary | Cached page images. Disposable: emptied on every start (each run writes into its own `run-…` folder and deletes the earlier ones in the background). |
| `MangaPixer:Storage:ScratchRoot` (`MangaPixer__Storage__ScratchRoot`) | `/scratch` in the image; otherwise `scratch` next to the binary | Temporary work folders for opening archives and for the YACReader import. Disposable. |
| `MangaPixer:Storage:MediaRoot` (`MangaPixer__Storage__MediaRoot`) | `/media` | The only folder tree the admin **Browse…** picker can show when you register a library. It does not restrict what you can type into **Root Path** by hand. |
| `MangaPixer:Storage:CacheBudgetBytes` (`MangaPixer__Storage__CacheBudgetBytes`) | `1073741824` (1 GiB) | Maximum size of the page cache, in bytes. The least recently used pages are evicted after a write pushes the cache over budget, and a full pass also runs once a day. The cache starts empty on every start, so the budget covers everything in the folder. |
| `MangaPixer:Storage:ScratchBudgetBytes` (`MangaPixer__Storage__ScratchBudgetBytes`) | `1073741824` (1 GiB) | Size limit for temporary work folders, in bytes. Pages are written here briefly while they are extracted, and the YACReader import unpacks its upload here. |

Budgets are plain byte counts: `268435456` is 256 MiB, `4294967296` is 4 GiB. Relative paths are resolved against the server's working directory. You cannot register a library whose folder is inside a storage root or contains one.

The Unraid Compose file sets the three roots to `/config/data`, `/config/cache` and `/config/scratch`.

## Media processing

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Media:MaxConcurrentJobs` (`MangaPixer__Media__MaxConcurrentJobs`) | `2` | How many archive jobs (analysis, page extraction, thumbnails) run at once. When it is more than 1 and someone is waiting for a page, one slot is held back for them. Use `1` on a low-memory NAS. |
| `MangaPixer:Media:WorkerIdleTimeoutSeconds` (`MangaPixer__Media__WorkerIdleTimeoutSeconds`) | `180` | How many seconds a helper process that opens archives may sit unused before the server shuts it down. Each one holds roughly 100-200 MB of memory, so a quiet server gives that back. The next page or scan starts a fresh one, which adds about a fifth of a second to that first request. `0` keeps helpers running until the server stops, as before version 1.22.0. |
| `MangaPixer:Media:MinWarmWorkers` (`MangaPixer__Media__MinWarmWorkers`) | `0` | How many helper processes stay running however long the server is idle. `0` lets a quiet server run with none. Set `1` if you prefer the first page after a quiet spell to open without the start-up delay and can spare the memory. It never goes above `MaxConcurrentJobs`. |
| `MangaPixer:Media:ThumbnailBackfill:BatchSize` (`MangaPixer__Media__ThumbnailBackfill__BatchSize`) | `200` | How many items the background thumbnail pass loads at a time. |
| `MangaPixer:Media:ThumbnailBackfill:BackoffMs` (`MangaPixer__Media__ThumbnailBackfill__BackoffMs`) | `200` | How long, in milliseconds, the thumbnail pass waits between checks while the server is busy with readers or analysis. |
| `MangaPixer:Media:PageVariants:MaxDimensions` (`MangaPixer__Media__PageVariants__MaxDimensions`) | `1080,1440,2160` | Page sizes the reader may ask for, as longest edge in pixels, written smallest first and separated by commas. A request is rounded up to the next size on this list, so a few sizes cover every screen. Pages are never enlarged: a page already smaller than the requested size is sent as it is. At most six sizes; each extra size is another cached copy of every page you read. |
| `MangaPixer:Media:PageVariants:WebpQuality` (`MangaPixer__Media__PageVariants__WebpQuality`) | `82` | Image quality (1-100) for those resized pages. Higher looks better and costs more space and bandwidth. |
| `MangaPixer:Media:PageVariants:DefaultFilter` (`MangaPixer__Media__PageVariants__DefaultFilter`) | `balanced` | How pages are resized when the reader does not choose: `sharp`, `balanced` or `soft`. `sharp` keeps line art crispest but can make screentone dots shimmer on a high-resolution screen; `soft` smooths them away at the cost of some crispness; `balanced` sits in between. Readers can override this per request, so this only sets the starting point. An unrecognised value stops the server at startup rather than being ignored. |
| `Media:WorkerExecutablePath` (`Media__WorkerExecutablePath`) | Set in the image; otherwise found automatically | Location of the helper process that opens archives. Leave it as it is. Note there is no `MangaPixer` prefix on this key. |

## Scanning

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Scanning:Scheduler:Enabled` (`MangaPixer__Scanning__Scheduler__Enabled`) | `true` | Turns automatic library scans on or off for the whole server. When off, libraries are only scanned when an admin starts a scan, and each library's **Auto-scan** setting is kept for when you turn it back on. |
| `MangaPixer:Scanning:Scheduler:StartupDelaySeconds` (`MangaPixer__Scanning__Scheduler__StartupDelaySeconds`) | `180` | Seconds after start-up before the first check for due libraries, so a restart does not start scans straight away. |
| `MangaPixer:Scanning:Scheduler:TickSeconds` (`MangaPixer__Scanning__Scheduler__TickSeconds`) | `60` | Seconds between checks for due libraries. |

How often each library is scanned is set per library in the web app (**Auto-scan**: Off, Hourly, Every 6 hours, Daily or Weekly; daily by default). A value that cannot be read is ignored and the default is used. See [Automatic scans](library-layout.md#automatic-scans).

## Backups

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Backups:Enabled` (`MangaPixer__Backups__Enabled`) | `true` | Turns the scheduled database backups on or off. **Back up now** keeps working either way. |
| `MangaPixer:Backups:IntervalHours` (`MangaPixer__Backups__IntervalHours`) | `24` | Hours between scheduled backups. Decimals are allowed (`0.5` = 30 minutes). The first backup runs 2 minutes after the server starts. |
| `MangaPixer:Backups:RetentionCount` (`MangaPixer__Backups__RetentionCount`) | `7` | How many `rotating-*.db` snapshots to keep. Older ones are deleted. Pre-migration and pre-restore snapshots are kept separately (the newest 3 of each). |
| `MangaPixer:Backups:Location` (`MangaPixer__Backups__Location`) | not set (`<DataRoot>/backups`) | Absolute folder for the rotating backups, for example `/backups` next to a bind mount. It must pass the same checks as a folder chosen in the web app. If it fails them, backups stop (they never fall back to the data folder) and `/health/ready` reports `Degraded`. |
| `MangaPixer:Backups:AllowLocationChange` (`MangaPixer__Backups__AllowLocationChange`) | `true` | `false` locks the backup location in the web app, even when no `Location` is set. |
| `MangaPixer:Backups:MaxRestoreUploadBytes` (`MangaPixer__Backups__MaxRestoreUploadBytes`) | `536870912` (512 MiB) | Largest backup file you can upload for a restore. The web server also caps uploads at 128 MiB, so in practice the limit is 128 MiB, or this value if it is lower. |

The schedule, retention and location can also be changed in the **Backup settings** card in the web app. Each setting is resolved on its own: a value in this configuration wins (the web app shows it as **Managed by server configuration**), then the value saved in the web app, then the default. A value that cannot be read (for example `IntervalHours: daily`) is ignored with a warning in the log. Pre-migration and pre-restore snapshots always stay in `<DataRoot>/backups`. See [Backup and restore](backup-and-restore.md#choosing-where-backups-are-kept).

## Sign-in protection

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Security:RateLimit:MaxAttemptsPerIp` (`MangaPixer__Security__RateLimit__MaxAttemptsPerIp`) | `10` | Failed sign-ins allowed from one IP address per window. |
| `MangaPixer:Security:RateLimit:MaxAttemptsPerUser` (`MangaPixer__Security__RateLimit__MaxAttemptsPerUser`) | `5` | Failed sign-ins allowed for one username per window. |
| `MangaPixer:Security:RateLimit:Window` (`MangaPixer__Security__RateLimit__Window`) | `00:05:00` | Length of the counting window, as `hh:mm:ss`. |
| `MangaPixer:Security:RateLimit:Disabled` (`MangaPixer__Security__RateLimit__Disabled`) | `false` | Turns the limiter off. Only for testing. |

The counters are held in memory and reset when the server restarts. Separately, an account locks for 15 minutes after 5 wrong passwords; that is not configurable. Behind a reverse proxy the limit counts each client's real address, as long as the proxy is trusted and sends `X-Forwarded-For` (see [Reverse proxy and HTTPS](reverse-proxy-and-https.md#what-the-server-sees-behind-a-proxy)).

## Logging

The log level is **not** set in configuration. It is controlled at runtime by an admin and goes back to `Information` every time the server restarts. The `Logging:LogLevel` entries in `appsettings.json` do not change the server's log output.

- **In the web UI:** the **Debug Logging** card in **MangaPixer Administration**. **Global level** accepts `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. Below it you can raise the level for a single area (**Scanning**, **Media** or **Reading**) without flooding the log with everything else; **Inherited from Global** makes an area follow the global level again. Changes apply immediately.
- **With the API** (admin only): `GET /api/v1/operations/logging` returns the current levels and `PUT /api/v1/operations/logging` changes them. For a single area:

  ```json
  { "categories": [ { "name": "Scanning", "level": "Debug" } ] }
  ```

  Send an empty `level` for a category to make it follow the global level again. Calling the API needs a signed-in admin session and the CSRF header; see [Backup and restore](backup-and-restore.md#calling-the-admin-api-from-a-script) for a script that does both.

Framework noise (`Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`) stays at `Warning` regardless of the level you pick.

Logs go to the container output and to files in `<DataRoot>/logs` (`mangapixer-<date>.log`, one file per day, a new file after 20 MB, 7 files kept). See [Troubleshooting](troubleshooting.md#logs) for what the logs contain and deliberately leave out.

## Settings stored in the app

A few server-wide settings are changed by an admin in **MangaPixer Administration** rather than in configuration, and are saved in the database:

- **Backup settings**: schedule, retention and location (configuration values above take precedence).
- **Update Checker**: off by default. When an admin ticks **Check for updates**, the server asks the GitHub Releases API for MangaPixer's latest release at most once a day (or when you select **Check now**) and shows **Update available** or **Up to date** in the admin page. The request carries no instance identifier, user data, paths or telemetry; apart from web series information (below), it is the only call MangaPixer makes to the internet, and only while this setting is on.
- **Series metadata**: **Show series information**, **Fetch from the web** (off by default; turning it on needs the consent tick), the **Daily request budget** (5000 by default) and the per-library switches. See [Series information](series-information.md#admin-settings). `Metadata__NetworkDisabled=true` (config key `Metadata:NetworkDisabled`) turns web lookups off regardless of the admin setting, for operators who want certainty; the admin card then says so.
- **Library icons, reading directions and automatic scan schedules**, set per library on the **Libraries** card.

## Fixed behavior

These are built in and have no setting:

- Sign-in sessions last 7 days from your last activity.
- Passwords need at least 8 characters, including a lowercase letter.
- Expired sessions are cleaned up every hour. The cache-size pass runs daily.
- Automatic scans run one library at a time and never alongside another scan.
- Archive-processing time limits (for example 120 seconds to open an archive on a drive that is spinning up).
