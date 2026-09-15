#Requires -Version 7.0
<#
    MangaPixer Verify-Contracts.ps1
    For API/DTO/migration/protocol/version changes: run drift and compatibility checks.
    Verifies OpenAPI regenerate/compare, generated Angular types, worker protocol tests,
    SemVer/build-identity checks, and migration model snapshot check.

    Usage: pwsh ./scripts/Verify-Contracts.ps1
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

# Stage 1: Version consistency check
Invoke-Stage "Version consistency" {
    $versionProps = Get-Content (Join-Path $repoRoot "Version.props") -Raw
    if ($versionProps -notmatch '<MangaPixerVersionFull>([^<]+)</MangaPixerVersionFull>') {
        throw "Could not find MangaPixerVersionFull in Version.props"
    }
    $expectedVersion = $Matches[1]
    Write-Host "Product version: $expectedVersion"

    $packageJson = Get-Content (Join-Path $repoRoot "web/package.json") -Raw | ConvertFrom-Json
    $webVersion = $packageJson.version
    if ($webVersion -ne $expectedVersion) {
        throw "Version mismatch: Version.props=$expectedVersion, package.json=$webVersion"
    }
    Write-Host "Version consistency verified: $expectedVersion"
}

# Stage 2: .NET build and contract tests
Invoke-Stage "dotnet build" {
    dotnet build MangaPixer.slnx -c Release 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }
}

Invoke-Stage "dotnet test (contracts)" {
    dotnet test MangaPixer.slnx --no-build -c Release --filter "FullyQualifiedName~Contracts|FullyQualifiedName~Ordering|FullyQualifiedName~Protocol" --verbosity normal 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Contract tests failed" }
}

# Stage 3: OpenAPI drift check (if npm available)
$npmAvailable = [bool](Get-Command npm -ErrorAction SilentlyContinue)
if ($npmAvailable -and (Test-Path "web/node_modules")) {
    Invoke-Stage "OpenAPI drift check" {
        npm --prefix web run api:check 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "OpenAPI drift check failed" }
    }
}
else {
    $results.Add([PSCustomObject]@{ Stage = "OpenAPI drift"; Status = "SKIPPED"; Duration = "n/a"; Error = "npm or node_modules not available" })
}

# Summary
Write-Host "`n=== Verify-Contracts Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All contract checks passed (skipped stages reported above)." -ForegroundColor Green
