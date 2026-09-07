#Requires -Version 7.0
<#
    Get-MangaPlexVersion.ps1
    Returns the composed product SemVer from Version.props, e.g. "0.1.0-dev.30".

    Composes from the individual parts rather than reading <MangaPlexVersionFull>,
    which is an MSBuild-computed element carrying a Condition attribute that a
    simple element regex cannot read reliably.

    Usage: $version = & ./scripts/Get-MangaPlexVersion.ps1
#>
[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$propsPath = Join-Path $RepoRoot "Version.props"
$props = Get-Content $propsPath -Raw

function Get-RequiredProp([string]$name) {
    if ($props -match "<$name>([^<]*)</$name>") { return $Matches[1].Trim() }
    throw "Could not find <$name> in $propsPath"
}

$major = Get-RequiredProp "MangaPlexVersionMajor"
$minor = Get-RequiredProp "MangaPlexVersionMinor"
$patch = Get-RequiredProp "MangaPlexVersionPatch"

$prerelease = ""
if ($props -match "<MangaPlexVersionPrerelease>([^<]*)</MangaPlexVersionPrerelease>") {
    $prerelease = $Matches[1].Trim()
}

$version = "$major.$minor.$patch"
if ($prerelease) { $version = "$version-$prerelease" }

Write-Output $version
