#Requires -Version 7.0
<#
    MangaPixer Verify-E2E.ps1
    Provisions a throwaway MangaPixer instance and runs the Playwright
    end-to-end (browser) suite against it.

    Steps, all self-contained (CI calls this script; it never re-implements
    the steps here):
      1. Build the release image from deploy/Dockerfile.
      2. Run the container on a free loopback port with throwaway state
         volumes and an empty synthetic media dir mounted read-only.
      3. Health-gate on GET /health.
      4. Create the first administrator via POST /api/v1/auth/setup. A fresh
         instance has zero users (no default credentials), so the login test
         in web/e2e/home.spec.ts needs an admin to exist first. The same
         credentials are handed to Playwright via E2E_ADMIN_USER /
         E2E_ADMIN_PASSWORD.
      5. Install the Playwright Chromium browser and run
         `npm --prefix web run e2e` with E2E_BASE_URL pointed at the container.
      6. ALWAYS tear down the container, state volumes, and temp dirs
         (try/finally), even when the suite fails.

    The HTML report is written to web/playwright-report/ (CI uploads it on
    failure). This script never publishes, tags, pushes, or writes to source
    media (the media mount is :ro and points at an empty throwaway dir).

    Usage: pwsh ./scripts/Verify-E2E.ps1
#>
[CmdletBinding()]
param(
    # Throwaway tag by design — this is a test harness, like Smoke-Container.ps1.
    [string]$ImageName = "mangapixer-e2e",
    [string]$ContainerName = "mangapixer-e2e-run",
    # 0 = pick a free loopback port automatically.
    [int]$HostPort = 0,
    # Credentials created by first-run setup and consumed by home.spec.ts.
    # These match the spec's defaults; they are ephemeral and per-run only.
    [string]$AdminUser = "admin",
    [string]$AdminPassword = "AdminPass123!",
    # Reuse an already-built image / an existing web/node_modules when iterating.
    [switch]$SkipBuild,
    [switch]$SkipNpmCi
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

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

function Get-FreeLoopbackPort {
    # Bind to port 0 on the loopback interface to let the OS hand us a free
    # port, then release it. A short TOCTOU window remains before the container
    # binds, which is acceptable for a single-run CI harness.
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try { return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port }
    finally { $listener.Stop() }
}

if ($HostPort -eq 0) { $HostPort = Get-FreeLoopbackPort }
$baseUrl = "http://127.0.0.1:${HostPort}"

# Throwaway per-run state (fresh named volumes => zero users => first-run setup),
# mirroring deploy/compose.yaml and Smoke-Container.ps1.
$stateVolumes = [ordered]@{
    "/data"    = "$ContainerName-data"
    "/cache"   = "$ContainerName-cache"
    "/scratch" = "$ContainerName-scratch"
}
# An empty media dir mounted read-only honours the source-media invariant.
# home.spec.ts only exercises login, so no library content is required.
$tempMedia = Join-Path ([System.IO.Path]::GetTempPath()) "mangapixer-e2e-media-$PID"

function Remove-E2EEnvironment {
    Write-Host "`n=== Teardown ===" -ForegroundColor Cyan
    docker rm -f $ContainerName 2>&1 | Out-Null
    foreach ($volume in $stateVolumes.Values) { docker volume rm -f $volume 2>&1 | Out-Null }
    Remove-Item -Path $tempMedia -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Teardown complete"
}

try {
    # Stage 1: Build the image (deploy/Dockerfile)
    if ($SkipBuild) {
        Write-Host "Skipping build (-SkipBuild); using existing image '$ImageName'." -ForegroundColor Yellow
        $results.Add([PSCustomObject]@{ Stage = "Docker build"; Status = "SKIPPED"; Duration = "n/a"; Error = "-SkipBuild" })
    }
    else {
        Invoke-Stage "Docker build" {
            docker build -f deploy/Dockerfile -t $ImageName . 2>&1 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
        }
    }

    # Stage 2: Run the container on the free loopback port with throwaway storage
    Invoke-Stage "Run container" {
        New-Item -ItemType Directory -Force -Path $tempMedia | Out-Null

        # Clear any leftovers from a previous run.
        docker rm -f $ContainerName 2>$null | Out-Null
        foreach ($volume in $stateVolumes.Values) { docker volume rm -f $volume 2>$null | Out-Null }

        $volumeArgs = foreach ($mount in $stateVolumes.GetEnumerator()) { "-v"; "$($mount.Value):$($mount.Key)" }
        docker run -d --name $ContainerName `
            -p "127.0.0.1:${HostPort}:8080" `
            -v "${tempMedia}:/media:ro" `
            @volumeArgs `
            -e Logging__LogLevel__Default=Information `
            $ImageName 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker run failed" }

        # Stage 3: Health-gate. A cold start (migrations, worker spawn) on a
        # two-core CI runner can take well over 30s, so allow 120s.
        $maxWait = 120
        $waited = 0
        while ($waited -lt $maxWait) {
            Start-Sleep -Seconds 2
            $waited += 2
            try {
                Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 3 | Out-Null
                Write-Host "Container healthy after ${waited}s at $baseUrl"
                return
            }
            catch {
                Write-Host "Waiting for health... (${waited}s)"
            }
        }
        throw "Container did not become healthy within ${maxWait}s"
    }

    # Stage 4: First-run setup — create the admin the login test signs in as.
    Invoke-Stage "First-run setup (create admin)" {
        $setupStatus = Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/setup-status" -TimeoutSec 5
        if (-not $setupStatus.setupRequired) { throw "Fresh instance does not report setupRequired" }

        $setupBody = @{ username = $AdminUser; password = $AdminPassword } | ConvertTo-Json
        $setup = Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/setup" -Method POST -Body $setupBody `
            -ContentType "application/json" -SkipHttpErrorCheck -TimeoutSec 10
        if ($setup.StatusCode -ge 300) {
            throw "Setup returned $($setup.StatusCode), expected 2xx"
        }
        Write-Host "Admin '$AdminUser' created via first-run setup"
    }

    # Stage 5a: Install web deps (Playwright lives in web/node_modules)
    if ($SkipNpmCi) {
        Write-Host "Skipping npm ci (-SkipNpmCi); using existing web/node_modules." -ForegroundColor Yellow
        $results.Add([PSCustomObject]@{ Stage = "npm ci (web)"; Status = "SKIPPED"; Duration = "n/a"; Error = "-SkipNpmCi" })
    }
    else {
        Invoke-Stage "npm ci (web)" {
            npm --prefix web ci --no-audit --no-fund 2>&1 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
        }
    }

    # Stage 5b: Install the Chromium browser Playwright drives.
    Invoke-Stage "Playwright install (chromium)" {
        # --with-deps installs OS packages via apt and is only supported on
        # Debian/Ubuntu (our CI runner). Off Linux, install the browser only.
        if ($IsLinux) {
            npm --prefix web exec -- playwright install --with-deps chromium 2>&1 | Out-Host
        }
        else {
            npm --prefix web exec -- playwright install chromium 2>&1 | Out-Host
        }
        if ($LASTEXITCODE -ne 0) { throw "playwright install failed" }
    }

    # Stage 5c: Run the Playwright e2e (browser) suite against the container.
    Invoke-Stage "Playwright e2e" {
        $env:E2E_BASE_URL = $baseUrl
        $env:E2E_ADMIN_USER = $AdminUser
        $env:E2E_ADMIN_PASSWORD = $AdminPassword
        try {
            npm --prefix web run e2e 2>&1 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "Playwright e2e failed (report in web/playwright-report/)" }
        }
        finally {
            Remove-Item Env:E2E_BASE_URL, Env:E2E_ADMIN_USER, Env:E2E_ADMIN_PASSWORD -ErrorAction SilentlyContinue
        }
    }
}
finally {
    # Always tear down, whether the suite passed, failed, or a stage threw.
    Remove-E2EEnvironment
}

# Summary. The e2e suite is the "browser" test kind; per-test counts are in the
# Playwright output above and in web/playwright-report/.
Write-Host "`n=== Verify-E2E Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All e2e (browser) checks passed." -ForegroundColor Green
