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

Leave `ASPNETCORE_URLS` alone in Docker. The image's health check calls `http://localhost:8080/health`. To use a different port, change the host side of the port mapping instead (see [Install with Docker](install-docker.md#reaching-the-server-from-other-devices)). The server speaks plain HTTP only. For HTTPS, see [Reverse proxy and HTTPS](reverse-proxy-and-https.md).

## Storage

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Storage:DataRoot` (`MangaPixer__Storage__DataRoot`) | `/data` in the image; otherwise a `data` folder next to the server binary | Database (`mangapixer.db`), sign-in keys (`keys/`), logs (`logs/`), backups (`backups/`) and thumbnails (`thumbnails/`). Keep this safe. |
| `MangaPixer:Storage:CacheRoot` (`MangaPixer__Storage__CacheRoot`) | `/cache` in the image; otherwise `cache` next to the binary | Cached page images. Disposable. |
| `MangaPixer:Storage:ScratchRoot` (`MangaPixer__Storage__ScratchRoot`) | `/scratch` in the image; otherwise `scratch` next to the binary | Temporary work folders for opening archives and for the YACReader import. Disposable. |
| `MangaPixer:Storage:MediaRoot` (`MangaPixer__Storage__MediaRoot`) | `/media` | The only folder tree the admin **Browse…** picker can show when you register a library. It does not restrict what you can type into **Root Path** by hand. |
| `MangaPixer:Storage:CacheBudgetBytes` (`MangaPixer__Storage__CacheBudgetBytes`) | `1073741824` (1 GiB) | Maximum size of the page cache, in bytes. The least recently used pages are evicted after a write pushes the cache over budget, and a full pass also runs once a day. |
| `MangaPixer:Storage:ScratchBudgetBytes` (`MangaPixer__Storage__ScratchBudgetBytes`) | `1073741824` (1 GiB) | Size limit for temporary work folders, in bytes. Only solid RAR/7z archives need much scratch space. |

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

## Backups

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Backups:Enabled` (`MangaPixer__Backups__Enabled`) | `true` | Turns the scheduled database backups on or off. **Back up now** keeps working either way. |
| `MangaPixer:Backups:IntervalHours` (`MangaPixer__Backups__IntervalHours`) | `24` | Hours between scheduled backups. Decimals are allowed (`0.5` = 30 minutes). The first backup runs 2 minutes after the server starts. |
| `MangaPixer:Backups:RetentionCount` (`MangaPixer__Backups__RetentionCount`) | `7` | How many `rotating-*.db` snapshots to keep. Older ones are deleted. Pre-migration and pre-restore snapshots are never deleted. |
| `MangaPixer:Backups:MaxRestoreUploadBytes` (`MangaPixer__Backups__MaxRestoreUploadBytes`) | `536870912` (512 MiB) | Largest backup file you can upload for a restore. The web server also caps uploads at 128 MiB, so in practice the limit is 128 MiB, or this value if it is lower. |

Backups are written to `<DataRoot>/backups`. You cannot change that folder. See [Backup and restore](backup-and-restore.md).

## Sign-in protection

| Key (environment variable) | Default | Meaning |
|---|---|---|
| `MangaPixer:Security:RateLimit:MaxAttemptsPerIp` (`MangaPixer__Security__RateLimit__MaxAttemptsPerIp`) | `10` | Failed sign-ins allowed from one IP address per window. |
| `MangaPixer:Security:RateLimit:MaxAttemptsPerUser` (`MangaPixer__Security__RateLimit__MaxAttemptsPerUser`) | `5` | Failed sign-ins allowed for one username per window. |
| `MangaPixer:Security:RateLimit:Window` (`MangaPixer__Security__RateLimit__Window`) | `00:05:00` | Length of the counting window, as `hh:mm:ss`. |
| `MangaPixer:Security:RateLimit:Disabled` (`MangaPixer__Security__RateLimit__Disabled`) | `false` | Turns the limiter off. Only for testing. |

The counters are held in memory and reset when the server restarts. Separately, an account locks for 15 minutes after 5 wrong passwords; that is not configurable. Behind a reverse proxy every client appears to come from the proxy's IP address, so the per-IP limit is shared by everyone (see [Reverse proxy and HTTPS](reverse-proxy-and-https.md#what-the-server-sees-behind-a-proxy)).

## Logging

The log level is **not** set in configuration. It is controlled at runtime by an admin and goes back to `Information` every time the server restarts. The `Logging:LogLevel` entries in `appsettings.json` do not change the server's log output.

- **In the web UI:** **MangaPixer Administration** > **Diagnostics** > **Log Level**. Choose `Verbose`, `Debug`, `Information`, `Warning`, `Error` or `Fatal`. The change applies immediately.
- **With the API** (admin only): `GET /api/v1/operations/logging` returns the current level. `PUT /api/v1/operations/logging` changes it. The API can also raise the level for a single area, `Scanning`, `Media` or `Reading`, without flooding the log with everything else:

  ```json
  { "categories": [ { "name": "Scanning", "level": "Debug" } ] }
  ```

  Send an empty `level` for a category to make it follow the global level again. Calling the API needs a signed-in admin session and the CSRF header; see [Backup and restore](backup-and-restore.md#calling-the-admin-api-from-a-script) for a script that does both.

Framework noise (`Microsoft.AspNetCore`, `Microsoft.EntityFrameworkCore`) stays at `Warning` regardless of the level you pick.

Logs go to the container output and to files in `<DataRoot>/logs` (`mangapixer-<date>.log`, one file per day, a new file after 20 MB, 7 files kept). See [Troubleshooting](troubleshooting.md#logs) for what the logs contain and deliberately leave out.

## Fixed behavior

These are built in and have no setting:

- Sign-in sessions last 7 days from when you sign in.
- Passwords need at least 8 characters, including a lowercase letter.
- Expired sessions are cleaned up every hour. The cache-size pass runs daily.
- Libraries are only scanned when an admin starts a scan.
- Archive-processing time limits (for example 120 seconds to open an archive on a drive that is spinning up).
