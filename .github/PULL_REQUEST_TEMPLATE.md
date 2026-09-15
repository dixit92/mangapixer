<!--
Thanks for contributing to MangaPlex. Pull requests target `dev`; `main` is
release-only. See CONTRIBUTING.md for the full rules.
-->

## Summary

<!-- What does this change and why? Link the issue it resolves, e.g. "Fixes #123". -->

## Verification

Tiers run (see CONTRIBUTING.md, "Building and verifying"):

- [ ] Full: `pwsh ./scripts/Verify.ps1 -Configuration Release`
- [ ] Contracts: `pwsh ./scripts/Verify-Contracts.ps1` (API, DTO, migration, worker-protocol, or version changes)
- [ ] Smoke: `pwsh ./scripts/Smoke-Container.ps1` (hosting, Docker, storage, or HTTP-flow changes)
- [ ] Web: `npm --prefix web run test:ci` and, where relevant, `npm --prefix web run e2e` (changes under `web/`)

Tests by kind (counts, passed / total):

| Unit | Service-with-DB | HTTP | Process | Browser |
|---|---|---|---|---|
|   |   |   |   |   |

## Checklist

- [ ] Source media stays read-only. Nothing writes, moves, renames, deletes, or extracts into library folders.
- [ ] No personal data, real paths, titles, or credentials in code, tests, fixtures, logs, or screenshots.
- [ ] No default credentials, telemetry, or third-party network calls added.
- [ ] New services are wired up (DI + a route or hosted service) and covered by at least one test through that public surface.
- [ ] `contracts/openapi.json` is unchanged, or regenerated on purpose and explained above.
- [ ] Documentation and the `[Unreleased]` section of `CHANGELOG.md` are updated for anything users or operators will notice.
- [ ] `Version.props` and `web/package.json` are untouched (maintainers bump them at release).
