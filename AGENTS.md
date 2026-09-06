# MangaPlex — Project Conventions

## Product

MangaPlex is a folder-native comic/manga server and Angular web reader.
It is an independent alternative inspired by YACReader, not a fork.

## Root namespace

All .NET namespaces use the exact root prefix `com.lifepixer.mangaplex`.
The Angular package name is `com.lifepixer.mangaplex.web`.

## Invariants

- **Source media is read-only in release one.** Never modify, move, rename, delete, annotate, or extract into source library directories.
- **No Git remote.** Do not configure a remote, push, or create commits by default.
- **No Gradle.** The backend build graph is .NET SDK/MSBuild. The frontend build graph is npm + Angular CLI. PowerShell 7 scripts provide orchestration.
- **No personal data in tracked files.** No real collection paths, credentials, user state, or machine-specific configuration.
- **`.devin/config.local.json` is ignored.** Never inspect or publish its contents.
- **No host SDK installation.** Use container-based builds if host .NET/Node SDKs are unavailable.

## Build and verification commands

All verification is script-driven. Skills and CI call the same scripts.

| Tier | Command | Use |
|---|---|---|
| Quick | `pwsh ./scripts/Verify-Quick.ps1` | Normal implementation loop |
| Full | `pwsh ./scripts/Verify.ps1 -Configuration Release` | Before declaring a package complete |
| Contracts | `pwsh ./scripts/Verify-Contracts.ps1` | API/DTO/migration/protocol/version changes |
| Packaging | `pwsh ./scripts/Verify-Packaging.ps1` | P16 and release candidates |
| Safety review | `pwsh ./scripts/Review-Safety.ps1` | Read-only diff safety review |

### Container-based build (when host SDKs are unavailable)

Verified commands using Docker (host has no .NET 10 SDK or Node):

```bash
# .NET build and test (Linux x64 container)
docker run --rm -v "D:\dev\lp-mangaplex:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet build MangaPlex.slnx -c Release

docker run --rm -v "D:\dev\lp-mangaplex:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    dotnet test MangaPlex.slnx --no-build -c Release

# Angular build (requires npm ci first)
docker run --rm -v "D:\dev\lp-mangaplex\web:/workspace/web" -w /workspace/web node:24-bookworm-slim \
    sh -c "npm ci && npm run build"
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

## Versioning

- SemVer 2.0.0 from the first build.
- Current version: `0.1.0-dev.10` (defined in `Version.props`).
- `Version.props` is the single source of truth consumed by .NET builds.
- `package.json` version must match `Version.props`.
- Git release tags: `v<version>` (not created by default).

## Shared contracts (P02)

- Opaque IDs use base36 encoding (`OpaqueId.Encode/Decode`). All ID types (`LibraryId`, `CatalogNodeId`, `ItemId`, `UserId`, `PageEntryKey`) are readonly record structs.
- Page indices are zero-based throughout (`PageIndex` with `Value >= 0`).
- `SortKey.EncodeName` produces a persisted sort key that matches `NaturalOrderComparer` ordering. Use `StringComparer.Ordinal` when sorting by sort key.
- Worker protocol uses JSON-lines over stdin/stdout with `WorkerEnvelope` framing. Protocol version is `WorkerProtocolVersion.Current` (1).
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
docker run --rm -v "D:\dev\lp-mangaplex:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c "apt-get update -qq && apt-get install -y -qq p7zip-full && \
    dotnet build MangaPlex.slnx -c Release && \
    dotnet test MangaPlex.slnx --no-build -c Release"
```

## Privacy

- Logs contain IDs, counts, timings, and sanitized error codes — never absolute paths, titles, passwords, tokens, cookies, archive entry names, or page bytes.
- Browser bundles and source maps must not embed actual deployment roots.
- No telemetry, analytics, remote fonts, or third-party library lookup calls.
- `.dockerignore` independently excludes secrets, app state, and media from build contexts.

## Devin skills

Project skills are in `.devin/skills/`:
- `/verify-quick` — Quick verification after edits
- `/verify-full` — Full release-mode verification
- `/verify-contracts` — Contract drift and compatibility checks
- `/verify-packaging` — Packaged Linux/Windows build and smoke
- `/review-safety` — Read-only safety review of diffs

Skills call canonical scripts and report failures with file/test references.
Skills never modify code to hide failures.
