#Requires -Version 7.0
<#
    MangaPixer Invoke-EfTool.ps1
    Wrapper around dotnet-ef running in the .NET SDK container.

    EF Core tooling requires the .NET SDK plus dotnet-ef; the host may have
    neither. This runs the tool in the sdk:10.0 container against the repo,
    reinstalling dotnet-ef each run (the container is removed afterwards, so
    the tool install does not persist).

    Usage:
      pwsh ./scripts/Invoke-EfTool.ps1 migrations list
      pwsh ./scripts/Invoke-EfTool.ps1 migrations add AddMyMigration --project src/MangaPixer.Server
      pwsh ./scripts/Invoke-EfTool.ps1 migrations script   # etc.

    Everything after the first argument is passed verbatim to dotnet-ef.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Command,

    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$Arguments = @()
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$image = "mcr.microsoft.com/dotnet/sdk:10.0"

$inner = "dotnet tool install --global dotnet-ef 2>&1 | Out-Null; " +
         "export PATH=`"`$PATH:/root/.dotnet/tools`"; " +
         "dotnet ef $Command " + ($Arguments -join " ")

& docker run --rm -v "${repoRoot}:/workspace" -w /workspace $image `
    bash -c $inner
exit $LASTEXITCODE
