#Requires -Version 7.0
<#
    Build-Installer.ps1
    Builds the MangaPlex per-user MSI installer (installer/MangaPlex.Installer,
    WiX v6) from a staged Windows distribution folder.

    ProductVersion is stamped from Version.props (via Get-MangaPlexVersion.ps1),
    the single source of truth - never hardcoded here. Pass -VersionOverride only
    to smoke-test the MajorUpgrade path with a bumped test-only version; it must
    never be used to publish a real installer.

    Usage:
        pwsh ./scripts/Build-Installer.ps1
        pwsh ./scripts/Build-Installer.ps1 -PayloadDir artifacts/windows-dist
        pwsh ./scripts/Build-Installer.ps1 -VersionOverride 1.12.1  # upgrade-path smoke only
#>
[CmdletBinding()]
param(
    [string]$PayloadDir = "artifacts/windows-dist",
    [string]$OutputDir = "artifacts/installer",
    [string]$VersionOverride
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Test-Path $PayloadDir)) {
    throw "Payload directory not found: $PayloadDir (expected the staged Windows distribution from Publish-Windows.ps1)"
}
$payloadDirFull = (Resolve-Path $PayloadDir).Path

$version = if ($VersionOverride) {
    Write-Host "Using -VersionOverride '$VersionOverride' (test build only - Version.props is untouched)." -ForegroundColor Yellow
    $VersionOverride
}
else {
    (& "$PSScriptRoot/Get-MangaPlexVersion.ps1").Trim()
}

Write-Host "Building MangaPlex installer $version from $payloadDirFull" -ForegroundColor Cyan

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "dotnet is required on PATH to build the installer."
}

# Reproducible toolchain: restores the pinned WiX v6 dotnet tool from
# .config/dotnet-tools.json (used for ad-hoc CLI/ICE inspection; the MSI
# itself is built through the wixproj below, which pulls WixToolset.Sdk via
# NuGet using the same pinned 6.0.2 version).
dotnet tool restore 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore failed" }

$wixproj = Join-Path $repoRoot "installer/MangaPlex.Installer/MangaPlex.Installer.wixproj"

# Force a full rebuild every time: MSBuild's incremental up-to-date check for
# the wixproj's CoreCompile target only looks at file timestamps, not at
# -p:ProductVersion, so two back-to-back builds that differ only by that
# property (as when smoke-testing a MajorUpgrade with -VersionOverride) could
# otherwise silently skip recompiling and reuse the previous version's output.
$objDir = Join-Path $repoRoot "installer/MangaPlex.Installer/obj"
$binDir = Join-Path $repoRoot "installer/MangaPlex.Installer/bin"
Remove-Item $objDir, $binDir -Recurse -Force -ErrorAction SilentlyContinue

dotnet build $wixproj -c Release -p:PayloadDir="$payloadDirFull" -p:ProductVersion="$version" 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "MSI build failed" }

$builtMsi = Join-Path $repoRoot "installer/MangaPlex.Installer/bin/Release/MangaPlex.msi"
if (-not (Test-Path $builtMsi)) { throw "Expected build output not found: $builtMsi" }

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$outputMsi = Join-Path $OutputDir "MangaPlex-$version.msi"
Copy-Item $builtMsi $outputMsi -Force

Write-Host "Built: $outputMsi" -ForegroundColor Green
Write-Output (Resolve-Path $outputMsi).Path
