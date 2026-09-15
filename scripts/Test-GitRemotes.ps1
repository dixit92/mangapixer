#Requires -Version 7.0
<#
    Test-GitRemotes.ps1
    Privacy preflight check shared by Verify-Quick.ps1, Verify.ps1 and
    Verify-Packaging.ps1.

    A git remote is expected (the repository is hosted on GitHub, and every
    clone has "origin"). What must never happen is a remote URL that carries a
    secret, because `git remote -v`, CI logs and bug reports print it. Throws
    if any fetch or push URL embeds credentials (`scheme://user:secret@host`)
    or a token-looking string (GitHub/GitLab token prefixes, or a long opaque
    user-info part such as `https://<token>@host`). Otherwise prints the
    remote names. Never prints a URL, so a leaked secret is not echoed.

    Usage: & ./scripts/Test-GitRemotes.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Known token formats, matched anywhere in the URL (user-info, path or query).
$tokenPattern = '(gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{20,}|glpat-[A-Za-z0-9_-]{20,})'

$names = @(git remote 2>$null | Where-Object { $_ })
if ($LASTEXITCODE -ne 0) { throw "git remote failed (is this a git repository?)" }

if ($names.Count -eq 0) {
    Write-Host "No git remote configured."
    return
}

$problems = [System.Collections.Generic.List[string]]::new()
foreach ($name in $names) {
    $urls = @(git remote get-url --all $name 2>$null) + @(git remote get-url --push --all $name 2>$null)
    foreach ($url in ($urls | Where-Object { $_ } | Select-Object -Unique)) {
        if ($url -match '://([^/@\s]+)@') {
            $userInfo = $Matches[1]
            if ($userInfo.Contains(':')) {
                $problems.Add("remote '$name' has a URL with embedded credentials (user:secret@)")
                continue
            }
            if ($userInfo -match '^[A-Za-z0-9_-]{20,}$') {
                $problems.Add("remote '$name' has a URL whose user-info looks like a token")
                continue
            }
        }
        if ($url -match $tokenPattern) {
            $problems.Add("remote '$name' has a URL containing a token-looking string")
        }
    }
}

if ($problems.Count -gt 0) {
    throw ("Git remote URL leaks a credential: " + ($problems -join "; ") +
        ". Use a credential helper or SSH instead, and rotate the exposed secret.")
}

Write-Host "Git remotes: $($names -join ', ') (no embedded credentials)"
