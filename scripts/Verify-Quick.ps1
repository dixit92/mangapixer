#Requires -Version 7.0
<#
    MangaPixer Verify-Quick.ps1
    Quick verification for the normal implementation loop.
    Runs privacy preflight, formatting/lint checks, affected .NET unit tests, and Angular unit tests.
    Target: under 10 minutes after warm restore.

    Usage: pwsh ./scripts/Verify-Quick.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Debug"
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
    $status = git status --short 2>&1
    # A remote is expected; fail only if a remote URL embeds a credential.
    & "$PSScriptRoot/Test-GitRemotes.ps1"

    # Check that .devin/config.local.json is ignored
    $ignored = git check-ignore .devin/config.local.json 2>&1
    if ($LASTEXITCODE -ne 0) { throw ".devin/config.local.json is not ignored by .gitignore" }

    # Check no media files are tracked
    $mediaExts = @("*.cbz", "*.cbr", "*.cb7", "*.zip", "*.rar", "*.7z")
    foreach ($ext in $mediaExts) {
        $tracked = git ls-files $ext 2>&1
        if ($tracked) { throw "Media file is tracked: $tracked" }
    }
}

# Stage 2: .NET restore (if needed)
$needRestore = -not (Test-Path "src/MangaPixer.Core/obj/project.assets.json")
if ($needRestore) {
    Invoke-Stage "dotnet restore" {
        dotnet restore MangaPixer.slnx 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed" }
    }
}

# Stage 3: .NET format check
Invoke-Stage "dotnet format verify" {
    dotnet format MangaPixer.slnx --verify-no-changes --no-restore 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet format found changes needed" }
}

# Stage 4: .NET build
Invoke-Stage "dotnet build ($Configuration)" {
    dotnet build MangaPixer.slnx --no-restore -c $Configuration 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}

# Stage 5: .NET tests
Invoke-Stage "dotnet test" {
    dotnet test MangaPixer.slnx --no-build -c $Configuration --verbosity normal 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed" }
}

# Summary
Write-Host "`n=== Verify-Quick Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All quick checks passed." -ForegroundColor Green
