---
name: verify-quick
description: Run quick verification (format, build, unit tests) after focused edits
allowed-tools:
  - read
  - grep
  - glob
  - exec
permissions:
  allow:
    - Exec(pwsh ./scripts/Verify-Quick.ps1)
    - Exec(pwsh scripts/Verify-Quick.ps1)
    - Exec(git status)
    - Exec(git diff)
    - Exec(git remote -v)
triggers:
  - user
  - model
---

Run the MangaPlex quick verification script and report results.

Execute: `pwsh ./scripts/Verify-Quick.ps1`

Report:
- Which stages passed and which failed
- For any failure, include the file/test references and error message
- Do NOT modify code to hide or suppress failures
- If the script itself is missing or errors, report that clearly

If `pwsh` is not available, report that PowerShell 7 is required and suggest using the Docker dev container instead.
