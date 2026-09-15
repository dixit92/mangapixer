# Install on Unraid

`deploy/compose.unraid.yaml` is a self-contained Compose file for Unraid. It is
an alternative to [the canonical Docker setup](install-docker.md), not a layer
on top of it. The differences:

| | Canonical (`compose.yaml`) | Unraid (`compose.unraid.yaml`) |
|---|---|---|
| State | Three named Docker volumes | One host folder, `/mnt/user/appdata/MangaPixer`, mounted at `/config` |
| File ownership | UID/GID 1000 by default | `PUID`/`PGID` so files belong to your Unraid user |
| Port | `127.0.0.1:8080` (loopback only) | `6266`, published on **all** interfaces, so the app is reachable from your LAN |
| Image | Built from the repository | Uses a prebuilt image by default; building on Unraid is optional |

The hardening is the same in both: read-only container filesystem, a `tmpfs`
for `/tmp`, `no-new-privileges`, and bounded container logs.

An Unraid Community Applications template is planned but does not exist yet.
For now you run the Compose file directly, so the host needs `docker compose`
(on Unraid that usually comes from a Compose plugin).

## Step 1: get an image onto Unraid

The Compose file expects an image called `mangapixer:<version>`. MangaPixer
does not publish images to a registry, so you either load one you built
elsewhere or build it on the server.

**Option A: build on another machine and copy it over (recommended).**
On a machine with Docker and a clone of the repository:

```sh
docker build -f deploy/Dockerfile -t mangapixer:1.12.0 .
docker save mangapixer:1.12.0 -o mangapixer-1.12.0-image.tar
```

`pwsh ./scripts/Package-Release.ps1` does the same thing and also writes an
SBOM and SHA-256 checksums to `artifacts/release/<version>/`. It pulls the
`anchore/syft` image to create the SBOM.

Copy the `.tar` file to Unraid and load it:

```sh
docker load -i mangapixer-1.12.0-image.tar
```

**Option B: build on Unraid.** Clone the repository on the server. In
`compose.unraid.yaml`, comment out the `image:` line and uncomment the
`build:` block beneath it.

Whichever option you use, set `MANGAPIXER_VERSION` to the version you loaded
or built. If it is unset, the file falls back to `mangapixer:latest`.

```sh
export MANGAPIXER_VERSION=1.12.0
```

## Step 2: set PUID and PGID

The container starts as root, sets the owner of `/config` and its `data`,
`cache` and `scratch` subfolders to `PUID:PGID` on **every start**, and then
runs the server as that user. Set these two variables to the user and group
that should own the appdata folder on the host. Check the numbers with:

```sh
ls -ln /mnt/user/appdata
```

Many Unraid systems use `99` (`nobody`) and `100` (`users`) for shares. If
you leave the variables unset, the defaults are `1000`/`1000`.

## Step 3: add your media shares

The media lines in `compose.unraid.yaml` are commented-out examples. Rather
than editing the tracked file, put your real paths in an override file next
to it, `compose.unraid.override.yaml`:

```yaml
services:
  mangapixer:
    environment:
      PUID: "99"
      PGID: "100"
    volumes:
      - /mnt/user/Reading:/media/reading:ro
      - /mnt/user/Comics:/media/comics:ro
```

- Always add `:ro` so each share is read-only inside the container. The
  server never writes into your media, and the read-only mount makes the
  operating system enforce that too.
- Mount shares under `/media`, where the library **Browse…** picker starts.
- Compose merges the override only when you pass it with `-f` (next step).
  Compose does not pick this file name up on its own.

You do not need a media-root variable for the mounts to work. You pick each
library's folder in the web UI, and the picker's starting folder is controlled
by [`MangaPixer__Storage__MediaRoot`](configuration.md#storage)
(default `/media`).

## Step 4: start the container

From the folder that holds both files:

```sh
docker compose -f compose.unraid.yaml -f compose.unraid.override.yaml up -d
```

(From a repository clone, use `deploy/compose.unraid.yaml` and
`deploy/compose.unraid.override.yaml`; the repository's `.gitignore` excludes
the override file.)

Check it:

```sh
curl http://localhost:6266/health
```

It answers `Healthy`. If port 6266 is already taken, change the left-hand
number of the port mapping. The server always listens on 8080 inside the
container.

## Step 5: first-run setup and libraries

Open `http://<unraid-ip>:6266`. There is no default account. Create the
first admin on the **Welcome to MangaPixer** screen, then register and scan
your libraries. Use root paths like `/media/reading`. The steps are the same
as [steps 5 and 6 of the Docker guide](install-docker.md#step-5-create-the-admin-account).

Port 6266 is published on every interface, but the server itself only
speaks plain HTTP. If you plan to reach it from outside your LAN, put it
behind a [reverse proxy with HTTPS](reverse-proxy-and-https.md), and consider
binding the port to a specific address.

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

Your normal appdata backup covers all of it. The `data/backups/rotating-*.db`
files are consistent snapshots taken by the server, so they are safe to
restore from even if your appdata backup ran while the container was up. See
[Backup and restore](backup-and-restore.md).

If you want the page cache off the array, mount another path and point
`MangaPixer__Storage__CacheRoot` at it (see [Configuration](configuration.md#storage)).

## Upgrading

Load or build the new image, set `MANGAPIXER_VERSION` to the new version, and
run the same `up -d` command. Compose recreates the container with the new
image, and `/config` is kept. Database schema upgrades take a
`pre-migration-*.db` snapshot first, as described in
[Install with Docker](install-docker.md#upgrading).
