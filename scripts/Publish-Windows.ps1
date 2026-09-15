#Requires -Version 7.0
<#
    MangaPlex Publish-Windows.ps1
    Produces a complete, runnable Windows distribution staging folder at
    artifacts/windows-dist/:

      artifacts/windows-dist/
        server/                 self-contained win-x64 MangaPlex.Server.exe
          wwwroot/              built Angular web assets (same bundle the
                                 Docker image serves, per deploy/Dockerfile)
          appsettings.Production.json
                                 Windows-distribution overlay: loopback bind
                                 + worker path. ASPNETCORE_ENVIRONMENT is
                                 "Production" by default, so ASP.NET Core
                                 loads this automatically; Docker/Linux never
                                 ships this file, so container behavior is
                                 byte-identical to before.
        worker/                 self-contained win-x64 MangaPlex.MediaWorker.exe
        MangaPlex.Tray.exe      the tray launcher project, if present
                                 (src/MangaPlex.Tray)

    Never touches deploy/**, Version.props, package.json, or the tray project.
    Default bind: http://127.0.0.1:27272 (see -BindUrl).

    Usage: pwsh ./scripts/Publish-Windows.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputDir = "artifacts/windows-dist",

    # Loopback-only default bind for the Windows distribution.
    # LAN access is an opt-in toggle owned by the tray launcher,
    # which passes ASPNETCORE_URLS to the server child process — that
    # explicit env var always wins over this appsettings default.
    # Port 6280 sits inside a Windows excluded port range on some hosts
    # (Hyper-V/WSL reservations; SocketException 10013) — 27272 is the
    # Windows-distribution default instead.
    [string]$BindUrl = "http://127.0.0.1:27272",

    [switch]$SkipWebBuild
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

# Single source of truth for the product name embedded in project/exe names
# below. A future product rename only needs to change this constant (plus
# the physical project/folder renames it mirrors).
$ProductName = "MangaPlex"

function Write-Stage {
    param([string]$Name)
    Write-Host "`n=== $Name ===" -ForegroundColor Cyan
}

$distRoot = Join-Path $repoRoot $OutputDir
$serverDir = Join-Path $distRoot "server"
$workerDir = Join-Path $distRoot "worker"

Write-Stage "Clean staging folder"
if (Test-Path $distRoot) {
    Remove-Item $distRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $distRoot | Out-Null

# --- Web assets (matches deploy/Dockerfile stage "web-build") ---
$webDist = Join-Path $repoRoot "web/dist/mangaplex-web/browser"
if (-not $SkipWebBuild) {
    Write-Stage "Build web assets (npm --prefix web ci && npm --prefix web run build)"
    npm --prefix web ci
    if ($LASTEXITCODE -ne 0) { throw "npm ci failed" }
    npm --prefix web run build
    if ($LASTEXITCODE -ne 0) { throw "npm run build failed" }
}
if (-not (Test-Path $webDist)) {
    throw "Web build output not found at $webDist (pass -SkipWebBuild only if it already exists)"
}

# --- Server: self-contained win-x64, including web assets in wwwroot/ ---
# Program.cs resolves wwwroot as <ContentRootPath>/wwwroot, i.e. next to the
# published exe — the same layout the Docker image uses for /app/wwwroot.
Write-Stage "Publish $ProductName.Server (self-contained win-x64)"
dotnet publish "src/$ProductName.Server/$ProductName.Server.csproj" -c Release -r win-x64 --self-contained true -o $serverDir
if ($LASTEXITCODE -ne 0) { throw "Server publish failed" }

Write-Stage "Copy web assets into server/wwwroot"
$wwwroot = Join-Path $serverDir "wwwroot"
Copy-Item $webDist $wwwroot -Recurse -Force

# --- Worker: self-contained win-x64, sibling of server/ (mirrors the
# container's /app/server + /app/worker layout) ---
Write-Stage "Publish $ProductName.MediaWorker (self-contained win-x64)"
dotnet publish "src/$ProductName.MediaWorker/$ProductName.MediaWorker.csproj" -c Release -r win-x64 --self-contained true -o $workerDir
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed" }

# --- Windows-distribution config overlay ---
# Only sets what the Windows distribution needs beyond the checked-in
# appsettings.json defaults: the loopback bind address and an explicit,
# self-contained worker path (so the server never shells out to a bare
# "dotnet <dll>", which would require a separately installed .NET runtime
# on the end-user machine). The relative worker path is resolved against
# the server's own assembly directory at startup (see Program.cs), so it
# is correct regardless of the launcher's working directory.
Write-Stage "Write Windows-distribution appsettings overlay"
$overlay = [ordered]@{
    Urls  = $BindUrl
    Media = [ordered]@{
        WorkerExecutablePath = "..\worker\$ProductName.MediaWorker.exe"
    }
}
$overlayPath = Join-Path $serverDir "appsettings.Production.json"
$overlay | ConvertTo-Json -Depth 5 | Set-Content -Path $overlayPath -Encoding utf8

# --- Tray launcher (the tray launcher project, if present) ---
# Guarded so the publish stays green whether or not src/MangaPlex.Tray
# exists. Never edits the tray project or MangaPlex.slnx.
$trayProject = Join-Path $repoRoot "src/$ProductName.Tray/$ProductName.Tray.csproj"
if (Test-Path $trayProject) {
    Write-Stage "Publish $ProductName.Tray (self-contained win-x64)"
    $trayStaging = Join-Path $distRoot "_tray-publish"
    dotnet publish $trayProject -c Release -r win-x64 --self-contained true -o $trayStaging
    if ($LASTEXITCODE -ne 0) { throw "Tray publish failed" }
    Copy-Item (Join-Path $trayStaging "$ProductName.Tray.exe") (Join-Path $distRoot "$ProductName.Tray.exe") -Force
    Remove-Item $trayStaging -Recurse -Force
}
else {
    Write-Host "SKIPPED: src/$ProductName.Tray not found (tray launcher project not present)" -ForegroundColor Yellow
}

Write-Stage "Publish-Windows summary"
Write-Host "Staging folder: $distRoot" -ForegroundColor Green
Get-ChildItem $distRoot -Recurse -Depth 1 | Select-Object FullName | Format-Table -AutoSize
