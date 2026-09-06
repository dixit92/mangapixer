---
name: verify-full
description: Run full release-mode verification before declaring a package complete
allowed-tools:
  - read
  - grep
  - glob
  - exec
permissions:
  allow:
    - Exec(pwsh ./scripts/Verify.ps1)
    - Exec(pwsh scripts/Verify.ps1)
    - Exec(git status)
    - Exec(git diff)
    - Exec(git remote -v)
    - Exec(git log)
triggers:
  - user
  - model
---

Run the MangaPlex full verification script and report results.

Execute: `pwsh ./scripts/Verify.ps1 -Configuration Release`

Report:
- Which stages passed, failed, or were skipped
- For any failure, include the file/test references and error message
- For skipped stages, explain why and whether they need a different runner
- Report pass/fail/skip counts, coverage if available, and elapsed time
- Do NOT modify code to hide or suppress failures
- A skipped required test is a failure unless its platform gate is explicitly assigned to another runner

If `pwsh` is not available, report that PowerShell 7 is required and suggest using the Docker dev container instead.
