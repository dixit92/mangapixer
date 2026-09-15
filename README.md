# MangaPlex

**A self-hosted, folder-native manga and comic library server with a web reader.**

<!-- TODO(owner): tagline / badges (version, license, build) / logo (assets/Square.svg, assets/Circle.svg) -->

MangaPlex serves your existing manga and comic folders to any browser. You point
it at the directories where your `.cbz` and `.cbr` files already live, and
it builds a catalog, generates thumbnails, and gives every user on your server
their own reading progress, read marks and "continue reading" shelf. It is an
independent project inspired by [YACReader](https://www.yacreader.com/) - not a
fork - with an ASP.NET Core backend and an Angular web reader that works on
desktop, tablet and phone.

### What "folder-native" means

- **Your folders are the library.** Nothing is imported, copied or reorganised.
  The folder tree you already have *is* the browse tree: series folders, volume
  sub-folders, loose archives.
- **Source media is never modified.** MangaPlex never writes, renames, moves,
  deletes, tags or extracts into your library folders. The container mounts them
  read-only (`:ro`). Everything MangaPlex creates (database, thumbnails, page
  cache, scratch space) lives in its own data directories.
- **Moves don't lose your place.** When a rescan finds that an archive was moved
  or renamed (one missing file and one new file with the same size and content
  signature), reading state follows the file.

<!-- TODO(owner): a sentence or two on why you built it / who it is for. -->

## Screenshots

<!-- TODO(owner): screenshots - home, library browse (card + list), reader (paged, double-page, webtoon), phone layout, admin. -->

## Features

**Library and browsing**

- Browse the folder hierarchy in a card view (with a card-size slider) or a list view.
- Sort by name (ascending or descending), recently added, recently read, or
  recently updated. Recently updated ranks a folder by its newest archive.
- Filter by read state (reading / read / unread) at any folder level. The filter
  also applies to series folders through their contents, and you can hide empty
  folders.
- A-Z jump rail with multilingual collation, infinite scroll in both directions
  after a jump, and read/reading rollup badges on folders.
- Multi-select, including shift/ctrl ranges and touch long-press, to mark items
  read or unread in bulk.
- Full-text search over titles and folder names (SQLite FTS5 trigram, so substrings match), with
  cover thumbnails and a folder-vs-archive badge.

**Reader**

- Four modes: paged left-to-right, paged right-to-left (manga), double-page
  spreads, and vertical webtoon scrolling. Admins set a default mode per library
  and per folder, and each user can override it per item or set a personal
  default.
- Double-page mode adapts: it shows a single page in narrow portrait, keeps wide
  spreads whole, and shows both page numbers.
- Touch and keyboard navigation: direction-aware swipe zones, arrow keys, a
  draggable page scrubber, a help overlay (`?`), and immersive fullscreen.
- Webtoon tap zones and swipe move by a configurable step (90% of the screen by
  default). Turn them off for free scrolling only.
- Configurable page-turn animation (Slide / Reveal / None) that respects
  `prefers-reduced-motion`.
- Auto-advance to the next or previous chapter, plus page prefetch around the
  current position.

**Per-user reading state**

- Each user has their own progress, read marks and "Continue reading" row. You
  can dismiss items from the row, and finished ones hide automatically.
- A "Start reading" / continue shortcut on each folder that opens the next unread item.
- Optional "always open read items from the start" preference.

**Home**

- "Continue reading": one row across all your libraries.
- "New chapters": recently added archives, grouped by library and stacked per
  top-level folder (latest chapter plus a "+N" badge). Each user chooses the time
  window in days (30 by default) and which libraries contribute.
- A grid of your libraries.

**Multi-user, privacy and access**

- No default credentials. A fresh instance shows a first-run setup screen, and
  the first admin account is created there (see
  [First-run setup](docs/users-and-access.md#first-run-setup)).
- Admin and reader roles, with per-user library access grants.
- Onboard users with a password, or with a single-use activation link that
  expires after 48 hours so the user sets their own password.
- **Private libraries and Incognito:** each user can mark libraries as private.
  While Incognito is on (the default for every new browser session), private
  libraries are hidden from browse, home and search.
- Login rate limiting, CSRF protection on authenticated state-changing requests,
  forced password change, per-user session revocation, and last-admin protection.

**Administration and operations**

- Add, rename and remove libraries from the web UI, using a folder picker
  confined to the media root. Removing a library deletes only MangaPlex's own
  metadata and thumbnails; your files are untouched.
- Scans run per library or across all libraries, and can be cancelled. Scans are
  manual: there is no scheduled rescan or filesystem watching yet.
- Persistent thumbnails that survive restarts and cache clears. A background
  backfill fills them in and yields to active readers. Thumbnails can be
  regenerated per library.
- Automatic rotating database backups (daily, 7 kept by default) and on-demand
  backups. A validated backup can be uploaded for restore; it is applied
  atomically on the next restart, with rollback if that fails (see
  [Backup and restore](docs/backup-and-restore.md)).
- Runtime log-level control (global and per subsystem) and a diagnostics export.
- YACReader progress import: if a library folder contains a YACReader library
  database, an admin can preview its read progress and import it into their own
  account. The YACReader data is only read.
- Health endpoints (`/health`, `/health/ready`) and an OpenAPI document at
  `/openapi/v1.json` (the checked-in contract is `contracts/openapi.json`).
- Web app manifest and icons, so MangaPlex can be added to a phone or tablet
  home screen.

## Supported formats

| | Formats |
|---|---|
| Archives | ZIP (`.cbz`, `.zip`), RAR 4 and RAR 5 (`.cbr`, `.rar`) |
| Page images | JPEG, PNG, WebP, AVIF, GIF, BMP, TIFF |

Scans pick up archives by extension, and each archive's actual type is then
detected from its file signature. Archives are read in place through the managed
SharpCompress library, with no external `7z` or `unrar` binary. Pages are decoded
and resized by ImageMagick (via Magick.NET) in a separate, supervised worker
process, so a malformed file cannot take the server down. Animated GIF, WebP and
APNG pages are passed through unchanged. Details:
[Supported archive formats](docs/library-layout.md#supported-archive-formats).

Current limitations:

- **Solid RAR archives and 7-Zip archives can't be opened in the reader yet.**
  Scans list them, but the server refuses page requests for them (it treats
  every 7-Zip archive as solid). Repack them as `.cbz` to read them.
- PDF, EPUB, CBT and folders of loose images are not supported.

<!-- TODO(owner): adjust the limitations list if solid-archive reading lands before publication. -->

## Quick start (Docker Compose)

Requirements: Docker with Compose v2, and a copy of this repository. The Compose
file builds the image from source; no prebuilt registry image is published yet.
<!-- TODO(owner): registry image + pull-based instructions once one exists. -->
Full guide: [Install with Docker](docs/install-docker.md).

1. **Clone the repository.**

   <!-- TODO(owner): public repo URL -->

   ```bash
   git clone <repository-url>
   cd <repository-folder>/deploy
   ```

2. **Mount your library read-only.** Create `deploy/compose.override.yaml`. Docker
   Compose merges it automatically with `compose.yaml` when you run it from
   `deploy/`. Mount each library under `/media`:

   ```yaml
   services:
     mangaplex:
       volumes:
         - /path/to/your/manga:/media/manga:ro
         - /path/to/your/comics:/media/comics:ro
   ```

   The file contains your real paths; `.gitignore` excludes it, so it stays out
   of commits.

3. **Pin the image tag and start.** Create `deploy/.env` (git-ignored) with the
   version you are building. It is read from `Version.props`; `1.12.0` at the
   time of writing. Then build and start:

   ```bash
   echo "MANGAPLEX_VERSION=1.12.0" > .env
   docker compose up -d --build
   ```

4. **Create the admin account.** Open <http://127.0.0.1:8080>. A fresh instance
   has no users and shows the setup screen. The password must be at least 8
   characters.

5. **Add a library.** Open **Administration** from the account menu. In the
   **Libraries** card, add a library, pick one of the folders under `/media`, then
   **Scan**. Browse, search and the reader work once the scan finishes.

What the canonical `deploy/compose.yaml` gives you:

- The port is published on **loopback only** (`127.0.0.1:8080`). For access from
  other devices, put a reverse proxy in front (see
  [Reverse proxy and HTTPS](docs/reverse-proxy-and-https.md)), or change the port
  mapping deliberately.
- Three named volumes: `mangaplex-data` at `/data` (database, keys, logs,
  backups, thumbnails - **back this one up**), `mangaplex-cache` at `/cache`
  (evictable page cache) and `mangaplex-scratch` at `/scratch` (temporary
  per-job workspace for the media worker).
- A read-only root filesystem, `no-new-privileges`, bounded logging (5 x 20 MB),
  and a health check. The server runs as a non-root user (UID/GID 1000 by default).

Update by pulling the new source, bumping `MANGAPLEX_VERSION`, and re-running
`docker compose up -d --build`. Database migrations run at startup, and a backup
is taken before a migration is applied.

### Unraid

`deploy/compose.unraid.yaml` is a self-contained alternative to the canonical
Compose file. It uses a single appdata bind (`/mnt/user/appdata/MangaPlex` at
`/config`, holding `data/`, `cache/` and `scratch/`) and the
linuxserver.io-style `PUID`/`PGID` variables. Full guide:
[Install on Unraid](docs/install-unraid.md).

1. Build a versioned image on a machine with the repository:
   `pwsh ./scripts/Package-Release.ps1`. This writes
   `artifacts/release/<version>/mangaplex-<version>-image.tar` plus an SBOM and
   checksums. Copy the tar to the server and `docker load -i` it. Alternatively,
   switch the file from `image:` to its commented `build:` block and build on
   Unraid.
2. Edit the file: uncomment and adjust the media mounts
   (`/mnt/user/<share>:/media/<name>:ro`), and set `PUID`/`PGID` to the user that
   owns your appdata share.
3. Start it with the matching version:
   `MANGAPLEX_VERSION=1.12.0 docker compose -f deploy/compose.unraid.yaml up -d`

This file publishes host port **6266 on all interfaces**, unlike the
loopback-only canonical file, and keeps the read-only root filesystem and
`no-new-privileges`. The folder picker only browses under `/media` by default; see
`MangaPlex__Storage__MediaRoot` below.

<!-- TODO(owner): Unraid Community Applications template once a registry image exists. -->

### Windows

<!-- TODO(owner): Windows is planned as a supported install path in the next release (tray app + MSI installer, built in a parallel cycle). Document the installer, where data lives, and how to add libraries once it ships. -->

Native Windows installation (tray app and installer) is planned for the next
release. Until then, use Docker Desktop with the Compose instructions above.

## Documentation

The [documentation index](docs/README.md) covers installation (Docker, Unraid),
configuration, library layout, the reader, users and access, backups, reverse
proxies and HTTPS, troubleshooting, and an FAQ.

## Configuration

Settings use standard ASP.NET Core configuration: `appsettings.json` or
environment variables. In environment variables, `:` becomes `__`. These are the
keys a self-hoster may want (full reference: [Configuration](docs/configuration.md)):

| Environment variable | Default | Purpose |
|---|---|---|
| `MangaPlex__Storage__DataRoot` | `./data` next to the app; `/data` in the image | Database (`mangaplex.db`), Data Protection keys, logs, backups, thumbnails |
| `MangaPlex__Storage__CacheRoot` | `./cache`; `/cache` in the image | Evictable derived page cache |
| `MangaPlex__Storage__ScratchRoot` | `./scratch`; `/scratch` in the image | Temporary per-job workspace for the media worker |
| `MangaPlex__Storage__MediaRoot` | `/media` | Root the admin folder picker may browse when adding libraries |
| `MangaPlex__Storage__CacheBudgetBytes` | `1073741824` (1 GiB) | Page-cache size limit (LRU eviction) |
| `MangaPlex__Storage__ScratchBudgetBytes` | `1073741824` (1 GiB) | Scratch size limit |
| `MangaPlex__Media__MaxConcurrentJobs` | `2` | Concurrent media-worker jobs; `1` suits low-memory NAS boxes |
| `MangaPlex__Media__ThumbnailBackfill__BatchSize` | `200` | Items per background thumbnail batch |
| `MangaPlex__Media__ThumbnailBackfill__BackoffMs` | `200` | Backfill back-off while readers are busy |
| `MangaPlex__Backups__Enabled` | `true` | Rotating database backups on/off |
| `MangaPlex__Backups__IntervalHours` | `24` | Hours between rotating backups |
| `MangaPlex__Backups__RetentionCount` | `7` | Rotating backups to keep (pre-migration backups are never pruned) |
| `MangaPlex__Backups__MaxRestoreUploadBytes` | `536870912` (512 MiB) | Size cap for an uploaded restore file |
| `MangaPlex__Security__RateLimit__MaxAttemptsPerIp` | `10` | Failed logins allowed per IP per window |
| `MangaPlex__Security__RateLimit__MaxAttemptsPerUser` | `5` | Failed logins allowed per username per window |
| `MangaPlex__Security__RateLimit__Window` | `00:05:00` | Rate-limit window |
| `Media__WorkerExecutablePath` | auto-discovered | Path to `MangaPixer.MediaWorker.dll`; the image sets `/app/worker/MangaPixer.MediaWorker.dll` |
| `ASPNETCORE_URLS` | `http://+:8080` in the image | Listen address |
| `PUID` / `PGID` | `1000` / `1000` | Container runtime user and group (entrypoint) |
| `MANGAPLEX_VERSION` | see the Compose files | Image tag used by the Compose files |

Log verbosity is changed at runtime in the **Diagnostics** card on the
Administration page. It resets to `Information` on restart. Logs are written to the console and to
`<DataRoot>/logs/`: daily files, 7 kept, 20 MB per file.

## Privacy and security

- **No telemetry, analytics, or phone-home.** MangaPlex makes no third-party
  lookups. Fonts (Roboto) and icons (Material Icons) are bundled and served by
  your own server, never from a CDN.
- **Logs never contain paths or titles.** They carry IDs, counts, timings and
  sanitised error codes, never absolute paths, titles, archive entry names,
  passwords, tokens, cookies or page bytes. Reader-facing API responses never
  contain filesystem paths. Only the admin-only folder picker and backup
  endpoints show server-side paths.
- **Read-only source media**, enforced both by the code's read-only filesystem
  layer and by the `:ro` mounts.
- Session cookies are protected with ASP.NET Core Data Protection. Keys live in
  `<DataRoot>/keys`. On Linux they are stored unencrypted, and in the container
  the entrypoint restricts that folder to its owner. On Windows they are
  DPAPI-encrypted. Treat the data volume as sensitive.
- The first admin is created only by you, through the setup screen. The setup
  endpoint refuses once any user exists.

<!-- TODO(owner): security contact / how to report a vulnerability (SECURITY.md). -->

## Building from source

Toolchain: **.NET SDK 10.0.4xx** (pinned by `global.json`), **Node.js 24** with
npm, and **PowerShell 7** for the scripts. Docker is needed for the container and
smoke flows.

```text
dotnet restore MangaPixer.slnx --locked-mode
dotnet build MangaPixer.slnx --no-restore -c Release
dotnet test MangaPixer.slnx --no-build -c Release

npm --prefix web ci
npm --prefix web run build
npm --prefix web run test:ci
```

The production container is a multi-stage build (Angular SPA, then server and
worker, then the runtime image):

```bash
docker build -f deploy/Dockerfile -t mangaplex:dev .
```

No local SDKs? Build inside the official containers instead:

```bash
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build MangaPixer.slnx -c Release
docker run --rm -v "$PWD/web:/workspace/web" -w /workspace/web node:24-bookworm-slim \
    sh -c "npm ci && npm run build"
```

The tests that need 7z-created fixtures return early, and so pass without testing
anything, when the `7z` command is missing. Install `p7zip-full` (for example
inside the SDK container) to actually exercise them.

<!-- TODO(owner): a documented local dev loop (dotnet run + ng serve with an API proxy) if you want contributors to use one. -->

### Verification scripts

All checks are script-driven (`pwsh`), and CI is meant to call the same scripts:

| Script | What it does |
|---|---|
| `./scripts/Verify-Quick.ps1` | Preflight, `dotnet format` check, .NET build and all .NET tests |
| `./scripts/Verify.ps1 -Configuration Release` | Locked restore, format check, build, all .NET tests, then `npm ci`, lint and the production Angular build, and a `docker compose config` check (web and Docker stages are skipped if npm or Docker is missing) |
| `./scripts/Verify-Contracts.ps1` | `Version.props` / `package.json` version match, Release build, and the contract, ordering and worker-protocol tests (including the OpenAPI snapshot) |
| `./scripts/Verify-Packaging.ps1` | Compose config and image build, a win-x64 publish on Windows, the container smoke test, then `Package-Release.ps1` |
| `./scripts/Smoke-Container.ps1` | Builds the image and runs a full HTTP flow against a synthetic library |
| `./scripts/Package-Release.ps1` | Version-tagged image, SPDX SBOM (syft, run in a container) and SHA-256 checksums in `artifacts/release/<version>` |
| `./scripts/Review-Safety.ps1` | Read-only safety review of the current diff |

The Angular unit and end-to-end tests are run directly:
`npm --prefix web run test:ci` (Vitest) and `npm --prefix web run e2e` (Playwright).

The preflight in `Verify-Quick.ps1`, `Verify.ps1` and `Verify-Packaging.ps1`
passes in a normal clone. It fails only if a git remote URL embeds a credential
(for example `https://user:token@host/...`).

### Repository layout

| Path | Contents |
|---|---|
| `src/MangaPixer.Server` | ASP.NET Core API, auth, scanning, catalog, SQLite (EF Core) persistence |
| `src/MangaPixer.MediaWorker` | Supervised archive/image worker process (no database, no network listener) |
| `src/MangaPixer.Core` | Shared contracts, IDs, natural ordering, worker protocol |
| `web/` | Angular + Angular Material web reader |
| `tests/` | xUnit suites (Core, MediaWorker, Server) and synthetic test-fixture generation |
| `assets/` | Logo artwork |
| `contracts/openapi.json` | Checked-in API contract (`/api/v1`) |
| `deploy/` | Dockerfile, Compose files, entrypoint |
| `scripts/` | PowerShell verification and release scripts |

## Project status

MangaPlex is at **1.12.0**. It follows SemVer, and the version lives in
`Version.props`. It is developed and used daily on a real home-server library.
<!-- TODO(owner): roadmap pointer (issues / milestones / project board), support expectations, and what "stable" means for you. -->

## Contributing

<!-- TODO(owner): contribution policy - are PRs welcome, issue-first, CLA/DCO, code of conduct? -->

Issues and suggestions are welcome. Before opening a pull request, run
`pwsh ./scripts/Verify-Quick.ps1` (see the known issue under
[Verification scripts](#verification-scripts)), and keep to the invariants in
[`AGENTS.md`](AGENTS.md): source media stays read-only, no personal data in
tracked files, no default credentials, and every new service is wired and tested
through its public surface.

## License

MangaPlex is released under the [MIT License](LICENSE). Copyright (c) 2026 Smit
Dixit. Third-party components and their licenses are listed in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
