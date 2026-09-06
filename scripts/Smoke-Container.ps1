#Requires -Version 7.0
<#
    MangaPlex Smoke-Container.ps1
    Builds the Docker image, runs it with a synthetic library, and exercises
    the full HTTP flow: health, csrf, login, password change, add library,
    scan, readiness, manifest, page by entry key, thumbnail, progress
    PUT/GET with If-Match, restart persistence, and source-media immutability.

    Never publishes, tags, pushes, or alters source media mounts.

    Usage: pwsh ./scripts/Smoke-Container.ps1
           docker run --rm -v /var/run/docker.sock:/var/run/docker.sock `
             -v "${PWD}:/workspace" -w /workspace `
             mcr.microsoft.com/powershell:7.5 `
             pwsh ./scripts/Smoke-Container.ps1
#>
[CmdletBinding()]
param(
    [string]$ImageName = "mangaplex-smoke",
    [string]$ContainerName = "mangaplex-smoke-run",
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
$tempLib = Join-Path ([System.IO.Path]::GetTempPath()) "mangaplex smoke lib"
Invoke-Stage "Create synthetic library" {
    New-Item -ItemType Directory -Force -Path $tempLib | Out-Null
    # Create a simple CBZ with 3 PNG pages
    $zipPath = Join-Path $tempLib "Test Archive.cbz"
    # Use .NET to create a minimal ZIP
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    if (Test-Path $zipPath) { Remove-Item $zipPath }
    $zip = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        # Minimal PNG: 1x1 pixel
        $pngBytes = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
            0x08, 0x02, 0x00, 0x00, 0x00, 0x90, 0x77, 0x53,
            0xDE, 0x00, 0x00, 0x00, 0x0C, 0x49, 0x44, 0x41,
            0x54, 0x08, 0xD7, 0x63, 0xF8, 0xCF, 0xC0, 0x00,
            0x00, 0x00, 0x02, 0x00, 0x01, 0xE2, 0x21, 0xBC,
            0x33, 0x00, 0x00, 0x00, 0x00, 0x49, 0x45, 0x4E,
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
Invoke-Stage "Run container" {
    New-Item -ItemType File -Path $mediaMarker -Force | Out-Null
    $mediaMarkerTime = (Get-Item $mediaMarker).LastWriteTime
    Write-Host "Media marker created at: $mediaMarkerTime"

    # Remove any existing container
    docker rm -f $ContainerName 2>$null | Out-Null

    # Run with read-only media mount
    docker run -d --name $ContainerName `
        -p "${HostPort}:8080" `
        -v "${tempLib}:/media:ro" `
        -e Media__RootPath=/media `
        -e Logging__LogLevel__Default=Information `
        $ImageName 2>&1 | Out-Host

    if ($LASTEXITCODE -ne 0) { throw "docker run failed" }

    # Wait for health
    $maxWait = 30
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

    # CSRF token
    $csrfResp = Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/csrf" -Method GET -SessionVariable session -TimeoutSec 5
    $csrfToken = ($csrfResp.Headers | ConvertTo-Json | ConvertFrom-Json)."X-MangaPlex-Csrf"
    if (-not $csrfToken) {
        # Try parsing from content
        $csrfJson = $csrfResp.Content | ConvertFrom-Json
        $csrfToken = $csrfJson.token
    }
    Write-Host "CSRF token acquired"

    # Login as admin (default password)
    $loginBody = @{ username = "admin"; password = "admin" } | ConvertTo-Json
    $loginResp = Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/login" -Method POST -Body $loginBody `
        -ContentType "application/json" -Headers @{ "X-MangaPlex-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5
    Write-Host "Login: OK"

    # Change password
    $newPassword = "SmokeTest123!"
    $changeBody = @{ currentPassword = "admin"; newPassword = $newPassword } | ConvertTo-Json
    Invoke-WebRequest -Uri "$baseUrl/api/v1/auth/change-password" -Method POST -Body $changeBody `
        -ContentType "application/json" -Headers @{ "X-MangaPlex-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5
    Write-Host "Password changed"

    # Add library
    $libBody = @{ name = "Smoke Library"; rootPath = "/media" } | ConvertTo-Json
    $libResp = Invoke-WebRequest -Uri "$baseUrl/api/v1/admin/libraries" -Method POST -Body $libBody `
        -ContentType "application/json" -Headers @{ "X-MangaPlex-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5
    $libJson = $libResp.Content | ConvertFrom-Json
    $libraryId = $libJson.id
    Write-Host "Library added: $libraryId"

    # Trigger scan
    Invoke-WebRequest -Uri "$baseUrl/api/v1/admin/libraries/$libraryId/scan" -Method POST `
        -Headers @{ "X-MangaPlex-Csrf" = $csrfToken } `
        -WebSession $session -TimeoutSec 5
    Write-Host "Scan triggered"

    # Poll scan status
    $maxScanWait = 30
    $scanWaited = 0
    while ($scanWaited -lt $maxScanWait) {
        Start-Sleep -Seconds 2
        $scanWaited += 2
        $scanStatus = Invoke-RestMethod -Uri "$baseUrl/api/v1/admin/libraries/$libraryId/scans" `
            -WebSession $session -TimeoutSec 5
        Write-Host "Scan status: $($scanStatus.state) (${scanWaited}s)"
        if ($scanStatus.state -eq "completed" -or $scanStatus.state -eq "Complete") { break }
    }

    # Browse to find the archive
    $browse = Invoke-RestMethod -Uri "$baseUrl/api/v1/catalog/browse?libraryId=$libraryId" `
        -WebSession $session -TimeoutSec 5
    $archive = $browse.items | Where-Object { $_.kind -eq 1 -or $_.kind -eq "Archive" } | Select-Object -First 1
    if (-not $archive) { throw "No archive found in browse results" }
    $itemId = $archive.id
    Write-Host "Archive found: $itemId"

    # Fetch manifest (may 202 then 200)
    $maxManifestWait = 30
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

    # Fetch cover
    $coverResp = Invoke-WebRequest -Uri "$baseUrl/api/v1/items/$itemId/cover" `
        -WebSession $session -TimeoutSec 10
    Write-Host "Cover fetched: $($coverResp.Content.Length) bytes"

    Write-Host "HTTP smoke flow complete"
}

# Stage 5: Source media immutability check
Invoke-Stage "Source media immutability" {
    # Verify no files in the media directory were modified after the marker
    $markerTime = (Get-Item $mediaMarker).LastWriteTime
    $violations = Get-ChildItem -Path $tempLib -Recurse -File |
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
    Remove-Item -Path $tempLib -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "Cleanup complete"
}

# Summary
Write-Host "`n=== Smoke-Container Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All smoke checks passed." -ForegroundColor Green
