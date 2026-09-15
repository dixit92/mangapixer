#Requires -Version 7.0
<#
    MangaPlex Verify-Packaging.ps1
    Build and smoke packaged Linux/Windows targets.
    Never publishes, tags, pushes, installs host tools, or alters media mounts.
    User invocation recommended because it is resource intensive.

    Usage: pwsh ./scripts/Verify-Packaging.ps1
#>
[CmdletBinding()]
param()

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

# Stage 1: Clean tree check
Invoke-Stage "Clean tree check" {
    $status = git status --short 2>&1
    if ($status) {
        Write-Host "Working tree is not clean:" -ForegroundColor Yellow
        Write-Host $status
    }
    # A remote is expected; fail only if a remote URL embeds a credential.
    & "$PSScriptRoot/Test-GitRemotes.ps1"
}

# Stage 2: Docker compose build
$dockerAvailable = [bool](Get-Command docker -ErrorAction SilentlyContinue)
if ($dockerAvailable) {
    Invoke-Stage "docker compose config" {
        docker compose -f deploy/compose.yaml config 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker compose config failed" }
    }
    Invoke-Stage "docker compose build" {
        docker compose -f deploy/compose.yaml build 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "docker compose build failed" }
    }
}
else {
    $results.Add([PSCustomObject]@{ Stage = "Docker build"; Status = "SKIPPED"; Duration = "n/a"; Error = "docker not on PATH" })
}

# Stage 3: Windows self-contained publish (only on Windows or with explicit approval)
$isWindows = $PSVersionTable.Platform -ne "Unix"
if ($isWindows) {
    $dotnetAvailable = [bool](Get-Command dotnet -ErrorAction SilentlyContinue)
    if ($dotnetAvailable) {
        Invoke-Stage "dotnet publish win-x64" {
            dotnet publish src/MangaPixer.Server/MangaPixer.Server.csproj -c Release -r win-x64 --self-contained true -o artifacts/win-x64 2>&1 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "win-x64 publish failed" }
        }
    }
    else {
        $results.Add([PSCustomObject]@{ Stage = "win-x64 publish"; Status = "SKIPPED"; Duration = "n/a"; Error = "dotnet SDK not on PATH" })
    }
}
else {
    $results.Add([PSCustomObject]@{ Stage = "win-x64 publish"; Status = "SKIPPED"; Duration = "n/a"; Error = "Not running on Windows; requires Windows runner" })
}

# Stage 4: Container smoke test (audit defect D19/D20)
if ($dockerAvailable) {
    Invoke-Stage "Container smoke test" {
        & "$repoRoot/scripts/Smoke-Container.ps1" 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Container smoke test failed" }
    }
}
else {
    $results.Add([PSCustomObject]@{ Stage = "Container smoke"; Status = "SKIPPED"; Duration = "n/a"; Error = "docker not on PATH" })
}

# Stage 5: Versioned release image + SBOM + checksums (audit finding F1, D20).
# Idempotent: an existing version tag is reused, not overwritten.
if ($dockerAvailable) {
    Invoke-Stage "Release packaging (version tag + SBOM + checksums)" {
        & "$repoRoot/scripts/Package-Release.ps1" 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "Package-Release failed" }
    }
}
else {
    $results.Add([PSCustomObject]@{ Stage = "Release packaging"; Status = "SKIPPED"; Duration = "n/a"; Error = "docker not on PATH" })
}

# Summary
Write-Host "`n=== Verify-Packaging Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All packaging checks passed (skipped stages reported above)." -ForegroundColor Green
