# MangaPixer - Project Conventions

Instructions for anyone changing this repository, human or AI agent. Human contributors should also read [CONTRIBUTING.md](CONTRIBUTING.md).

## Product

MangaPixer is a folder-native comic/manga server and Angular web reader. It is an independent alternative inspired by YACReader, not a fork.

## Root namespace

All .NET namespaces use the exact root prefix `com.lifepixer.mangapixer`. The Angular package name is `com.lifepixer.mangapixer.web`.

## Invariants

- **Source media is read-only in release one.** Never modify, move, rename, delete, annotate, or extract into source library directories.
- **Remotes, pushes, tags and `main` are maintainer-gated.** The repository has a single GitHub remote. Agents never push, create tags, merge into `main`, or add remotes or credentials; those steps belong to the maintainer. Agents commit only when asked.
- **No Gradle.** The backend build graph is .NET SDK/MSBuild. The frontend build graph is npm + Angular CLI. PowerShell 7 scripts provide orchestration.
- **No personal data in tracked files.** No real collection paths, credentials, user state, or machine-specific configuration.
- **No default credentials.** The server must never ship a known username/password. A fresh instance starts with zero users; the first admin is created only by the user via `POST /api/v1/auth/setup` (guarded by the first-run setup screen) and that endpoint refuses once any user exists.
- **`.devin/config.local.json` is ignored.** Never inspect or publish its contents.
- **No host SDK installation by agents.** If the host has no .NET or Node SDK, use the container-based builds below instead of installing one.
- **Wiring is part of the deliverable.** A service is not done until it is registered in DI, reachable (controller route or hosted service), and covered by at least one test through that public surface (`WebApplicationFactory`, spawned worker process, or Playwright). Service-level tests alone are not enough: a service can pass its own tests while being unreachable in the running app (never registered, or no route to it).
- **Report tests by kind.** When reporting results, break counts down into unit / service-with-DB / HTTP / process / browser so integration coverage is visible.

## Build and verification commands

All verification is script-driven. Local runs and CI call the same scripts.

| Tier | Command | Use |
|---|---|---|
| Quick | `pwsh ./scripts/Verify-Quick.ps1` | Normal implementation loop |
| Full | `pwsh ./scripts/Verify.ps1 -Configuration Release` | Before declaring a change complete (this is what CI runs) |
| Contracts | `pwsh ./scripts/Verify-Contracts.ps1` | API/DTO/migration/protocol/version changes |
| Packaging | `pwsh ./scripts/Verify-Packaging.ps1` | Release candidates |
| Smoke | `pwsh ./scripts/Smoke-Container.ps1` | Full container HTTP smoke flow |
| E2E | `pwsh ./scripts/Verify-E2E.ps1` | Playwright browser suite: builds the image, runs it on a free loopback port with throwaway storage, provisions the first admin, installs Chromium, runs `web/e2e`, always tears down. Runs as its own CI job, separate from the Full tier. |
| Release | `pwsh ./scripts/Package-Release.ps1` | Build the version-tagged image (immutable), SBOM (syft) + SHA-256 checksums into `artifacts/release/<version>` |
| Safety review | `pwsh ./scripts/Review-Safety.ps1` | Read-only diff safety review |

Windows distribution (Windows host only):

| Step | Command | Use |
|---|---|---|
| Publish | `pwsh ./scripts/Publish-Windows.ps1` | Self-contained server, worker, web assets and tray launcher staged in `artifacts/windows-dist` |
| Smoke | `pwsh ./scripts/Smoke-Windows.ps1` | Start the published server from a clean data root and check health, web UI, first-run setup, worker startup and clean shutdown |
| Installer | `pwsh ./scripts/Build-Installer.ps1` | Per-user MSI from `artifacts/windows-dist` into `artifacts/installer` |

Scripts report failures with file/test references and never modify code to hide failures. There are no repo-specific agent skills; call the scripts directly.

### Container-based build (when host SDKs are unavailable)

Run from the repository root. `$PWD` is the repository root in bash and in PowerShell; in `cmd.exe` use `%cd%`.

```bash
# .NET build and test (Linux x64 container)
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build MangaPixer.slnx -c Release

docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet test MangaPixer.slnx --no-build -c Release

# Angular build (requires npm ci first)
docker run --rm -v "$PWD/web:/workspace/web" -w /workspace/web node:24-bookworm-slim \
    sh -c "npm ci && npm run build"
```

`scripts/Smoke-Container.ps1` needs PowerShell 7 and the Docker CLI on the same host; there is no container fallback for it (the PowerShell images ship no Docker client).

### Direct .NET commands (when SDK is available)

```text
dotnet restore MangaPixer.slnx --locked-mode
dotnet build MangaPixer.slnx --no-restore -c Release
dotnet test MangaPixer.slnx --no-build -c Release
dotnet format MangaPixer.slnx --verify-no-changes
```

### Angular commands (when Node is available)

```text
npm --prefix web ci
npm --prefix web run lint
npm --prefix web run build
npm --prefix web run test:ci
npm --prefix web run e2e
npm --prefix web run api:check
```

### Live review instance (browser check)

Build the current tree into an image and run it on a loopback port with throwaway storage, so a human can review the running app without touching any real deployment. Mount test media **read-only** (source-media invariant); supply the real media path per machine - never commit it.

```bash
# Build (multi-stage: Angular + server + worker)
docker build -f deploy/Dockerfile -t mangapixer:live-review .

# Run on a free loopback port with throwaway storage; first-run setup
# screen creates the admin (no default credentials).
docker run -d --name mangapixer-live-review -p 127.0.0.1:8097:8080 \
    -v "<temp>/data:/data" -v "<temp>/cache:/cache" -v "<temp>/scratch:/scratch" \
    -v "<local-test-media>:/media:ro" \
    mangapixer:live-review
```

Health: `curl http://127.0.0.1:8097/health`. Tear down with `docker rm -f mangapixer-live-review`, `docker rmi mangapixer:live-review`, and delete the temp storage dirs. If 8097 is taken, pick any free loopback port.

## Versioning

- SemVer 2.0.0 from the first build.
- Current version: see `Version.props`.
- `Version.props` is the single source of truth consumed by .NET builds.
- `web/package.json` version must match `Version.props`.
- Git release tags: `v<version>` (created only as part of a release cut).

### Release ritual (maintainer-gated)

Every release is cut on `dev` and then **`main` is fast-forwarded / merged to that release commit** so `main` always tracks the latest released version. This step is mandatory and easy to forget: if it is skipped, `main` falls behind the released versions while `dev` moves on.

Order, all maintainer-gated (agents do NOT do these autonomously):

1. Bump `Version.props` (and match `web/package.json`) to `<version>`, stamp the CHANGELOG `[<version>]` section, and refresh version-referencing docs (README and `docs/` install examples such as `MANGAPIXER_VERSION=` and the `MangaPixer-<version>-windows-x64.msi` filenames). Do not restate the version as prose where a live source already shows it — the README version badge tracks the latest release, so there is no manual "current version" line to update. If `Directory.Packages.props` or `web/package-lock.json` changed since the last release, refresh the affected sections of `THIRD-PARTY-NOTICES.md` (section 7 lists the commands) so every shipped package, and any upstream work it derives from, is attributed; the notices drift check must pass against the release publish output (`dotnet publish` the server and worker into a temp folder, then `node web/scripts/check-notices-drift.mjs --publish <tmp>/server --publish <tmp>/worker`). Commit on `dev`.
2. Tag `v<version>` on that commit.
3. **Merge `dev` into `main`** (`git checkout main && git merge --no-ff dev`), so `main` contains the release commit and tag. `main` is the "last released" trunk; `dev` is the version-agnostic integration trunk that runs ahead.
4. Build/deploy the tagged image as needed.

Never merge `dev` into `main` for an in-progress cycle (main must track released versions only) - the merge happens as part of the cut, after the version bump + tag.

## Shared contracts

- Opaque IDs use base36 encoding (`OpaqueId.Encode/Decode`). All ID types (`LibraryId`, `CatalogNodeId`, `ItemId`, `UserId`, `PageEntryKey`) are readonly record structs.
- Page indices are zero-based throughout (`PageIndex` with `Value >= 0`).
- `SortKey.EncodeName` produces a persisted sort key that matches `NaturalOrderComparer` ordering. `SortKey.ForNode` (what the catalog stores) orders case-insensitively: the case-folded name first, the name as spelled as a tie-breaker. Use `StringComparer.Ordinal` when sorting by sort key.
- Worker protocol uses JSON-lines over stdin/stdout with `WorkerEnvelope` framing. Protocol version is `WorkerProtocolVersion.Current` (5).
- No DTO exposes source paths. Only these worker-IPC DTOs carry a path, and none is ever a public HTTP DTO: `AnalyzeRequest.ArchivePath`, `ExtractRequest.ArchivePath`, `ComicInfoRequest.ArchivePath` and `CoverRenderRequest.ArchivePath` (private validated source locators), `ImageHashRequest.ImagePath` and `CoverRenderRequest.ImagePath` (a server-owned cache or scratch file - a stored cover thumbnail, or provider image bytes the server wrote to scratch; never a source path), and `CoverRenderRequest.OutputPath` (the server-owned file in the data root's cover store that the worker writes; never a source path). `BreadcrumbsDto.Trail` is used instead of `Path` to avoid the forbidden name.
- OpenAPI contract is at `contracts/openapi.json`. API prefix is `/api/v1`.
- Content version mismatch rejects progress updates (`ProgressPreconditions.Validate`).

## Testing

- .NET: xUnit with `Microsoft.NET.Test.Sdk`.
- Angular: Vitest (via Angular CLI) for unit tests, Playwright for E2E.
- Tests are deterministic, locale/timezone-independent, and use synthetic fixtures.
- No test uses real user collections or arbitrary user-supplied directories.
- Destructive filesystem tests use only newly created per-test writable fixtures.
- Archive tests requiring 7z fixtures need `p7zip-full` installed in the container.
- SQLite tests require `SQLitePCLRaw.bundle_e_sqlite3` 3.0.5+ for FTS5/trigram support.

### Container-based test with 7z support

```bash
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c "apt-get update -qq && apt-get install -y -qq p7zip-full && \
    dotnet build MangaPixer.slnx -c Release && \
    dotnet test MangaPixer.slnx --no-build -c Release"
```

## Deployment configurations

Two Compose files coexist under `deploy/`. They are alternatives, not layers.

| File | Layout | Use |
|---|---|---|
| `deploy/compose.yaml` | Three named Docker volumes (`/data`, `/cache`, `/scratch`); pulls `ghcr.io/dixit92/mangapixer` (add `compose.build.yaml` to build from source) | Canonical, portable default. Used by CI, `Verify-Packaging.ps1`, `Verify.ps1`, and the e2e/smoke flow. |
| `deploy/compose.unraid.yaml` | Single `/config` bind to `/mnt/user/appdata/MangaPixer`, with `data`/`cache`/`scratch` as subfolders | Unraid-targeted. Appdata lives on the array (parity-protected, backed up with the rest of `/mnt/user/appdata`). |

Conventions for the Unraid config:

- `PUID`/`PGID` env vars (default `1000/1000`) follow the linuxserver.io/Unraid convention. Set them to the host user that owns the appdata share so files on the host are owned by you, not by an arbitrary in-image UID. The entrypoint creates the runtime user/group at startup when the IDs differ from the image's built-in 1000.
- Media mounts are commented examples. Uncomment and edit to point at `/mnt/user/<share>` paths. To keep real paths out of git, copy the file to `compose.unraid.override.yaml` (gitignored) and put your mounts there.
- `read_only: true`, `no-new-privileges`, and bounded logging are preserved from the canonical config. Unlike the canonical config, the port is published on all interfaces (`6266:8080`), not loopback only: LAN access is the point of an Unraid deployment. Put a reverse proxy in front for anything beyond a trusted LAN.
- Source media is mounted `:ro` to enforce the read-only source invariant.

The `entrypoint.sh` is shared by both configs. It chowns whichever state directories exist (`/data`, `/cache`, `/scratch`, and/or `/config` and its subfolders) to `PUID:PGID`, then drops privileges via `gosu`. Default behavior when `PUID`/`PGID` are unset is unchanged from the original 1000:1000 image user, so the canonical volume-based config and smoke tests are unaffected.

The app is listed in Unraid Community Applications from the `dixit92/unraid-templates` repository; the template references the published registry image, not a build context (see `docs/install-unraid.md`).

## Privacy

- Logs contain IDs, counts, timings, and sanitized error codes - never absolute paths, titles, passwords, tokens, cookies, archive entry names, or page bytes.
- Browser bundles and source maps must not embed actual deployment roots.
- No telemetry, analytics, remote fonts, or third-party library lookup calls, with two sanctioned exceptions, both off by default and enabled only by an admin:
  - The **Update Checker**: when enabled it makes one GET to the GitHub Releases API for `dixit92/mangapixer` to compare versions. The request sends only a generic `User-Agent` (required by the GitHub API) and carries no instance identifier, user data, path, or telemetry; the result is shown only in the admin page / app footer.
  - The **metadata fetcher**: when the admin has enabled it globally (after the consent text) AND for a library, admin actions in that library may contact only the sites on its **provider allowlist** - currently MangaUpdates (`api.mangaupdates.com`, `cdn.mangaupdates.com`), the Grand Comics Database (`www.comics.org`, `files1.comics.org`), AniList (`graphql.anilist.co`), MangaDex (`api.mangadex.org`, `uploads.mangadex.org`) and Wikipedia (`en.wikipedia.org`, `www.wikidata.org`). Every request to these sites carries the same fixed `User-Agent`, naming MangaPixer, its version and its project URL (`MangaPixer/<version> (+https://github.com/dixit92/mangapixer)`) - identical on every instance of a version, so it is not an instance identifier. The admin can remove any site from the allowlist in Metadata Manager and add it back; a removed site receives no request of any kind. The approved sites are fixed in code: adding a site, a host or a new kind of request needs owner approval and a new consent version, and an instance that consented to an older version stops fetching until an admin accepts the new text. For MangaUpdates it sends only the search text the admin confirms (derived from a folder or file name, or an embedded ComicInfo series name), provider record ids, a fixed provider type filter when the admin hides doujinshi and novels in a search, and the fixed `User-Agent`; never paths, file lists, declared facts (a declared type or creator is only compared on the server with what the provider returns), user data, reading state, cookies, or an instance identifier. It honours provider rate limits and a daily request budget. Fetched data and images are stored in the application's own data root and served from there, so the browser never contacts a provider. Logs record ids, counts, status codes and timings, never search text or titles. The config switch `Metadata:NetworkDisabled=true` disables it regardless of the UI.
    - **Automatic lookups** (one global **Automatic matching** switch, off by default, with its own consent text; it applies only to libraries whose "Fetch from the web" is on): background matching may send the cleaned name of a new series-like folder, or of an archive that is its own work (in a collection folder, or loose next to subfolders) - a name nobody reviews before it is sent - to MangaUpdates with the fixed provider type filter, or, for a work with a comics signal (see Grand Comics Database), first to the Grand Comics Database with a start year taken from that name and to MangaUpdates only when GCD finds nothing close (without its doujinshi entry below a folder whose Content is "Doujinshi & adult one-shots"), and background refresh may send provider record ids of linked series, and background matching may send the cleaned name of a folder or archive again - with the same type filter - when it is still waiting after an earlier automatic attempt: an unmatched work after 30, 90 and 180 days, and a work waiting in Needs review once after a MangaPixer update changes the matcher's rules (never a work an admin has linked, confirmed or marked "Don't match"), to the same allowlisted hosts. Automatic requests count in the same daily budget (no separate cap), are paced at one per second and stop when the budget is spent or the provider asks to slow down. Nothing inside a folder marked "Don't match" is ever looked up. Manual Identify keeps working without the automatic consent.
      - **Automatic cover comparison** (part of Automatic matching and its consent, only while the "Compare covers" setting is on - on by default; `Metadata:AutoMatch:CompareCovers=false` disables it regardless of the UI): when the top two candidate records tie on the title for a volume-shaped work (a folder of volumes, or a one-shot - never chapters or webtoons), background matching may download the cover images of those two candidates from `cdn.mangaupdates.com`, by the image URL the provider returned, to compare them with the work's stored cover thumbnail. The request carries no data from the library, only the fixed `User-Agent`. The images are decoded and hashed by the media worker, compared by a 64-bit perceptual hash kept in memory, and deleted right after; nothing new is stored. At most two images per work; they count in the same daily budget and pacing and stop with it.
      - **Volume covers and volume lists** (part of Automatic matching and its consent, only while "Volume covers from the web" is on - on by default; `Metadata:AutoMatch:VolumeCovers=false` disables it regardless of the UI): for each series linked to a MangaUpdates record (automatically or by an admin), background work may send MangaDex the requests described under MangaDex - find its record, read its volume -> chapter list (in all languages and in the preferred language) and its list of volume covers in the preferred and the original language - and download the covers of volume 1 and of the volumes the folder holds - as volume files, as all of their chapters by the exact volume list, or as an estimated volume of the Volumes view (chapters placed by the volume list's estimate where it has gaps) - or, for a series MangaDex lists no volume 1 cover for, its main cover -; it repeats the lists on the linked record's refresh schedule to pick up a cover in the preferred language. When MangaDex gives no volume list, it may ask AniList for the series' totals as described under AniList. These requests count in the same daily budget and are paced at one per second. Nothing inside a folder marked "Don't match" is looked up.
    - **AniList** is used only for the chapters-per-volume conversion (the Missing report and virtual volumes): when an admin asks (one series, or a batch of at most 20), and - with Automatic matching on - in the background for a linked series that MangaDex gives no volume list for (see Volume covers and volume lists). It is never used to match a folder. It sends only the AniList record id when known (entered by an admin, stored, or taken from the linked MangaDex record's own AniList link), otherwise the title of the already-linked MangaUpdates record (never a folder or file name), with the fixed query fields and a fixed "manga, not novels" type filter, and the fixed `User-Agent`; never paths, file lists, user data, reading state, cookies or an instance identifier. Requests count in the same daily budget, are paced at one per second, and stop while AniList asks to slow down. The matched entry's titles, format, status, start year and volume / chapter totals are stored in the data root as a record that is never linked to a folder; logs record ids, counts, status codes and timings only.
    - **MangaDex** is used only as a companion of a series that is already linked to a MangaUpdates record, for that series' volume -> chapter list and its volume covers; it is never used to identify or match a folder. It sends only the title or an associated title of the already-linked MangaUpdates record (never a folder or file name), MangaDex record ids, a fixed content-rating list, the preferred and the original cover language as a filter (the preferred language also for the volume -> chapter list, to learn which chapters are released in it), and the fixed `User-Agent`; never paths, file lists, user data, reading state, cookies or an instance identifier. A MangaDex record is used only when its own MangaUpdates link names the already-linked record, or when an admin chose it. Cover images are downloaded from `uploads.mangadex.org` by the record id and file name MangaDex returned (its 512-pixel version), re-encoded and hashed by the media worker and stored in the data root with their hash; the browser never contacts MangaDex. Without Automatic matching, MangaDex is contacted only when an admin asks (Choose cover, Change MangaDex match, Refresh). MangaDex is credited in Metadata Manager, next to its volume-covers setting. Requests count in the same daily budget, are paced at one per second, honour MangaDex's rate limits and stop while MangaDex asks to slow down. Logs record ids, counts, status codes and timings only.
    - **Grand Comics Database** (`www.comics.org` API, `files1.comics.org` cover thumbnails) is the comics provider: Identify, automatic matching and refresh for works with a comics signal (a Comic / Graphic novel declaration, a comics category folder, issue- or album-numbered files, a comics id in ComicInfo) and whenever an admin picks it in Identify. It sends only the search text the admin confirms (or, with Automatic matching, the cleaned name of a new comics folder or work, as for MangaUpdates), a start year taken from that name, GCD record ids, and the fixed `User-Agent`; never paths, file lists, user data, reading state, cookies or an instance identifier; no account or key. Cover thumbnails are used only to show candidates and for the transient cover comparison; they are not stored as covers. Data is stored in the data root with its CC BY-SA 4.0 credit. Requests count in the same daily budget, are paced at no more than 25 per hour, and stop while GCD asks to slow down or blocks.
    - **Wikipedia** (`en.wikipedia.org`, with `www.wikidata.org` to find the page) is used only as a companion of a series already linked to a MangaUpdates record, for the volume -> chapter list of its English "List of ... chapters" page and each volume's English release date and ISBN; it is never used to identify or match a folder. It sends only the linked MangaUpdates record id (to Wikidata, to find the English article linked to it), the page titles Wikidata or Wikipedia returned or an admin chose (falling back to the linked record's English title and the `List of <title> chapters` page titles built from it), fixed query parameters, and the fixed `User-Agent` naming MangaPixer and its project URL as the Wikimedia User-Agent policy requires (the same for every instance); never folder or file names, paths, file lists, user data, reading state, cookies or an instance identifier. Only volume and chapter numbers, dates, ISBNs, the page title and revision id are stored in the data root, credited to Wikipedia with a link; no chapter titles or summaries. Without Automatic matching it is contacted only when an admin asks; in the background it runs with the volume covers and volume lists (only while "Volume covers from the web" is on). Requests count in the same daily budget, are paced at one per second, one at a time, with `maxlag`, and stop while Wikimedia asks to slow down. Logs record ids, counts, status codes and timings only.
  - Adding a provider, a host, or any other kind of automatic lookup changes this list and needs owner approval.
- `.dockerignore` independently excludes secrets, app state, and media from build contexts.

## Branching

Branch off `dev`, not `main` or a release tag, so your branch already contains all merged work and merges cleanly; pull requests target `dev`. `dev` is a single long-lived, **version-agnostic** trunk: the release number lives only in `Version.props` and the tag, decided at cut time. Parallel work uses one git worktree per branch (`git worktree add ../<folder> -b feature/<topic> dev`), and every change is verified in its own clean worktree before hand-off.
