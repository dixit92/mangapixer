#Requires -Version 7.0
<#
    MangaPixer Verify.ps1 (Full)
    Full release-mode verification before declaring a work package complete.
    This is exactly what CI runs (.github/workflows/ci.yml, check "Verify (Full tier)").

    Stages, in order:
      1. Privacy preflight (remote-credential / ignored-config / tracked-media scan)
      2. dotnet restore (locked)
      3. dotnet format --verify-no-changes
      4. dotnet build (Release)
      5. dotnet test (all .NET suites: unit, service-with-DB, HTTP, process)
      6. npm ci               (web/, when npm is on PATH)
      7. npm run lint         (web/)
      8. npm run build        (web/, Angular production build)
      9. npm run test:ci      (web/, Vitest unit suite via Angular CLI, single run)
     10. docker compose config (when docker is on PATH)

    NOT run here: Playwright e2e (`npm run e2e`). Playwright targets an
    already-running instance (see web/playwright.config.ts, default
    127.0.0.1:8091) which this tier does not provision, so e2e stays a manual /
    reviewer step (CONTRIBUTING.md, the live review instance in AGENTS.md, and
    the PR template). The Vitest unit suite above needs no server and runs here.

    Usage: pwsh ./scripts/Verify.ps1 -Configuration Release
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$artifactsDir = Join-Path $repoRoot "artifacts"
if (-not (Test-Path $artifactsDir)) { New-Item -ItemType Directory -Path $artifactsDir | Out-Null }

$results = [System.Collections.Generic.List[PSCustomObject]]::new()

function Invoke-Stage {
    param([string]$Name, [scriptblock]$Action)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action
        $sw.Stop()
        $results.Add([PSCustomObject]@{ Stage = $Name; Status = "PASS"; Duration = $sw.Elapsed.ToString() })
        Write-Host "PASS: $Name ($($sw.Elapsed))" -ForegroundColor Green
    }
    catch {
        $sw.Stop()
        $results.Add([PSCustomObject]@{ Stage = $Name; Status = "FAIL"; Duration = $sw.Elapsed.ToString(); Error = $_.Exception.Message })
        Write-Host "FAIL: $Name ($($sw.Elapsed))" -ForegroundColor Red
        Write-Host $_.Exception.Message -ForegroundColor Red
        throw
    }
}

# Stage 1: Privacy preflight
Invoke-Stage "Privacy Preflight" {
    # A remote is expected; fail only if a remote URL embeds a credential.
    & "$PSScriptRoot/Test-GitRemotes.ps1"

    $ignored = git check-ignore .devin/config.local.json 2>&1
    if ($LASTEXITCODE -ne 0) { throw ".devin/config.local.json is not ignored" }

    $status = git status --short 2>&1
    $diffCheck = git diff --check 2>&1
    if ($diffCheck) { throw "git diff --check found whitespace errors" }

    $mediaExts = @("*.cbz", "*.cbr", "*.cb7", "*.zip", "*.rar", "*.7z")
    foreach ($ext in $mediaExts) {
        $tracked = git ls-files $ext 2>&1
        if ($tracked) { throw "Media file is tracked: $tracked" }
    }
}

# Stage 2: Clean restore
Invoke-Stage "dotnet restore (locked)" {
    dotnet restore MangaPixer.slnx --locked-mode 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Locked restore failed, falling back to normal restore..." -ForegroundColor Yellow
        dotnet restore MangaPixer.slnx 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
    }
}

# Stage 3: Format check
Invoke-Stage "dotnet format verify" {
    dotnet format MangaPixer.slnx --verify-no-changes --no-restore 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet format found changes needed" }
}

# Stage 4: Build
Invoke-Stage "dotnet build ($Configuration)" {
    dotnet build MangaPixer.slnx --no-restore -c $Configuration 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}

# Stage 5: All tests
Invoke-Stage "dotnet test (all)" {
    dotnet test MangaPixer.slnx --no-build -c $Configuration --verbosity normal 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }
}

# Stage 6: Angular restore and lint (if npm is available)
$npmAvailable = [bool](Get-Command npm -ErrorAction SilentlyContinue)
if ($npmAvailable) {
    Invoke-Stage "npm ci" {
        npm --prefix web ci --no-audit --no-fund 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    }
    Invoke-Stage "npm lint" {
        npm --prefix web run lint 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "npm lint failed" }
    }
    Invoke-Stage "npm build" {
        npm --prefix web run build 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "npm build failed" }
    }
    # Angular unit tests: Vitest via Angular CLI (test target has watch:false, so
    # this runs the suite once and exits). No running server needed — unlike e2e.
    Invoke-Stage "npm test:ci" {
        npm --prefix web run test:ci 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "npm test:ci failed" }
    }
}
else {
    Write-Host "npm not available on host — Angular verification requires container or host Node." -ForegroundColor Yellow
    $results.Add([PSCustomObject]@{ Stage = "Angular (npm)"; Status = "SKIPPED"; Duration = "n/a"; Error = "npm not on PATH" })
}

# Stage 7: Docker compose config validation
$dockerAvailable = [bool](Get-Command docker -ErrorAction SilentlyContinue)
if ($dockerAvailable) {
    Invoke-Stage "docker compose config" {
        docker compose -f deploy/compose.yaml config 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker compose config validation failed" }
    }
}
else {
    $results.Add([PSCustomObject]@{ Stage = "Docker compose config"; Status = "SKIPPED"; Duration = "n/a"; Error = "docker not on PATH" })
}

# Summary
Write-Host "`n=== Verify-Full Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All full checks passed (skipped stages reported above)." -ForegroundColor Green
