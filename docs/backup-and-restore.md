# Backup and restore

## Automatic database backups

The server snapshots its database on a schedule. You do not need to set
anything up.

- **When:** 2 minutes after every start, then every 24 hours.
- **Where:** `<data root>/backups`. That is `/data/backups` with the Docker
  setup and `/mnt/user/appdata/MangaPlex/data/backups` on Unraid.
- **Name:** `rotating-<UTC date>-<UTC time>.db`, for example `rotating-20260915-024123.db`.
- **Retention:** the newest 7 are kept, and older `rotating-*` files are deleted.
- **Safe while running:** each snapshot is a consistent, self-contained copy
  of the database. It is safe to take while people are reading, and safe to
  copy off the machine at any time.

In **MangaPlex Administration** > **Diagnostics** > **Database Backups** you
see the schedule ("Every 24 h · keeping last 7"), when the last backup
succeeded and how many are on disk. **Back up now** takes one immediately; it
is a normal rotating backup and counts toward the 7.

To change the interval, retention, or turn the schedule off, see
[Configuration](configuration.md#backups).

The server also takes two kinds of one-off snapshot, which are **never
deleted automatically**:

| File | Taken |
|---|---|
| `pre-migration-<timestamp>.db` | Before an upgrade changes the database schema. |
| `pre-restore-<timestamp>.db` | Right before a restore is staged, so you can undo it. |

Delete old ones yourself when you no longer need them.

## What a backup contains

A backup is **the database only**. That covers:

- user accounts, password hashes and roles, library access grants,
- libraries (name, folder path, reading direction) and the catalog of folders
  and archives,
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

For a complete copy of a server, back up the whole data root (`/data`, or
`/config/data` on Unraid). Its `backups` folder always holds a recent
consistent database snapshot, even if the live `mangaplex.db` was copied while
the server was writing to it.

To copy the backups out of a Docker volume:

```sh
docker compose -f deploy/compose.yaml cp mangaplex:/data/backups ./mangaplex-backups
```

## Restoring a backup

There is no restore button in the web app yet. You restore with a single API
call, or by swapping the file by hand while the server is stopped.

### Option 1: upload through the API

1. Sign in as an admin from a script and upload the backup file (see the
   script in [Calling the admin API from a script](#calling-the-admin-api-from-a-script)):

   ```sh
   curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/operations/restore" \
     -H "X-MangaPlex-Csrf: $CSRF" \
     -F "file=@rotating-20260915-024123.db"
   ```

   The server checks the upload before accepting it. It must:

   - be a SQLite database,
   - pass an integrity check,
   - contain MangaPlex's tables,
   - not come from a newer version of MangaPlex.

   It then snapshots the current database as `pre-restore-<timestamp>.db`
   and answers:

   ```json
   {"preRestoreBackupFileName":"pre-restore-20260915-042342.db",
    "message":"Restore staged. Restart the server to complete the restore."}
   ```

   Nothing has changed yet. The live database is never overwritten while in use.

2. **Restart the server** (`docker compose ... restart`, or restart the
   container on Unraid). During start-up, before it opens the database, the
   server:
   - checks the staged file again,
   - moves the current database aside as `mangaplex.db.replaced-<timestamp>`,
   - puts the backup in its place.

   If anything fails, it puts the original back and logs
   "Pending DB restore did not apply".

Uploads are limited to 128 MiB. For a larger database, use option 2.

### Option 2: swap the file by hand

This is the same swap the server performs at start-up:

1. Stop the container.
2. In the data root, move `mangaplex.db` somewhere safe and delete
   `mangaplex.db-wal` and `mangaplex.db-shm` if they exist.
3. Copy your backup file into the data root as `mangaplex.db`. On Linux, make
   sure the server's user can write it; the container fixes ownership of the
   data folder on start.
4. Start the container.

### After a restore

- Everything returns to how it was when the backup was taken: accounts,
  passwords, access grants, reading progress. Anyone who signed in after
  that point has to sign in again.
- **Scan your libraries** to pick up files added, changed or removed since
  the backup.
- If the backup came from an older version, the server upgrades its schema on
  start-up, taking a `pre-migration-*.db` snapshot first.
- To undo the restore, restore the `pre-restore-*.db` file the same way.
- The `mangaplex.db.replaced-*` files in the data root are not cleaned up
  automatically. Delete them once you are happy with the result.

## Calling the admin API from a script

Every admin action in the web app is also an API call under `/api/v1`. Any
request that changes something needs two things: a signed-in session cookie
and a CSRF token in the `X-MangaPlex-Csrf` header. This bash script sets up
both with `curl`:

```sh
BASE=http://127.0.0.1:8080
JAR=mangaplex-cookies.txt

# 1. Sign in as an admin (stores the session cookie in $JAR)
curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/auth/login" \
  -H 'Content-Type: application/json' \
  -d '{"username":"admin","password":"your-password"}' > /dev/null

# 2. Get a CSRF token
CSRF=$(curl -s -c "$JAR" -b "$JAR" "$BASE/api/v1/auth/csrf" | sed 's/.*"token":"\([^"]*\)".*/\1/')

# 3. Call admin endpoints; send the token with every change
curl -s -c "$JAR" -b "$JAR" "$BASE/api/v1/operations/backups"
curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/operations/backups/rotating" \
  -H "X-MangaPlex-Csrf: $CSRF"
```

Delete the cookie file when you are done. It holds a valid session for 7 days.
The full API description is served at `/openapi/v1.json`.

## Importing reading progress from YACReader

If a library folder was previously managed by YACReaderLibrary, you can copy
your reading progress from its database into MangaPlex.

**What you need:**

- The YACReader database inside the library's own root folder, either as
  `library.ydb` or as `.yacreaderlibrary/library.ydb` (the folder
  YACReaderLibrary creates). The server opens it read-only and works on a
  temporary copy.
- The library registered and **scanned** in MangaPlex, so the archives exist
  to match against.

**Steps:**

1. In **MangaPlex Administration** > **Libraries**, libraries with a YACReader
   database show an extra **Import YACReader reading progress** button (two
   arrows). Select it.
2. The panel shows a preview, for example "12 comics · 10 matched · 2
   unmatched · 0 already have progress". Nothing has been written yet.
3. Optionally tick **Overwrite items that already have MangaPlex progress**.
4. Select **Import N item(s)**.

**What is imported:**

| In YACReader | In MangaPlex |
|---|---|
| Read | Read (a permanent read mark), position on the last page YACReader recorded |
| Opened but not finished | In progress, on the same page |
| Never opened | Nothing |

- Progress goes into **the account of the admin running the import**. To
  import for another user, call the API directly with their user ID (from
  `GET /api/v1/admin/users`):

  ```sh
  curl -s -c "$JAR" -b "$JAR" -X POST "$BASE/api/v1/admin/import/yacreader/apply" \
    -H "X-MangaPlex-Csrf: $CSRF" -H 'Content-Type: application/json' \
    -d '{"libraryId":"<library-id>","targetUserId":"<user-id>"}'
  ```

  `POST /api/v1/admin/import/yacreader/preview` takes the same body and shows
  what would happen without writing anything. Library IDs come from
  `GET /api/v1/libraries`.
- Archives are matched by their path relative to the library folder,
  ignoring case. Files YACReader knew about that MangaPlex cannot find are
  listed as unmatched and skipped.
- Only reading progress is imported. Covers, bookmarks, ratings, tags and
  other metadata are not.
- Without **Overwrite**, items that already have progress in MangaPlex are
  left alone. With it, their position is replaced, but existing read marks are
  never removed.
