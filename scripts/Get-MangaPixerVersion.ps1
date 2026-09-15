#Requires -Version 7.0
<#
    Get-MangaPixerVersion.ps1
    Returns the composed product SemVer from Version.props, e.g. "0.1.0-dev.30".

    Composes from the individual parts rather than reading <MangaPixerVersionFull>,
    which is an MSBuild-computed element carrying a Condition attribute that a
    simple element regex cannot read reliably.

    Usage: $version = & ./scripts/Get-MangaPixerVersion.ps1
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

$major = Get-RequiredProp "MangaPixerVersionMajor"
$minor = Get-RequiredProp "MangaPixerVersionMinor"
$patch = Get-RequiredProp "MangaPixerVersionPatch"

$prerelease = ""
if ($props -match "<MangaPixerVersionPrerelease>([^<]*)</MangaPixerVersionPrerelease>") {
    $prerelease = $Matches[1].Trim()
}

$version = "$major.$minor.$patch"
if ($prerelease) { $version = "$version-$prerelease" }

Write-Output $version
