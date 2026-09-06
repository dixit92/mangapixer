---
name: verify-contracts
description: Run contract drift and compatibility checks for API/DTO/migration/protocol/version changes
allowed-tools:
  - read
  - grep
  - glob
  - exec
permissions:
  allow:
    - Exec(pwsh ./scripts/Verify-Contracts.ps1)
    - Exec(pwsh scripts/Verify-Contracts.ps1)
    - Exec(git status)
    - Exec(git diff)
triggers:
  - user
  - model
---

Run the MangaPlex contract verification script and report results.

Execute: `pwsh ./scripts/Verify-Contracts.ps1`

This checks:
- Version consistency between Version.props and package.json
- OpenAPI drift (regenerate and compare)
- Worker protocol contract tests
- SemVer/build-identity checks
- Migration model snapshot check

Report:
- Which checks passed, failed, or were skipped
- For any failure, identify the owning contract file and the drift
- Do NOT modify code to hide or suppress failures
- Generated API schema types are never hand-edited; if drift is found, regenerate

If `pwsh` is not available, report that PowerShell 7 is required and suggest using the Docker dev container instead.
