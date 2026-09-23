# Backup and restore

## Automatic database backups

The server snapshots its database on a schedule. You do not need to set anything up.

- **When:** 2 minutes after the server starts, then every 24 hours. After a restart the schedule continues from the newest snapshot, so frequent restarts do not each take a backup.
- **Where:** `<data root>/backups` by default. That is `/data/backups` with the Docker setup and `/mnt/user/appdata/MangaPixer/data/backups` on Unraid. You can move them to another disk or a network share (see [Choosing where backups are kept](#choosing-where-backups-are-kept)).
- **Name:** `rotating-<UTC date>-<UTC time>.db`, for example `rotating-20260915-024123.db`.
- **Retention:** the newest 7 are kept, and older ones are deleted. Only files with exactly this generated name are ever deleted; anything else in the folder is left alone.
- **Safe while running:** each snapshot is a consistent, self-contained copy of the database. It is safe to take while people are reading, and safe to copy off the machine at any time.

In **MangaPixer Administration** > **Backups** you see the schedule ("Every 24 h · keeping last 7"), when the last backup succeeded and how many are on disk. **Back up now** takes one immediately; it is a normal rotating backup and counts toward the 7.

### Backup settings

The **Backup settings** card in **MangaPixer Administration** changes the schedule without editing any file or restarting:

- **Scheduled backups** on or off. **Back up now** keeps working when the schedule is off.
- **Frequency**: every 6 or 12 hours, daily, every 2 days, weekly, or a custom number of hours (1 to 720).
- **Keep the newest** 1 to 100 snapshots. Lowering the number deletes nothing when you save; the card tells you how many of the oldest snapshots the next backup will remove.
- **Location**: the default folder, or a custom folder (below).

A setting that is also set in the server configuration (see [Configuration](configuration.md#backups)) is shown with **Managed by server configuration** and cannot be changed in the card. The configuration always wins, field by field.

### Choosing where backups are kept

Backups are an emergency copy, so it often makes sense to keep them on a different disk than the database: an archive disk, a second drive, or a network share. Pick **Custom folder** in the **Backup settings** card and enter an absolute path:

- **Docker / Unraid:** the container can only write where you give it a bind mount. Add one first, for example `/mnt/user/archive/mangapixer-backups:/backups`, then enter `/backups`. The folder must be writable by the container user (`PUID`/`PGID` on Unraid, `1000:1000` by default); the server does not change its ownership.
- **Windows:** any local or network folder, for example `D:\MangaPixer-backups` or `\\nas\archive\mangapixer`. Prefer a `\\server\share` path over a mapped drive letter: mapped drives belong to one sign-in and may not be connected when MangaPixer starts.

Before it saves, the server checks the folder: it must be absolute, its parent folder must already exist (only the last folder is created for you), it must be writable, and it must not be inside, or contain, the data, cache, scratch or media folders, any library folder, or the program folder. System and temporary folders (for example `/tmp`, `/etc`, `C:\Windows`, a drive root) are refused. Links are followed, so a symbolic link cannot point the backups into a library. Use **Test** to run all checks without saving. Both **Test** and **Save** ask for your current password, because this setting decides where a full copy of the database is written; wrong passwords count toward the sign-in rate limit, and every change is recorded in the audit trail.

The server writes a small `.mangapixer-backups.json` marker file into the folder. It is how the server recognises the folder later; do not delete it. If another MangaPixer server already uses the folder, saving is refused so two servers never delete each other's snapshots; after a reinstall you can take the folder over through the API (`adoptExistingMarker`).

Changing the location does not move or delete anything: existing snapshots stay where they are and are no longer listed or pruned. Take a backup right after the change (the card offers **Back up now**) so the new folder has one. Switching back to the default folder picks up the snapshots that are still there.

Anyone who can write to the backup folder can place a file there that shows up in the restore list. Every restore is validated, but use a folder only you can write to.

### If the backup location is unavailable

When a custom folder is missing, its marker file is gone (for example a share that is not mounted), or it now overlaps a protected folder, MangaPixer does **not** fall back to the data disk. Instead:

- scheduled backups are skipped and **Back up now** reports that the location is unavailable;
- the **Backup settings** card shows a red **Backup location unavailable** banner and the restore list says so;
- `/health/ready` reports `Degraded` (still HTTP 200, so Docker does not restart the container). It also reports `Degraded` when the last successful backup is older than twice the interval;
- the audit trail records when the location became unavailable and when it came back.

Once the folder is back, the next backup works again.

### Safety snapshots

The server also takes two kinds of one-off snapshot. They always stay in `<data root>/backups`, even when rotating backups go to a custom folder, so an upgrade or a restore never depends on a network share:

| File | Taken |
|---|---|
| `pre-migration-<timestamp>.db` | Before an upgrade changes the database schema. |
| `pre-restore-<timestamp>.db` | Right before a restore is staged, so you can undo it. |

The newest 3 of each kind are kept; older ones of the same kind are deleted after a new one is taken successfully.

## What a backup contains

A backup is **the database only**. That covers:

- user accounts, password hashes and roles, library access grants,
- libraries (name, folder path, reading direction) and the catalog of folders and archives,
- reading progress, read marks, bookmarks, **Continue reading** dismissals,
- per-user settings, including Private libraries and New Chapters options,
- sessions that existed when the snapshot was taken.

It does **not** contain:

| Not in the backup | Where it lives | What happens without it |
|---|---|---|
| Your comics and manga | Your media folders | Never touched by the server. Back them up separately. |
| Sign-in keys | `<data root>/keys` | Everyone has to sign in again. Nothing else is lost. |
| Cover thumbnails | `<data root>/thumbnails` | Regenerated automatically at start-up, or with **Regenerate thumbnails**. |
| Page cache | cache root | Rebuilt as people read. |
| Scratch files | scratch root | Temporary; not needed. |
| Logs | `<data root>/logs` | Only needed for troubleshooting. |

For a complete copy of a server, back up the whole data root (`/data`, or `/config/data` on Unraid). Its `backups` folder always holds a recent consistent database snapshot, even if the live `mangapixer.db` was copied while the server was writing to it.

To copy the backups out of a Docker volume:

```sh
docker compose -f deploy/compose.yaml cp mangapixer:/data/backups ./mangapixer-backups
```

## Restoring a backup

There is no restore button in the web app yet. You restore with a single API call, or by swapping the file by hand while the server is stopped.

### Option 1: upload through the API

1. Sign in as an admin from a script and upload the backup file (see the script in [Calling the admin API from a script](#calling-the-admin-api-from-a-script)):

   ```sh
   curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/operations/restore" \
     -H "X-MangaPixer-Csrf: $CSRF" \
     -F "file=@rotating-20260915-024123.db"
   ```

   The server checks the upload before accepting it. It must:

   - be a SQLite database,
   - pass an integrity check,
   - contain MangaPixer's tables,
   - not come from a newer version of MangaPixer.

   It then snapshots the current database as `pre-restore-<timestamp>.db` and answers:

   ```json
   {"preRestoreBackupFileName":"pre-restore-20260915-042342.db",
    "message":"Restore staged. Restart the server to complete the restore."}
   ```

   Nothing has changed yet. The live database is never overwritten while in use.

2. **Restart the server** (`docker compose ... restart`, or restart the container on Unraid). During start-up, before it opens the database, the server:
   - checks the staged file again,
   - moves the current database aside as `mangapixer.db.replaced-<timestamp>`,
   - puts the backup in its place.

   If anything fails, it puts the original back and logs "Pending DB restore did not apply".

Uploads are limited to 128 MiB. For a larger database, use option 2.

### Option 2: swap the file by hand

This is the same swap the server performs at start-up:

1. Stop the container.
2. In the data root, move `mangapixer.db` somewhere safe and delete `mangapixer.db-wal` and `mangapixer.db-shm` if they exist.
3. Copy your backup file into the data root as `mangapixer.db`. On Linux, make sure the server's user can write it; the container fixes ownership of the data folder on start.
4. Start the container.

### After a restore

- Everything returns to how it was when the backup was taken: accounts, passwords, access grants, reading progress. Anyone who signed in after that point has to sign in again.
- **Scan your libraries** to pick up files added, changed or removed since the backup.
- If the backup came from an older version, the server upgrades its schema on start-up, taking a `pre-migration-*.db` snapshot first.
- To undo the restore, restore the `pre-restore-*.db` file the same way.
- The `mangapixer.db.replaced-*` files in the data root are not cleaned up automatically. Delete them once you are happy with the result.

## Calling the admin API from a script

Every admin action in the web app is also an API call under `/api/v1`. Any request that changes something needs two things: a signed-in session cookie and a CSRF token in the `X-MangaPixer-Csrf` header. This bash script sets up both with `curl`:

```sh
BASE=http://127.0.0.1:8080
JAR=mangapixer-cookies.txt

# 1. Sign in as an admin (stores the session cookie in $JAR)
curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"your-password"}' > /dev/null

# 2. Get a CSRF token
CSRF=$(curl -s -c "$JAR" -b "$JAR" "$BASE/api/v1/auth/csrf" | sed 's/.*"token":"\([^"]*\)".*/\1/')

# 3. Call admin endpoints; send the token with every change
curl -s -c "$JAR" -b "$JAR" "$BASE/api/v1/operations/backups"
curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/operations/backups/rotating" \
  -H "X-MangaPixer-Csrf: $CSRF"
```

Delete the cookie file when you are done. It holds a valid session for 7 days. The full API description is served at `/openapi/v1.json`.

## Importing reading progress from YACReader

If a library folder was previously managed by YACReaderLibrary, you can copy your reading progress from its database into MangaPixer.

**What you need:**

- The YACReader database inside the library's own root folder, either as `library.ydb` or as `.yacreaderlibrary/library.ydb` (the folder YACReaderLibrary creates). The server opens it read-only and works on a temporary copy.
- The library registered and **scanned** in MangaPixer, so the archives exist to match against.

**Steps:**

1. In **MangaPixer Administration** > **Libraries**, libraries with a YACReader database show an extra **Import YACReader reading progress** button (two arrows). Select it.
2. The panel shows a preview, for example "12 comics · 10 matched · 2 unmatched · 0 already have progress". Nothing has been written yet.
3. Optionally tick **Overwrite items that already have MangaPixer progress**.
4. Select **Import N item(s)**.

**What is imported:**

| In YACReader | In MangaPixer |
|---|---|
| Read | Read (a permanent read mark), position on the last page YACReader recorded |
| Opened but not finished | In progress, on the same page |
| Never opened | Nothing |

- Progress goes into **the account of the admin running the import**. To import for another user, call the API directly with their user ID (from `GET /api/v1/admin/users`):

  ```sh
  curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/admin/import/yacreader/apply" \
    -H "X-MangaPixer-Csrf: $CSRF" -H 'Content-Type: application/json' \
    -d '{"libraryId":"<library-id>","targetUserId":"<user-id>"}'
  ```

  `POST /api/v1/admin/import/yacreader/preview` takes the same body and shows what would happen without writing anything. Library IDs come from `GET /api/v1/libraries`.
- Archives are matched by their path relative to the library folder, ignoring case. Files YACReader knew about that MangaPixer cannot find are listed as unmatched and skipped.
- Only reading progress is imported. Covers, bookmarks, ratings, tags and other metadata are not.
- Without **Overwrite**, items that already have progress in MangaPixer are left alone. With it, their position is replaced, but existing read marks are never removed.
