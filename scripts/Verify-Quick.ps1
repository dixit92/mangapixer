#Requires -Version 7.0
<#
    MangaPixer Verify-Quick.ps1
    Quick verification for the normal implementation loop.
    Runs privacy preflight, formatting check, build, the fast .NET tier and the Angular unit tests.

    The fast .NET tier is every test project except the Server tests that boot an application host:
    the Server.Tests classes under .Server.Http, .Server.Hosting and .Server.Contracts (plus
    HealthEndpointTests) are skipped, because each of them starts a full host with its own SQLite
    database. The Full tier (Verify.ps1) and CI run them. Pass -IncludeHttp to run them here too, or
    -Filter to hand your own `dotnet test --filter` expression to the Server tests (it replaces the
    host-boot exclusion). -SkipWeb leaves the Angular tests out (they need npm on PATH; they are
    skipped with a note when npm is missing).

    Usage: pwsh ./scripts/Verify-Quick.ps1 [-IncludeHttp] [-Filter <expr>] [-SkipWeb]
#>
[CmdletBinding()]
param(
    [string]$Configuration = "Debug",
    [switch]$IncludeHttp,
    [string]$Filter = "",
    [switch]$SkipWeb
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

# Stage 5: .NET tests. The fast tier skips the Server tests that boot a host (see the header);
# the other test projects always run in full.
$hostBootFilter = 'FullyQualifiedName!~.Server.Http.&FullyQualifiedName!~.Server.Hosting.&FullyQualifiedName!~.Server.Contracts.&FullyQualifiedName!~HealthEndpointTests'
$serverFilter = if ($Filter) { $Filter } elseif ($IncludeHttp) { "" } else { $hostBootFilter }

Invoke-Stage "dotnet test (fast tier)" {
    foreach ($project in @("Core", "MediaWorker", "Tray")) {
        dotnet test "tests/MangaPixer.$project.Tests" --no-build -c $Configuration 2>&1 | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet test failed ($project)" }
    }
    $serverArgs = @("tests/MangaPixer.Server.Tests", "--no-build", "-c", $Configuration)
    if ($serverFilter) { $serverArgs += @("--filter", $serverFilter) }
    dotnet test @serverArgs 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet test failed (Server)" }
}

# Stage 6: Angular unit tests (Vitest), when npm is available
if (-not $SkipWeb) {
    if (Get-Command npm -ErrorAction SilentlyContinue) {
        Invoke-Stage "npm test:ci" {
            if (-not (Test-Path "web/node_modules")) {
                npm --prefix web ci --no-audit --no-fund 2>&1 | Out-Host
                if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
            }
            npm --prefix web run test:ci 2>&1 | Out-Host
            if ($LASTEXITCODE -ne 0) { throw "npm test:ci failed" }
        }
    }
    else {
        Write-Host "npm not on PATH - Angular unit tests skipped." -ForegroundColor Yellow
        $results.Add([PSCustomObject]@{ Stage = "npm test:ci"; Status = "SKIPPED"; Duration = "n/a" })
    }
}

# Summary
Write-Host "`n=== Verify-Quick Summary ===" -ForegroundColor Cyan
$results | Format-Table -AutoSize
$failed = $results | Where-Object { $_.Status -eq "FAIL" }
if ($failed) { exit 1 }
Write-Host "All quick checks passed." -ForegroundColor Green
