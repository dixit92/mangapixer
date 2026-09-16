#Requires -Version 7.0
<#
    MangaPixer Smoke-Container.ps1
    Builds the Docker image, runs it with a synthetic library mounted
    read-only and fresh state volumes, and exercises the HTTP flow: health,
    first-run setup (no default credentials), csrf, password change and
    sign-in, add library, scan, browse, manifest, page by entry key, cover,
    then source-media immutability and log hygiene.

    Never publishes, tags, pushes, or alters source media mounts.

    Usage: pwsh ./scripts/Smoke-Container.ps1
           docker run --rm -v /var/run/docker.sock:/var/run/docker.sock `
             -v "${PWD}:/workspace" -w /workspace `
             mcr.microsoft.com/powershell:7.5 `
             pwsh ./scripts/Smoke-Container.ps1
#>
[CmdletBinding()]
param(
    # Throwaway tag by design — this is a test harness. Immutable, version-stamped
    # release tagging lives in Package-Release.ps1 (audit finding F1).
    [string]$ImageName = "mangapixer-smoke",
    [string]$ContainerName = "mangapixer-smoke-run",
    [int]$HostPort = 18080
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

# Stage 1: Build the Docker image
Invoke-Stage "Docker build" {
    docker build -f deploy/Dockerfile -t $ImageName . 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
}

# Stage 2: Create a synthetic library with spaces in the path
$tempLib = Join-Path ([System.IO.Path]::GetTempPath()) "mangapixer smoke lib"
Invoke-Stage "Create synthetic library" {
    New-Item -ItemType Directory -Force -Path $tempLib | Out-Null
    # Create a simple CBZ with 3 PNG pages
    $zipPath = Join-Path $tempLib "Test Archive.cbz"
    # Use .NET to create a minimal ZIP
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        # Minimal valid PNG: 1x1 red pixel (checksums verified, so the worker
        # can actually decode and re-encode it)
        $pngBytes = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
            0x54, 0x78, 0xDA, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
            0x00, 0x03, 0x01, 0x01, 0x00, 0xF7, 0x03, 0x41,
            0x43, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
            0x44, 0xAE, 0x42, 0x60, 0x82)
        foreach ($name in @("page001.png", "page002.png", "page003.png")) {
            $entry = $zip.CreateEntry($name)
            $stream = $entry.Open()
            $stream.Write($pngBytes, 0, $pngBytes.Length)
            $stream.Close()
        }
    }
    finally {
        $zip.Dispose()
    }
    Write-Host "Synthetic library at: $tempLib"
}

# Stage 3: Run the container with :ro media mount
$mediaMarker = Join-Path $tempLib ".smoke-marker"
# Fresh named state volumes per run, mirroring deploy/compose.yaml. The server
# runs as UID 1000 and cannot create /data, /cache or /scratch at the
# filesystem root itself, and a fresh /data means zero users (first-run setup).
$stateVolumes = [ordered]@{
    "/data"    = "$ContainerName-data"
    "/cache"   = "$ContainerName-cache"
    "/scratch" = "$ContainerName-scratch"
}
Invoke-Stage "Run container" {
    New-Item -ItemType File -Path $mediaMarker -Force | Out-Null
    # -Force: on Linux, PowerShell treats dot-files as hidden and Get-Item /
    # Get-ChildItem skip them without it (the CI runner failed here).
    $mediaMarkerTime = (Get-Item -Force $mediaMarker).LastWriteTime
    Write-Host "Media marker created at: $mediaMarkerTime"

    # Remove any existing container and state from an earlier run
    docker rm -f $ContainerName 2>$null | Out-Null
    foreach ($volume in $stateVolumes.Values) { docker volume rm -f $volume 2>$null | Out-Null }

    # Run with read-only media mount and fresh state volumes
    $volumeArgs = foreach ($mount in $stateVolumes.GetEnumerator()) { "-v"; "$($mount.Value):$($mount.Key)" }
    docker run -d --name $ContainerName `
        -p "${HostPort}:8080" `
        -v "${tempLib}:/media:ro" `
        @volumeArgs `
        -e Logging__LogLevel__Default=Information `
        $ImageName 2>&1 | Out-Host

    if ($LASTEXITCODE -ne 0) { throw "docker run failed" }

    # Wait for health
    # Generous limits: a cold start on a two-core CI runner (first-run migrations,
    # worker spawn) can take well over 30 s.
    $maxWait = 120
    $waited = 0
    while ($waited -lt $maxWait) {
        Start-Sleep -Seconds 2
        $waited += 2
        try {
            $health = Invoke-RestMethod -Uri "http://localhost:${HostPort}/health" -TimeoutSec 3
            Write-Host "Container healthy after ${waited}s"
            return
        }
        catch {
            Write-Host "Waiting for health... (${waited}s)"
        }
    }
    throw "Container did not become healthy within ${maxWait}s"
}

# Stage 4: HTTP smoke flow
Invoke-Stage "HTTP smoke flow" {
    $baseUrl = "http://localhost:${HostPort}"

    # Health
    $health = Invoke-RestMethod -Uri "$baseUrl/health" -TimeoutSec 5
    Write-Host "Health: OK"

    # No default credentials: a fresh data volume has zero users, so the
    # instance must report first-run setup and reject a well-known login.
    $setupStatus = Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/setup-status" -TimeoutSec 5
    if (-not $setupStatus.setupRequired) { throw "Fresh instance does not report setupRequired" }
    $defaultLogin = Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/login" -Method POST -SkipHttpErrorCheck `
        -Body (@{ username = "admin"; password = "admin" } | ConvertTo-Json) -ContentType "application/json" -TimeoutSec 5
    if ($defaultLogin.StatusCode -ne 401) { throw "Default admin/admin login returned $($defaultLogin.StatusCode), expected 401" }
    Write-Host "No default credentials: setup required, admin/admin rejected"

    # First-run setup creates the admin and signs it in; a second setup is refused
    $adminName = "smoke-admin"
    $setupPassword = "SmokeSetup123!"
    $setupBody = @{ username = $adminName; password = $setupPassword } | ConvertTo-Json
    Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/setup" -Method POST -Body $setupBody `
        -ContentType "application/json" -SessionVariable session -TimeoutSec 5 | Out-Null
    $secondSetup = Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/setup" -Method POST -Body $setupBody `
        -ContentType "application/json" -SkipHttpErrorCheck -TimeoutSec 5
    if ($secondSetup.StatusCode -ne 409) { throw "Second setup returned $($secondSetup.StatusCode), expected 409" }
    Write-Host "Setup: admin created; second setup refused (409)"

    # CSRF tokens are bound to the signed-in user, so fetch one after sign-in
    function Get-CsrfToken($webSession) {
        (Invoke-RestMethod -Uri "$baseUrl/api/v1/auth/csrf" -WebSession $webSession -TimeoutSec 5).token
    }
    $csrfToken = Get-CsrfToken $session
    if (-not $csrfToken) { throw "No CSRF token returned" }
    Write-Host "CSRF token acquired"

    # Change password (this revokes every session, the current one included),
    # then sign in again with the new password
    $newPassword = "SmokeTest123!"
    $changeBody = @{ currentPassword = $setupPassword; newPassword = $newPassword } | ConvertTo-Json
    Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/change-password" -Method POST -Body $changeBody `
        -ContentType "application/json" -Headers @{ "X-MangaPixer-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5 | Out-Null
    Write-Host "Password changed"
    $loginBody = @{ username = $adminName; password = $newPassword } | ConvertTo-Json
    Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/login" -Method POST -Body $loginBody `
        -ContentType "application/json" -SessionVariable session -TimeoutSec 5 | Out-Null
    $csrfToken = Get-CsrfToken $session
    Write-Host "Login with new password: OK"

    # Add library
    $libBody = @{ displayName = "Smoke Library"; rootPath = "/media" } | ConvertTo-Json
    $libJson = Invoke-RestMethod -Uri "$baseUrl/api/v1/admin/libraries" -Method POST -Body $libBody `
        -ContentType "application/json" -Headers @{ "X-MangaPixer-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5
    $libraryId = $libJson.id
    if (-not $libraryId) { throw "Library registration returned no id" }
    Write-Host "Library added: $libraryId"

    # Trigger scan
    Invoke-WebRequest -Uri "$baseUrl/api/v1/admin/libraries/$libraryId/scan" -Method POST `
        -Headers @{ "X-MangaPixer-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5 | Out-Null
    Write-Host "Scan triggered"

    # Poll the latest scan run until it finishes
    $maxScanWait = 120
    $scanWaited = 0
    $scanState = $null
    while ($scanWaited -lt $maxScanWait) {
        Start-Sleep -Seconds 2
        $scanWaited += 2
        $scans = @(Invoke-RestMethod -Uri "$baseUrl/api/v1/admin/libraries/$libraryId/scans" `
            -WebSession $session -TimeoutSec 5)
        $scanState = if ($scans.Count -gt 0) { $scans[0].status } else { "none" }
        Write-Host "Scan status: $scanState (${scanWaited}s)"
        if ($scanState -in @("completed", "failed", "cancelled", "interrupted")) { break }
    }
    if ($scanState -ne "completed") { throw "Scan did not complete (last status: $scanState)" }

    # Browse to find the archive
    $browse = Invoke-RestMethod -Uri "$baseUrl/api/v1/libraries/$libraryId/browse" `
        -WebSession $session -TimeoutSec 5
    $archive = $browse.items | Where-Object { $_.kind -eq 1 -or $_.kind -eq "Archive" } | Select-Object -First 1
    if (-not $archive) { throw "No archive found in browse results" }
    $itemId = $archive.id
    Write-Host "Archive found: $itemId"

    # Fetch manifest (may 202 then 200)
    $maxManifestWait = 120
    $manifestWaited = 0
    $manifest = $null
    while ($manifestWaited -lt $maxManifestWait) {
        try {
            $resp = Invoke-WebRequest -Uri "$baseUrl/api/v1/items/$itemId/manifest" `
                -WebSession $session -TimeoutSec 5
            if ($resp.StatusCode -eq 200) {
                $manifest = $resp.Content | ConvertFrom-Json
                Write-Host "Manifest ready: $($manifest.pageCount) pages"
                break
            }
        }
        catch {
            $status = $_.Exception.Response.StatusCode.value__
            if ($status -eq 202) {
                Write-Host "Manifest preparing... (${manifestWaited}s)"
            }
            else {
                throw
            }
        }
        Start-Sleep -Seconds 2
        $manifestWaited += 2
    }
    if (-not $manifest) { throw "Manifest not ready within ${maxManifestWait}s" }

    # Fetch first page by entry key
    $firstPage = $manifest.pages[0]
    $entryKey = $firstPage.entryKey
    $pageResp = Invoke-WebRequest -Uri "$baseUrl/api/v1/items/$itemId/pages/$entryKey" `
        -WebSession $session -TimeoutSec 10
    Write-Host "Page fetched: $($pageResp.Headers['Content-Type'])"
    if ($pageResp.Content.Length -eq 0) { throw "Page content empty" }

    # Verify cache headers (D9)
    $cacheControl = $pageResp.Headers["Cache-Control"]
    if (-not $cacheControl) { throw "Missing Cache-Control header" }
    Write-Host "Cache-Control: $cacheControl"

    # Fetch cover (202 while the thumbnail is still being generated)
    $coverResp = $null
    for ($coverWaited = 0; $coverWaited -lt 120; $coverWaited += 2) {
        $coverResp = Invoke-WebRequest -Uri "$baseUrl/api/v1/items/$itemId/cover" `
            -WebSession $session -SkipHttpErrorCheck -TimeoutSec 10
        if ($coverResp.StatusCode -ne 202) { break }
        Start-Sleep -Seconds 2
    }
    if ($coverResp.StatusCode -ne 200) { throw "Cover returned $($coverResp.StatusCode), expected 200" }
    Write-Host "Cover fetched: $($coverResp.RawContentLength) bytes"

    Write-Host "HTTP smoke flow complete"
}

# Stage 5: Source media immutability check
Invoke-Stage "Source media immutability" {
    # Verify no files in the media directory were modified after the marker
    $markerTime = (Get-Item -Force $mediaMarker).LastWriteTime
    $violations = Get-ChildItem -Path $tempLib -Recurse -File -Force |
        Where-Object { $_.LastWriteTime -gt $markerTime -and $_.Name -ne ".smoke-marker" }
    if ($violations) {
        $violationList = $violations | ForEach-Object { $_.FullName }
        throw "Source media was modified: $($violationList -join ', ')"
    }
    Write-Host "Source media unchanged"
}

# Stage 6: Log hygiene check
Invoke-Stage "Log hygiene" {
    $logs = docker logs $ContainerName 2>&1
    $logString = $logs -join "`n"
    # Check for forbidden patterns
    if ($logString -match "/media/") { throw "Logs contain /media/ path" }
    if ($logString -match "InvalidOperationException") { throw "Logs contain InvalidOperationException" }
    if ($logString -match "disk full") { throw "Logs contain 'disk full'" }
    Write-Host "Log hygiene: clean"
}

# Stage 7: Cleanup
Invoke-Stage "Cleanup" {
    docker rm -f $ContainerName 2>&1 | Out-Null
    foreach ($volume in $stateVolumes.Values) { docker volume rm -f $volume 2>&1 | Out-Null }
    Remove-Item -Path $tempLib -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Cleanup complete"
}

# Summary
Write-Host "`n=== Smoke-Container Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All smoke checks passed." -ForegroundColor Green
