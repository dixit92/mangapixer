# MangaPlex — Project Conventions

Instructions for anyone changing this repository, human or AI agent. Human
contributors should also read [CONTRIBUTING.md](CONTRIBUTING.md).

## Product

MangaPlex is a folder-native comic/manga server and Angular web reader.
It is an independent alternative inspired by YACReader, not a fork.

## Root namespace

All .NET namespaces use the exact root prefix `com.lifepixer.mangaplex`.
The Angular package name is `com.lifepixer.mangaplex.web`.

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
| Release | `pwsh ./scripts/Package-Release.ps1` | Build the version-tagged image (immutable), SBOM (syft) + SHA-256 checksums into `artifacts/release/<version>` |
| Safety review | `pwsh ./scripts/Review-Safety.ps1` | Read-only diff safety review |

Windows distribution (Windows host only):

| Step | Command | Use |
|---|---|---|
| Publish | `pwsh ./scripts/Publish-Windows.ps1` | Self-contained server, worker, web assets and tray launcher staged in `artifacts/windows-dist` |
| Smoke | `pwsh ./scripts/Smoke-Windows.ps1` | Start the published server from a clean data root and check health, web UI, first-run setup, worker startup and clean shutdown |
| Installer | `pwsh ./scripts/Build-Installer.ps1` | Per-user MSI from `artifacts/windows-dist` into `artifacts/installer` |

Scripts report failures with file/test references and never modify code to hide
failures. There are no repo-specific agent skills; call the scripts directly.

### Container-based build (when host SDKs are unavailable)

Run from the repository root. `$PWD` is the repository root in bash and in
PowerShell; in `cmd.exe` use `%cd%`.

```bash
# .NET build and test (Linux x64 container)
docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build MangaPlex.slnx -c Release

docker run --rm -v "$PWD:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet test MangaPlex.slnx --no-build -c Release

# Angular build (requires npm ci first)
docker run --rm -v "$PWD/web:/workspace/web" -w /workspace/web node:24-bookworm-slim \
    sh -c "npm ci && npm run build"

# Container smoke test (when host lacks pwsh; requires Docker socket)
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
    -v "$PWD:/workspace" -w /workspace \
    mcr.microsoft.com/powershell:7.5 \
    pwsh ./scripts/Smoke-Container.ps1
```

### Direct .NET commands (when SDK is available)

```text
dotnet restore MangaPlex.slnx --locked-mode
dotnet build MangaPlex.slnx --no-restore -c Release
dotnet test MangaPlex.slnx --no-build -c Release
dotnet format MangaPlex.slnx --verify-no-changes
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

Build the current tree into an image and run it on a loopback port with
throwaway storage, so a human can review the running app without touching any
real deployment. Mount test media **read-only** (source-media invariant); supply
the real media path per machine — never commit it.

```bash
# Build (multi-stage: Angular + server + worker)
docker build -f deploy/Dockerfile -t mangaplex:live-review .

# Run on a free loopback port with throwaway storage; first-run setup
# screen creates the admin (no default credentials).
docker run -d --name mangaplex-live-review -p 127.0.0.1:8097:8080 \
    -v "<temp>/data:/data" -v "<temp>/cache:/cache" -v "<temp>/scratch:/scratch" \
    -v "<local-test-media>:/media:ro" \
    mangaplex:live-review
```

Health: `curl http://127.0.0.1:8097/health`. Tear down with
`docker rm -f mangaplex-live-review`, `docker rmi mangaplex:live-review`, and
delete the temp storage dirs. If 8097 is taken, pick any free loopback port.

## Versioning

- SemVer 2.0.0 from the first build.
- Current version: see `Version.props`.
- `Version.props` is the single source of truth consumed by .NET builds.
- `web/package.json` version must match `Version.props`.
- Git release tags: `v<version>` (created only as part of a release cut).

### Release ritual (maintainer-gated)

Every release is cut on `dev` and then **`main` is fast-forwarded / merged to that
release commit** so `main` always tracks the latest released version. This step is
mandatory and easy to forget: if it is skipped, `main` falls behind the released
versions while `dev` moves on.

Order, all maintainer-gated (agents do NOT do these autonomously):

1. Bump `Version.props` (and match `web/package.json`) to `<version>`; commit on `dev`.
2. Tag `v<version>` on that commit.
3. **Merge `dev` into `main`** (`git checkout main && git merge --no-ff dev`), so `main`
   contains the release commit and tag. `main` is the "last released" trunk; `dev` is the
   version-agnostic integration trunk that runs ahead.
4. Build/deploy the tagged image as needed.

Never merge `dev` into `main` for an in-progress cycle (main must track released
versions only) - the merge happens as part of the cut, after the version bump + tag.

## Shared contracts

- Opaque IDs use base36 encoding (`OpaqueId.Encode/Decode`). All ID types (`LibraryId`, `CatalogNodeId`, `ItemId`, `UserId`, `PageEntryKey`) are readonly record structs.
- Page indices are zero-based throughout (`PageIndex` with `Value >= 0`).
- `SortKey.EncodeName` produces a persisted sort key that matches `NaturalOrderComparer` ordering. Use `StringComparer.Ordinal` when sorting by sort key.
- Worker protocol uses JSON-lines over stdin/stdout with `WorkerEnvelope` framing. Protocol version is `WorkerProtocolVersion.Current` (2).
- No DTO exposes source paths. `AnalyzeRequest.ArchivePath` is the only DTO with a path field (private validated source locator for worker IPC, never a public HTTP DTO). `BreadcrumbsDto.Trail` is used instead of `Path` to avoid the forbidden name.
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
    dotnet build MangaPlex.slnx -c Release && \
    dotnet test MangaPlex.slnx --no-build -c Release"
```

## Deployment configurations

Two Compose files coexist under `deploy/`. They are alternatives, not layers.

| File | Layout | Use |
|---|---|---|
| `deploy/compose.yaml` | Three named Docker volumes (`/data`, `/cache`, `/scratch`) | Canonical, portable default. Used by CI, `Verify-Packaging.ps1`, `Verify.ps1`, and the e2e/smoke flow. |
| `deploy/compose.unraid.yaml` | Single `/config` bind to `/mnt/user/appdata/MangaPlex`, with `data`/`cache`/`scratch` as subfolders | Unraid-targeted. Appdata lives on the array (parity-protected, backed up with the rest of `/mnt/user/appdata`). |

Conventions for the Unraid config:

- `PUID`/`PGID` env vars (default `1000/1000`) follow the linuxserver.io/Unraid convention. Set them to the host user that owns the appdata share so files on the host are owned by you, not by an arbitrary in-image UID. The entrypoint creates the runtime user/group at startup when the IDs differ from the image's built-in 1000.
- Media mounts are commented examples. Uncomment and edit to point at `/mnt/user/<share>` paths. To keep real paths out of git, copy the file to `compose.unraid.override.yaml` (gitignored) and put your mounts there.
- `read_only: true`, `no-new-privileges`, and bounded logging are preserved from the canonical config. Unlike the canonical config, the port is published on all interfaces (`6266:8080`), not loopback only: LAN access is the point of an Unraid deployment. Put a reverse proxy in front for anything beyond a trusted LAN.
- Source media is mounted `:ro` to enforce the read-only source invariant.

The `entrypoint.sh` is shared by both configs. It chowns whichever state directories exist (`/data`, `/cache`, `/scratch`, and/or `/config` and its subfolders) to `PUID:PGID`, then drops privileges via `gosu`. Default behavior when `PUID`/`PGID` are unset is unchanged from the original 1000:1000 image user, so the canonical volume-based config and smoke tests are unaffected.

A Unraid Community Applications template (XML) is a separate post-MVP packaging task — it references a published registry image, not a build context, and only makes sense once `Package-Release.ps1` is producing version-tagged images.

## Privacy

- Logs contain IDs, counts, timings, and sanitized error codes — never absolute paths, titles, passwords, tokens, cookies, archive entry names, or page bytes.
- Browser bundles and source maps must not embed actual deployment roots.
- No telemetry, analytics, remote fonts, or third-party library lookup calls.
- `.dockerignore` independently excludes secrets, app state, and media from build contexts.

## Branching

Branch off `dev`, not `main` or a release tag, so your branch already contains
all merged work and merges cleanly; pull requests target `dev`. `dev` is a
single long-lived, **version-agnostic** trunk: the release number lives only in
`Version.props` and the tag, decided at cut time. Parallel work uses one git
worktree per branch (`git worktree add ../<folder> -b feature/<topic> dev`), and
every change is verified in its own clean worktree before hand-off.
