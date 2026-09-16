#Requires -Version 7.0
<#
    Package-Release.ps1
    Builds the release container image tagged with the exact Version.props version,
    treats version tags as immutable, generates an SPDX SBOM (anchore/syft), and
    writes SHA-256 checksums over the artifacts.

    Addresses audit finding F1 (image tag drift — images were never tagged with the
    source version) and the deferred SBOM/checksum items (D20).

    Never pushes to a registry. A `:latest` alias is created only with -Latest and
    points at the same image id as the version tag.

    Immutability: if the version tag already exists it is NOT rebuilt (the existing
    image is reused for SBOM/checksums); pass -Force to rebuild over it deliberately.

    SBOM is produced from a saved image archive (`docker save`), so syft needs no
    access to the Docker socket.

    Usage: pwsh ./scripts/Package-Release.ps1 [-Latest] [-Force]
           docker run --rm -v /var/run/docker.sock:/var/run/docker.sock `
             -v "${PWD}:/workspace" -w /workspace `
             mcr.microsoft.com/powershell:7.5 pwsh ./scripts/Package-Release.ps1
#>
[CmdletBinding()]
param(
    [switch]$Latest,
    [switch]$Force,
    [string]$SyftImage = "anchore/syft:latest"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker is required on PATH to package a release."
}

$version = (& "$PSScriptRoot/Get-MangaPixerVersion.ps1").Trim()
$imageTag = "mangapixer:$version"
Write-Host "Release version: $version  ->  $imageTag" -ForegroundColor Cyan

# --- Immutable versioned build ---
docker image inspect $imageTag *> $null
$tagExists = ($LASTEXITCODE -eq 0)

if ($tagExists -and -not $Force) {
    Write-Host "Image tag '$imageTag' already exists; version tags are immutable. Reusing it (pass -Force to rebuild)." -ForegroundColor Yellow
}
else {
    if ($tagExists) { Write-Host "Rebuilding existing tag '$imageTag' because -Force was given." -ForegroundColor Yellow }
    # Git build metadata for InformationalVersion (deploy/Dockerfile ARGs ->
    # Directory.Build.props). Without them the image reports "<version>+dirty",
    # which the first public release did. CI checks out the tagged commit, so
    # the tree is clean there; locally the dirty flag reflects the working tree.
    $gitShort = (git rev-parse --short HEAD 2>$null)
    if (-not $gitShort) { $gitShort = "" }
    $gitDirty = if (git status --porcelain 2>$null) { "true" } else { "false" }
    Write-Host "Git: $gitShort (dirty=$gitDirty)"
    docker build -f deploy/Dockerfile `
        --build-arg GIT_COMMIT_SHORT=$gitShort `
        --build-arg GIT_IS_DIRTY=$gitDirty `
        -t $imageTag . 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker build failed" }
}

if ($Latest) {
    docker tag $imageTag "mangapixer:latest" 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker tag :latest failed" }
    Write-Host "Tagged mangapixer:latest at the same image id as $imageTag." -ForegroundColor Green
}

# --- Output directory ---
$outDir = Join-Path $repoRoot "artifacts/release/$version"
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$outDirFull = (Resolve-Path $outDir).Path

# --- SBOM from a saved image archive ---
$tarName = "mangapixer-$version-image.tar"
$sbomName = "mangapixer-$version.sbom.spdx.json"
Write-Host "Saving image archive..." -ForegroundColor Cyan
docker save $imageTag -o (Join-Path $outDir $tarName)
if ($LASTEXITCODE -ne 0) { throw "docker save failed" }

Write-Host "Generating SPDX SBOM with syft..." -ForegroundColor Cyan
docker run --rm -v "${outDirFull}:/work" $SyftImage scan "docker-archive:/work/$tarName" -o "spdx-json=/work/$sbomName" -q 2>&1 | Out-Host
if ($LASTEXITCODE -ne 0) { throw "syft SBOM generation failed" }

# --- SBOM the Windows self-contained folder too, if it was published ---
$winDir = Join-Path $repoRoot "artifacts/win-x64"
if (Test-Path $winDir) {
    $winSbom = "mangapixer-$version.win-x64.sbom.spdx.json"
    $winFull = (Resolve-Path $winDir).Path
    Write-Host "Generating SBOM for win-x64 publish folder..." -ForegroundColor Cyan
    docker run --rm -v "${winFull}:/scan:ro" -v "${outDirFull}:/work" $SyftImage scan "dir:/scan" -o "spdx-json=/work/$winSbom" -q 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "syft SBOM (win-x64) failed" }
}

# --- Windows MSI installer, if a Windows distribution was staged ---
$windowsDistDir = Join-Path $repoRoot "artifacts/windows-dist"
if (Test-Path $windowsDistDir) {
    Write-Host "Building Windows MSI installer..." -ForegroundColor Cyan
    $builtMsiPath = & "$PSScriptRoot/Build-Installer.ps1" -PayloadDir $windowsDistDir
    Copy-Item $builtMsiPath $outDir -Force
    Write-Host "Copied $(Split-Path -Leaf $builtMsiPath) into $outDir" -ForegroundColor Green
}
else {
    Write-Host "No artifacts/windows-dist/ staging folder - skipping the Windows MSI installer step." -ForegroundColor Yellow
}

# --- SHA-256 checksums over every artifact ---
$sumFile = Join-Path $outDir "SHA256SUMS.txt"
$lines = Get-ChildItem $outDir -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } | ForEach-Object {
    $hash = (Get-FileHash -Algorithm SHA256 $_.FullName).Hash.ToLowerInvariant()
    "$hash  $($_.Name)"
}
$lines | Set-Content -Encoding ascii $sumFile

Write-Host "`n=== Release artifacts ($outDir) ===" -ForegroundColor Cyan
Get-ChildItem $outDir | Format-Table Name, Length -AutoSize
Write-Host "Done. $imageTag built/reused; SBOM + SHA-256 checksums written. No registry push." -ForegroundColor Green
