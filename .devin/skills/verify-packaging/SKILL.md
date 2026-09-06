---
name: verify-packaging
description: Build and smoke packaged Linux/Windows targets (resource intensive, user invocation recommended)
allowed-tools:
  - read
  - grep
  - glob
  - exec
permissions:
  allow:
    - Exec(pwsh ./scripts/Verify-Packaging.ps1)
    - Exec(pwsh scripts/Verify-Packaging.ps1)
    - Exec(docker compose -f deploy/compose.yaml config)
    - Exec(docker compose -f deploy/compose.yaml build)
    - Exec(docker image inspect)
    - Exec(git status)
    - Exec(git remote -v)
triggers:
  - user
---

Run the MangaPlex packaging verification script and report results.

Execute: `pwsh ./scripts/Verify-Packaging.ps1`

This is resource intensive. It:
- Checks for a clean tree and no remote
- Validates Docker Compose configuration
- Builds the Linux container image
- Publishes a Windows self-contained binary (only on Windows with .NET SDK)
- Never publishes, tags, pushes, installs host tools, or alters media mounts

Report:
- Which stages passed, failed, or were skipped
- For any failure, include the error message and relevant build log
- Windows packaging requires a Windows runner; cross-publishing on Linux is not sufficient
- Do NOT modify code to hide or suppress failures

If `pwsh` is not available, report that PowerShell 7 is required and suggest using the Docker dev container instead.
