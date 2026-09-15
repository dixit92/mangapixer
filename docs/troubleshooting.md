# Troubleshooting

## Check that the server is running

```sh
curl http://127.0.0.1:8080/health          # Docker default port
curl http://localhost:6266/health          # Unraid default port
```

A running server answers `Healthy`. `/health/ready` returns the same. Both
only confirm that the web server is up; they do not test the database or the
archive worker. For those, look at the logs.

The container's own health check calls `/health` every 30 seconds. Check it
with:

```sh
docker compose -f deploy/compose.yaml ps
```

`GET /api/v1/system/info` returns the running version, which the app footer
also shows.

If the container keeps restarting, its logs usually say why:

```sh
docker compose -f deploy/compose.yaml logs --tail 100 mangaplex
```

## Logs

**Where:**

- The container output: `docker compose ... logs mangaplex`, or the Docker
  tab on Unraid.
- Files in `<data root>/logs` (`/data/logs`, or
  `/mnt/user/appdata/MangaPlex/data/logs` on Unraid), named
  `mangaplex-<yyyyMMdd>.log`. There is one file per day, a new file starts
  after 20 MB, and the newest 7 files are kept.

**Format:** `timestamp [LEVEL] source (event id) message`. Timestamps are UTC.

**What logs deliberately leave out.** For privacy, the server never logs:

- file or folder paths,
- titles and archive or page names,
- library names,
- passwords, tokens or cookies,
- client IP addresses,
- page contents.

Values of that kind are written as `[redacted]`. What you do see are internal
IDs, counts, timings, usernames and short error codes.

To connect a log line to an item, use the item's ID. It is the last part of
the reader's address (`/reader/<item-id>`), and it is also in the library
view's links.

**More detail.** In **MangaPlex Administration** > **Diagnostics**, set **Log
Level** to `Debug`. The change is immediate and resets to `Information` when
the server restarts. To turn up only one area (`Scanning`, `Media` or
`Reading`), use the API described in
[Configuration](configuration.md#logging).

## I can't sign in

- **"Too many login attempts. Please try again later."** Too many failed
  attempts for that username (5) or from your address (10) in 5 minutes. Wait
  a few minutes. Behind a reverse proxy, all users share one address; see
  [Reverse proxy and HTTPS](reverse-proxy-and-https.md#what-the-server-sees-behind-a-proxy).
- **"Account is temporarily locked due to too many failed attempts."** The
  account had 5 wrong passwords. Wait 15 minutes.
- **"This account has been disabled."** An admin disabled the account.
- **Forgotten password:** another admin can use **Reset password** on your
  row in the **Users** list. There is no command-line reset. If the only admin
  account is lost, restore a database backup from a time when you still knew
  the password ([Backup and restore](backup-and-restore.md#restoring-a-backup)).
- **The setup screen doesn't appear on a new install:** it only appears while
  the server has no accounts. If you mounted an existing data folder, sign in
  with an account from that database instead.
- **You are signed out after a week:** sessions last 7 days. Changing your
  password also signs out all of your sessions.

## A scan does not pick up files

1. **Did you start a scan?** Scans never run on their own, including right
   after you register a library. Select **Scan now** on the library's row.
2. **Is the file a supported archive?** Only `.cbz`, `.zip`, `.cbr`, `.rar`,
   `.cb7` and `.7z` are picked up. Folders of loose images, PDFs and EPUBs are
   ignored. See [Library layout](library-layout.md#supported-archive-formats).
3. **Can the container see the file?** List the folder from inside the container:

   ```sh
   docker compose -f deploy/compose.yaml exec mangaplex ls -la /media/comics
   ```

   If it is missing or empty, check the volume line in your override file
   and that the share is mounted on the host.
4. **Can the server's user read it?** The server runs as `PUID:PGID` (default
   `1000:1000`) and needs read permission on files and read + execute on
   folders.
5. **Is it in a skipped folder?** Folders starting with `.` and system folders
   such as `@eaDir` or `#recycle` are skipped. Symbolic links to folders are not
   followed.
6. **Did the scan fail?** The scan history for a library shows what the last
   scans did:

   ```sh
   curl -s -b "$JAR" "$BASE/api/v1/admin/libraries/<library-id>/scans"
   ```

   Each run lists how many entries were observed, added and removed, plus any
   error, such as "Library root is not accessible." (See
   [the API script](backup-and-restore.md#calling-the-admin-api-from-a-script)
   for `$JAR` and `$BASE`.)

If a share was unmounted during a scan, don't worry about losing progress: a
scan that finds nearly everything missing deletes nothing. Remount the share
and scan again.

## An archive won't open

| Message | Meaning |
|---|---|
| "Preparing this chapter…" | It has not been analysed yet. This happens right after a scan, while the server works through new archives. Wait a moment. |
| "This archive is password-protected." | Encrypted archives are not supported. |
| "The source file is no longer available." | The file was moved or deleted since the last scan. Rescan. |
| "The page could not be prepared in time; please retry." | Usually a drive spinning up, or a very large page. Try again. |

`.cb7`/`.7z` archives and other *solid* archives are listed with a page count
but cannot be read yet, and they show no cover. Repack them as `.cbz`.

## Thumbnails are missing

Covers are generated in the background, so give them time after a large
scan. The server always serves pages to readers before it works on covers.
Then:

1. Select **Regenerate thumbnails** (the image icon) on the library's row in
   **MangaPlex Administration** > **Libraries**. The message shows how many
   covers were queued, or "All thumbnails are already up to date."
2. Restarting the server also runs a full pass that fills in every missing
   cover.
3. Solid archives (including every `.cb7`/`.7z`) cannot get a cover yet.
4. Check the logs for worker errors. At start-up you should see
   `Worker process ready` and `Worker pool started`.

Thumbnails live in `<data root>/thumbnails`. It is safe to delete that folder;
the start-up pass rebuilds it.

## Permission errors (PUID / PGID)

Symptoms:

- the container exits right after starting,
- the logs show permission-denied errors or `unable to open database file`,
- files on the host belong to an unexpected user.

How ownership works: the container starts as root, gives `PUID:PGID`
ownership of `/data`, `/cache`, `/scratch` and `/config` (whichever exist),
and then runs the server as that user.

- **Set `PUID`/`PGID` to the owner of your host folder.** Check it with `ls -ln`.
  See [Install on Unraid](install-unraid.md#step-2-set-puid-and-pgid).
- **Don't add `user:` to the Compose service.** The container then can't fix
  ownership, and `PUID`/`PGID` are ignored.
- **Network shares that refuse ownership changes** (for example NFS with root
  squashing) make that ownership step fail silently. Either keep the state
  folders on local storage, or set `PUID`/`PGID` to the user that owns the
  share.
- **Media only needs to be readable by `PUID:PGID`.** It is mounted
  read-only, and its ownership is never changed.

## Port already in use

`docker compose up` fails with `Bind for 127.0.0.1:8080 failed: port is
already allocated` (or similar) when something else uses the port. Change the
host (left-hand) side of the port mapping in your override file, and keep the
container side at 8080:

```yaml
services:
  mangaplex:
    ports: !override
      - "127.0.0.1:8181:8080"
```

On Unraid, change `6266:8080` to another free port such as `6267:8080`. Then
use the new port in your browser and in your reverse proxy.

## The container stops the first time it starts after a restore

On Docker Desktop for Windows with a folder bind mount for `/data`, the first
start after an API restore can exit right after applying the swap, with
`SQLite Error 14: 'unable to open database file'` in the log. The restore has
already been applied at that point. **Start the container again** and it comes
up normally. (With `restart: unless-stopped` Docker does this for you.) Named
Docker volumes, as in the default Compose file, are not affected.

## Restore upload rejected as too large

`Multipart body length limit 134217728 exceeded` means the file is over
128 MiB, the most the server accepts as an upload. Use the manual restore
instead ([Backup and restore](backup-and-restore.md#option-2-swap-the-file-by-hand)).
