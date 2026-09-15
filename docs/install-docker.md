# Install with Docker

This guide walks through `deploy/compose.yaml`, the canonical way to run the
server. It works on any Linux host, on a NAS that runs Docker, and on Docker
Desktop. For Unraid, see [Install on Unraid](install-unraid.md) instead.

## What you need

- Docker Engine with the Compose plugin (`docker compose`). The override
  example below uses the `!override` tag, which needs Compose 2.24 or later.
- A clone of this repository. MangaPlex does not publish a prebuilt image yet,
  so you build the image from source. The build is fully containerized: you
  do not need .NET or Node.js on the host.
- Your comics or manga in folders that the Docker host can read.

<!-- TODO(owner): if a registry image is published, add a "pull instead of build" path here. -->

## What the Compose file sets up

| Setting | Value | Why |
|---|---|---|
| Port | `127.0.0.1:8080:8080` | Published on loopback only. Only the Docker host itself can reach the app until you change this or put a [reverse proxy](reverse-proxy-and-https.md) in front. |
| `/data` | named volume `mangaplex-data` | Database, sign-in keys, logs, backups and thumbnails. **This is the volume to back up.** |
| `/cache` | named volume `mangaplex-cache` | Cached page images. Size-limited (1 GiB by default) and safe to delete. |
| `/scratch` | named volume `mangaplex-scratch` | Temporary work folders used while opening archives. Safe to delete. |
| `read_only: true` + `tmpfs: /tmp` | | The container's own filesystem is read-only; only the three volumes and `/tmp` are writable. |
| `no-new-privileges` | | The process cannot gain privileges after start-up. |
| Logging | `local` driver, 20 MB × 5 files | Container output cannot fill your disk. |
| Restart | `unless-stopped` | The server comes back after a reboot or crash. |

The container starts as root only long enough to fix ownership of the state
folders, then drops to UID/GID `1000:1000`. You can change that with `PUID` and
`PGID` (see [Configuration](configuration.md#container-user)).

Compose names the volumes after the project, which defaults to the folder
containing the Compose file. With the commands below the volumes are called
`deploy_mangaplex-data`, `deploy_mangaplex-cache` and `deploy_mangaplex-scratch`.

## Step 1: get the source

```sh
git clone <repository-url> mangaplex
cd mangaplex
```

To install a specific release, check out its tag (for example `git checkout v1.12.0`).

## Step 2: mount your media read-only

The server never writes to your media (see [Library layout](library-layout.md#read-only-guarantee)),
and mounting it read-only makes the operating system enforce that too. Keep
your real paths out of the tracked Compose file: put them in an override file
next to it, `deploy/compose.override.yaml`:

```yaml
services:
  mangaplex:
    volumes:
      - /srv/comics:/media/comics:ro
      - /srv/manga:/media/manga:ro
```

Mount every share somewhere under `/media`. That is the folder the library
**Browse…** picker starts in (you can change it with
[`MangaPlex__Storage__MediaRoot`](configuration.md#storage)).

Compose only reads the override file when you pass it with `-f`, as in the
commands below. The repository's `.gitignore` already excludes
`deploy/compose.override.yaml`, so Git never picks it up.

## Step 3: choose the image tag

The Compose file tags the image `mangaplex:${MANGAPLEX_VERSION}`. When the
variable is unset it falls back to `latest`, so always set the variable to the
version you are building. The version is in `Version.props`, and this script
prints it:

```sh
pwsh ./scripts/Get-MangaPixerVersion.ps1
```

Then set it in your shell. Set it again in every new shell before you run
`docker compose`, or Compose looks for an image under the fallback tag.

```sh
export MANGAPLEX_VERSION=1.12.0          # bash / zsh
```

```powershell
$env:MANGAPLEX_VERSION = "1.12.0"         # PowerShell
```

## Step 4: build and start

Run this from the repository root:

```sh
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml up -d --build
```

The first build downloads the .NET and Node base images and takes several
minutes. Later builds reuse cached layers.

Check that the server is up:

```sh
curl http://127.0.0.1:8080/health
```

It answers `Healthy`. The image also has a Docker health check that calls the
same endpoint every 30 seconds, so `docker compose -f deploy/compose.yaml ps`
shows `(healthy)` once start-up finishes.

## Step 5: create the admin account

There is **no default username or password.** A new server has no users at all.

Open `http://127.0.0.1:8080` in a browser. The **Welcome to MangaPlex** setup
screen appears. Choose an **Admin username** and a **Password**, confirm it,
and select **Create account**. You are signed in as the first admin.

- Passwords need at least 8 characters, including at least one lowercase letter.
- Usernames can contain letters, digits and `- . _ @ +`. No spaces.

The setup screen only works while the server has no users. After the first
account exists, the setup endpoint refuses every request, so nobody can use
it to create a second admin.

<!-- TODO(owner): screenshot of the first-run setup screen -->

## Step 6: add a library

1. Open the account menu and choose **MangaPlex Administration**.
2. In the **Libraries** card, under **Register New Library**, enter a
   **Display Name** and a **Root Path (server-side mount)**, for example
   `/media/comics`. **Browse…** lets you pick a folder under `/media` instead
   of typing it.
3. Select **Register**.
4. Select the **Scan now** button (the circular-arrow icon) on the new library's row. Registering a
   library does not scan it, and the server never scans on its own; you
   start every scan. See [Library layout](library-layout.md#rescans-moves-and-deletions).

<!-- TODO(owner): screenshot of the Administration > Libraries card -->

New users you create see no libraries until you give them access. See
[Users and access](users-and-access.md).

## Reaching the server from other devices

The default port mapping only listens on `127.0.0.1`. You have two options:

- **Recommended:** keep the loopback binding and run a reverse proxy on the
  same host that adds HTTPS. See [Reverse proxy and HTTPS](reverse-proxy-and-https.md).
- **LAN only, plain HTTP:** replace the port list in your override file. The
  `!override` tag replaces the list instead of adding to it:

  ```yaml
  services:
    mangaplex:
      ports: !override
        - "8080:8080"
  ```

The server always listens on port 8080 inside the container. To use a
different host port, change only the left-hand number (for example
`"127.0.0.1:8181:8080"`).

## Upgrading

1. Update your clone (`git pull`, or `git checkout` the new release tag).
2. Set `MANGAPLEX_VERSION` to the new version (`pwsh ./scripts/Get-MangaPixerVersion.ps1`).
3. Rebuild and restart:

   ```sh
   docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml up -d --build
   ```

Your volumes are kept. On start-up the server upgrades the database schema if
the new version needs it. **Before it changes an existing database it writes a
snapshot** called `pre-migration-<UTC timestamp>.db` to `/data/backups`. If
that snapshot fails, the server refuses to start rather than risk your data.
Automatic backup rotation never deletes these snapshots.

Each version is its own image tag, so the previous image stays on disk. Once
the new version is running, you can remove old ones with `docker image rm mangaplex:<old-version>`.

## Where backups land

Everything is under `/data/backups` in the `mangaplex-data` volume:

| File | Written when |
|---|---|
| `rotating-<timestamp>.db` | Every 24 hours (first run 2 minutes after start) and when you select **Back up now**. The newest 7 are kept. |
| `pre-migration-<timestamp>.db` | Before a schema upgrade. Never pruned. |
| `pre-restore-<timestamp>.db` | Before a restore is staged. Never pruned. |

Timestamps are UTC. To copy them to the host:

```sh
docker compose -f deploy/compose.yaml cp mangaplex:/data/backups ./mangaplex-backups
```

A backup contains the database only. See [Backup and restore](backup-and-restore.md)
for what that covers, how to restore, and how to change the schedule.

## Stopping and removing

```sh
docker compose -f deploy/compose.yaml -f deploy/compose.override.yaml down
```

`down` keeps the volumes. `down -v` **deletes them**, including your database
and backups. Copy `/data/backups` out first if you ever do that.
