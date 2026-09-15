# Contributing to MangaPlex

Thanks for your interest in MangaPlex, a folder-native comic/manga server with an
Angular web reader. This guide explains how to set up a development environment,
how changes are verified, and the rules every contribution has to follow.

By participating you agree to follow the [Code of Conduct](CODE_OF_CONDUCT.md).
Security problems should **not** be filed as public issues; see
[SECURITY.md](SECURITY.md).

## Ways to contribute

- **Report a bug** with the [bug report form](../../issues/new?template=bug_report.yml).
- **Suggest a feature** with the [feature request form](../../issues/new?template=feature_request.yml),
  or start a thread in Discussions if the idea still needs shaping.
- **Improve the documentation** under [`docs/`](docs/).
- **Send a pull request.** For anything larger than a small fix, please open an issue
  first so the approach can be agreed before you spend time on it.

## Prerequisites

| Tool | Version | Needed for |
|---|---|---|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0.400 or a later 10.0.4xx patch (pinned in [`global.json`](global.json)) | Server, media worker, all .NET tests |
| [Node.js](https://nodejs.org/) + npm | 24.x | Angular web reader (lint, build, unit tests) |
| [PowerShell](https://github.com/PowerShell/PowerShell) | 7.0 or later (`pwsh`) | All verification scripts in `scripts/` |
| Git | any recent version | |
| [Docker](https://docs.docker.com/get-docker/) | optional | Container smoke test, packaging, and the container fallback below |
| 7-Zip CLI (`7z`, e.g. `p7zip-full`) | optional | 7z archive fixture tests; they are skipped when `7z` is not on `PATH` |

The backend builds with the .NET SDK and MSBuild only, and the frontend with npm and
the Angular CLI. PowerShell scripts orchestrate both. The project does not use Gradle
or any other build system.

## Repository layout

| Path | Contents |
|---|---|
| `src/MangaPlex.Core` | Shared contracts: opaque IDs, sort keys, the worker protocol |
| `src/MangaPlex.Server` | ASP.NET Core server: HTTP API (`/api/v1`), auth, catalog, persistence (SQLite + EF Core migrations) |
| `src/MangaPlex.MediaWorker` | Out-of-process archive/image worker, talks to the server over JSON-lines on stdin/stdout |
| `web/` | Angular web reader (Vitest unit tests, Playwright end-to-end tests) |
| `tests/` | xUnit test projects plus `MangaPlex.TestSupport` (synthetic fixtures) |
| `contracts/openapi.json` | The committed OpenAPI contract; changes to it are deliberate |
| `deploy/` | Dockerfile and Compose files |
| `scripts/` | Verification, smoke, and packaging scripts |
| `docs/` | User and operator documentation |

## Building and verifying

All verification is **script-driven**. Local runs and CI call the same scripts in
`scripts/`, so if a script passes on your machine it should also pass in CI. Run
them from the repository root.

| Tier | Command | What it runs | When to use it |
|---|---|---|---|
| Quick | `pwsh ./scripts/Verify-Quick.ps1` | Privacy preflight, `dotnet format` check, Debug build, all .NET tests | Your normal edit-build-test loop |
| Full | `pwsh ./scripts/Verify.ps1 -Configuration Release` | Privacy preflight, locked restore, `dotnet format` check, Release build, all .NET tests, `npm ci` + lint + production build of the web app, Compose file validation | Before opening or updating a pull request (this is what CI runs) |
| Contracts | `pwsh ./scripts/Verify-Contracts.ps1` | `Version.props` / `web/package.json` version match, contract/ordering/protocol tests, OpenAPI drift check (needs `web/node_modules`, so run it after the Full tier or `npm --prefix web ci`) | Any change to API routes, DTOs, EF migrations, the worker protocol, or versions |
| Smoke | `pwsh ./scripts/Smoke-Container.ps1` | Builds the image, runs it against a synthetic library with a read-only media mount, and exercises the full HTTP flow, restart persistence, source-media immutability, and log hygiene | Changes to hosting, Docker, storage, or anything the HTTP flow touches |
| Packaging | `pwsh ./scripts/Verify-Packaging.ps1` | Compose build, Windows self-contained publish (on Windows), the Smoke tier, and release packaging | Release candidates; resource-intensive |
| Safety review | `pwsh ./scripts/Review-Safety.ps1` | Read-only review of your diff for safety issues | Before a pull request that touches file access, auth, or logging |

The scripts never modify code to make a check pass. Fix the reported issue instead.
If `dotnet format` complains, run `dotnet format MangaPlex.slnx` and commit the result.

The privacy preflight accepts a normal clone with its `origin` remote. It fails
only if a remote URL embeds a credential, such as `https://user:token@host/...`
or a token-looking string; use a credential helper or SSH instead, and rotate any
secret that ended up in a URL.

The web app has two test suites the tiers above do not run. Run them yourself when
you change anything under `web/`:

```text
npm --prefix web run test:ci   # Vitest unit tests
npm --prefix web run e2e       # Playwright; needs a running instance (set E2E_BASE_URL)
```

Playwright targets `http://127.0.0.1:8091` by default. Point `E2E_BASE_URL` at
`ng serve` or at a locally built container.

### Container fallback (no host SDKs)

If you would rather not install the .NET SDK or Node on your machine, the same steps
run in the official images. From the repository root:

```bash
# .NET build and tests (add p7zip-full so the 7z fixture tests run)
docker run --rm -v "${PWD}:/workspace" -w /workspace mcr.microsoft.com/dotnet/sdk:10.0 \
    bash -c "apt-get update -qq && apt-get install -y -qq p7zip-full && \
             dotnet build MangaPlex.slnx -c Release && \
             dotnet test MangaPlex.slnx --no-build -c Release"

# Web app: install, lint, build, unit tests
docker run --rm -v "${PWD}/web:/workspace/web" -w /workspace/web node:24-bookworm-slim \
    sh -c "npm ci && npm run lint && npm run build && npm run test:ci"

# Container smoke test from a PowerShell container (needs the Docker socket)
docker run --rm -v /var/run/docker.sock:/var/run/docker.sock \
    -v "${PWD}:/workspace" -w /workspace mcr.microsoft.com/powershell:7.5 \
    pwsh ./scripts/Smoke-Container.ps1
```

The .NET SDK image includes PowerShell, so you can also run
`pwsh ./scripts/Verify-Quick.ps1` inside it.

### Running a local instance

To try your change in a browser, build the image and run it on a loopback port with
throwaway storage. Mount test media **read-only**:

```bash
docker build -f deploy/Dockerfile -t mangaplex:local .
docker run -d --name mangaplex-local -p 127.0.0.1:8091:8080 \
    -v "<temp>/data:/data" -v "<temp>/cache:/cache" -v "<temp>/scratch:/scratch" \
    -v "<your-test-media>:/media:ro" \
    mangaplex:local
```

A fresh instance has no users. The first-run setup screen creates the first
administrator. Clean up with `docker rm -f mangaplex-local` and delete the temp
directories when you are done.

## Rules every contribution must follow

These rules are what make MangaPlex safe to point at a real library. A pull request
that breaks one of them will not be merged, even if every test passes.

1. **Source media is read-only.** The server and worker must never modify, move,
   rename, delete, annotate, or extract into a library directory. Anything derived
   from media (thumbnails, page caches, scratch files) goes into the server's own
   data, cache, or scratch directories. Tests that need writable files create fresh
   per-test fixtures.
2. **No personal data in tracked files.** Do not commit real library paths, titles
   from your collection, credentials, user state, or machine-specific configuration.
   Tests use synthetic fixtures only, never a real collection or an arbitrary user
   directory. Media and archive files (`*.cbz`, `*.zip`, `*.7z`, ...) are never
   committed; generate fixtures at test time.
3. **No default credentials.** A fresh instance starts with zero users. The first
   administrator is created only through the first-run setup flow
   (`POST /api/v1/auth/setup`), which refuses once any user exists. Never add a
   built-in username or password, not even for testing or demos.
4. **Privacy in logs and bundles.** Logs contain IDs, counts, timings, and sanitized
   error codes. They must never contain absolute paths, titles, passwords, tokens,
   cookies, archive entry names, or page bytes. Browser bundles and source maps must
   not embed deployment paths. No telemetry, analytics, remote fonts, or third-party
   lookup calls.
5. **Wiring is part of the deliverable.** A new service is not finished until it is
   registered in dependency injection, reachable through a controller route or hosted
   service, and covered by at least one test that goes through that public surface
   (`WebApplicationFactory`, a spawned worker process, or Playwright). Service-level
   tests alone are not enough.
6. **Report tests by kind.** When you describe test results in a pull request, break
   the counts down into *unit / service-with-DB / HTTP / process / browser* so reviewers
   can see how much of the change is covered end to end.
7. **Contracts change deliberately.** API routes live under `/api/v1`. DTOs never expose
   source paths. Additive changes are fine. Breaking changes need prior discussion. If
   you touch the API, DTOs, migrations, or the worker protocol, run the Contracts tier
   and commit the regenerated `contracts/openapi.json` on purpose.
8. **Deterministic tests.** Tests must not depend on locale, time zone, wall-clock
   timing, or test ordering.

### Code conventions

- .NET namespaces start with `com.lifepixer.mangaplex`. Formatting follows
  [`.editorconfig`](.editorconfig) and is enforced by `dotnet format`. Warnings are
  treated as errors.
- The web app is linted with `npm --prefix web run lint` (ESLint + angular-eslint).
- Line endings are LF for text files (see [`.gitattributes`](.gitattributes)).

## Branches, commits, and pull requests

- **`dev`** is the integration branch. Branch from `dev` and target your pull request
  at `dev`.
- **`main`** tracks released versions only. It is updated by maintainers during a
  release. Do not open pull requests against `main`.
- **Releases** are tagged `v<version>` (for example `v1.12.0`) on `dev`, and `main` is
  then merged up to that release commit. Versions follow [SemVer 2.0.0](https://semver.org/).
  The version number lives in [`Version.props`](Version.props) and `web/package.json`.
  Maintainers bump it at release time, so leave both alone in pull requests.

### Commit messages

Commits use a conventional style, `type(scope): subject`, in the imperative mood:

```text
feat(browse): add a read-state filter to folder listings
fix(reader): keep the spread page pair in sync after a resize
test(server): cover the scan-all endpoint over HTTP
docs: explain the read-only media mount
```

Common types are `feat`, `fix`, `test`, `docs`, `refactor`, `perf`, `build`, `ci`, and
`chore`. Scopes are usually an area such as `reader`, `browse`, `catalog`, `server`,
`web`, or `admin`. Keep the subject short and use the body for the *why*.

### Pull request checklist

The [pull request template](.github/PULL_REQUEST_TEMPLATE.md) asks you to confirm that:

- the Full tier (`Verify.ps1 -Configuration Release`) passes, plus Contracts, Smoke,
  or the web test suites where relevant;
- tests are reported by kind;
- no personal data, real paths, or credentials were added;
- documentation, and the `[Unreleased]` section of [CHANGELOG.md](CHANGELOG.md), are
  updated for anything a user or operator would notice.

CI runs the same scripts on every pull request. A red CI run needs to be fixed before
review, not explained away.

## Reporting bugs

Use the [bug report form](../../issues/new?template=bug_report.yml) and include:

- the MangaPlex version (shown in the app footer, or reported by `GET /api/v1/system/info`);
- how you run it (Docker/Compose, Unraid, or Windows) and your browser/device;
- steps to reproduce, what you expected, and what happened;
- relevant log lines.

MangaPlex logs are designed not to contain paths or titles. Still, **read your logs
before pasting them** and remove anything personal, such as library paths, file names,
user names, IP addresses, or tokens.

## License

MangaPlex is released under the [MIT License](LICENSE). By submitting a contribution you
agree that it is licensed under the same terms.
