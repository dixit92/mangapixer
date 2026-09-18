# Install on Unraid

`deploy/compose.unraid.yaml` is a self-contained Compose file for Unraid. It is an alternative to [the canonical Docker setup](install-docker.md), not a layer on top of it. The differences:

| | Canonical (`compose.yaml`) | Unraid (`compose.unraid.yaml`) |
|---|---|---|
| State | Three named Docker volumes | One host folder, `/mnt/user/appdata/MangaPixer`, mounted at `/config` |
| File ownership | UID/GID 1000 by default | `PUID`/`PGID` so files belong to your Unraid user |
| Port | `127.0.0.1:8080` (loopback only) | `6266`, published on **all** interfaces, so the app is reachable from your LAN |
| Image | Pulled from GHCR (or built from a clone with the build overlay) | Pulled from GHCR; building on Unraid is optional |

The hardening is the same in both: read-only container filesystem, a `tmpfs` for `/tmp`, `no-new-privileges`, and bounded container logs.

An Unraid Community Applications template is planned but does not exist yet. For now you run the Compose file directly, so the host needs `docker compose` (on Unraid that usually comes from a Compose plugin).

## Step 1: get the Compose file and choose a version

Copy `deploy/compose.unraid.yaml` from the repository to a folder on the server, for example `/mnt/user/appdata/MangaPixer-compose/`, or clone the repository there. The file pulls `ghcr.io/dixit92/mangapixer:<version>` when the container starts, so nothing needs to be built or copied by hand.

Set `MANGAPIXER_VERSION` to the release you want (see the [Releases page](https://github.com/dixit92/mangapixer/releases)). If it is unset, the file falls back to `latest`, which moves with every release.

```sh
export MANGAPIXER_VERSION=1.17.1
```

To run an unreleased build instead, clone the repository on the server and add `deploy/compose.build.yaml` to the `-f` list in step 4; Compose then builds the image from the clone. `pwsh ./scripts/Package-Release.ps1` on another machine still produces a loadable `.tar` if you prefer to build elsewhere and `docker load` it.

## Step 2: set PUID and PGID

The container starts as root, sets the owner of `/config` and its `data`, `cache` and `scratch` subfolders to `PUID:PGID` on **every start**, and then runs the server as that user. Set these two variables to the user and group that should own the appdata folder on the host. Check the numbers with:

```sh
ls -ln /mnt/user/appdata
```

Many Unraid systems use `99` (`nobody`) and `100` (`users`) for shares. If you leave the variables unset, the defaults are `1000`/`1000`.

## Step 3: add your media shares

The media lines in `compose.unraid.yaml` are commented-out examples. Rather than editing the tracked file, put your real paths in an override file next to it, `compose.unraid.override.yaml`:

```yaml
services:
  mangapixer:
    environment:
      PUID: "99"
      PGID: "100"
    volumes:
      - /mnt/user/Manga:/media/manga:ro
      - /mnt/user/Comics:/media/comics:ro
```

- Always add `:ro` so each share is read-only inside the container. The server never writes into your media, and the read-only mount makes the operating system enforce that too.
- Mount shares under `/media`, where the library **Browse…** picker starts.
- Compose merges the override only when you pass it with `-f` (next step). Compose does not pick this file name up on its own.

You do not need a media-root variable for the mounts to work. You pick each library's folder in the web UI, and the picker's starting folder is controlled by [`MangaPixer__Storage__MediaRoot`](configuration.md#storage) (default `/media`).

## Step 4: start the container

From the folder that holds both files:

```sh
docker compose -f compose.unraid.yaml -f compose.unraid.override.yaml up -d
```

(From a repository clone, use `deploy/compose.unraid.yaml` and `deploy/compose.unraid.override.yaml`; the repository's `.gitignore` excludes the override file.)

Check it:

```sh
curl http://localhost:6266/health
```

It answers `Healthy`. If port 6266 is already taken, change the left-hand number of the port mapping. The server always listens on 8080 inside the container.

## Step 5: first-run setup and libraries

Open `http://<unraid-ip>:6266`. There is no default account. Create the first admin on the **Welcome to MangaPixer** screen, then register and scan your libraries. Use root paths like `/media/manga`. The steps are the same as [steps 5 and 6 of the Docker guide](install-docker.md#step-5-create-the-admin-account).

Port 6266 is published on every interface, but the server itself only speaks plain HTTP. If you plan to reach it from outside your LAN, put it behind a [reverse proxy with HTTPS](reverse-proxy-and-https.md), and consider binding the port to a specific address.

## What ends up in appdata

```text
/mnt/user/appdata/MangaPixer/
├── data/
│   ├── mangapixer.db          database (plus -wal / -shm files while running)
│   ├── keys/                 sign-in cookie keys (owner-only permissions)
│   ├── logs/                 daily log files, 7 kept
│   ├── backups/              rotating-*, pre-migration-*, pre-restore-* snapshots
│   └── thumbnails/           cover thumbnails
├── cache/                    page image cache (1 GiB budget, disposable)
└── scratch/                  temporary work folders (disposable)
```

Your normal appdata backup covers all of it. The `data/backups/rotating-*.db` files are consistent snapshots taken by the server, so they are safe to restore from even if your appdata backup ran while the container was up. See [Backup and restore](backup-and-restore.md).

If you want the page cache off the array, mount another path and point `MangaPixer__Storage__CacheRoot` at it (see [Configuration](configuration.md#storage)).

## Upgrading

Set `MANGAPIXER_VERSION` to the new version, run the same command with `pull` instead of `up -d`, then `up -d` again. Compose recreates the container with the new image, and `/config` is kept. Database schema upgrades take a `pre-migration-*.db` snapshot first, as described in [Install with Docker](install-docker.md#upgrading).
