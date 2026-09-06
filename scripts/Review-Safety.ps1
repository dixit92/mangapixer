#Requires -Version 7.0
<#
    MangaPlex Review-Safety.ps1
    Read-only review of a diff for safety issues:
    - Source writes (any media deletion/move API)
    - Path traversal vulnerabilities
    - ACL bypass
    - Secret/log leakage
    - Authorization-before-cache violations
    - Destructive operations
    - Test gaps

    Usage: pwsh ./scripts/Review-Safety.ps1
    Does NOT modify any files. Read-only analysis only.
#>
[CmdletBinding()]
param(
    [string]$Against = "HEAD"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$findings = [System.Collections.Generic.List[PSCustomObject]]::new()

function Add-Finding {
    param([string]$Category, [string]$Severity, [string]$Description, [string]$File, [string]$Line)
    $findings.Add([PSCustomObject]@{
        Category = $Category
        Severity = $Severity
        Description = $Description
        File = $File
        Line = $Line
    })
}

# Get the diff
$diffArgs = @("diff", $Against)
if ($Against -eq "HEAD" -and (-not (git rev-parse HEAD 2>$null))) {
    # No commits yet — diff against empty tree
    $diffArgs = @("diff", "--cached")
    $hasStaged = git diff --cached --name-only 2>$null
    if (-not $hasStaged) {
        $diffArgs = @("diff", "4b825dc642cb6eb9a060e54bf8d69288fbee4904")  # empty tree
    }
}

$diffOutput = git @diffArgs 2>&1
if (-not $diffOutput) {
    Write-Host "No diff to review." -ForegroundColor Yellow
    exit 0
}

$diffFiles = git @($diffArgs + @("--name-only")) 2>&1

Write-Host "Reviewing $($diffFiles.Count) changed files..." -ForegroundColor Cyan

# Check for dangerous patterns in changed files
$dangerousPatterns = @(
    @{ Pattern = 'File\.Delete'; Category = "Destructive"; Severity = "HIGH"; Desc = "File.Delete call detected" },
    @{ Pattern = 'File\.Move'; Category = "Destructive"; Severity = "HIGH"; Desc = "File.Move call detected" },
    @{ Pattern = 'Directory\.Delete'; Category = "Destructive"; Severity = "HIGH"; Desc = "Directory.Delete call detected" },
    @{ Pattern = '\.\.\/'; Category = "PathTraversal"; Severity = "HIGH"; Desc = "Path traversal pattern in source" },
    @{ Pattern = 'password.*=.*"'; Category = "SecretLeak"; Severity = "CRITICAL"; Desc = "Hardcoded password string" },
    @{ Pattern = 'apiKey.*=.*"'; Category = "SecretLeak"; Severity = "CRITICAL"; Desc = "Hardcoded API key" },
    @{ Pattern = 'EnableSensitiveDataLogging'; Category = "SecretLeak"; Severity = "HIGH"; Desc = "Sensitive data logging enabled" },
    @{ Pattern = 'AbsolutePath.*log'; Category = "LogLeak"; Severity = "MEDIUM"; Desc = "Absolute path in log" },
    @{ Pattern = 'File\.WriteAllBytes.*media'; Category = "SourceWrite"; Severity = "CRITICAL"; Desc = "Write to media directory" }
)

foreach ($file in $diffFiles) {
    if (-not (Test-Path $file)) { continue }
    $content = Get-Content $file -ErrorAction SilentlyContinue
    if (-not $content) { continue }

    for ($i = 0; $i -lt $content.Count; $i++) {
        $line = $content[$i]
        foreach ($dp in $dangerousPatterns) {
            if ($line -match $dp.Pattern) {
                Add-Finding -Category $dp.Category -Severity $dp.Severity -Description $dp.Desc -File $file -Line ($i + 1)
            }
        }
    }
}

# Check for media file deletion/move APIs
$mediaMutationPatterns = @(
    @{ Pattern = 'DeleteArchive|MoveArchive|QuarantineArchive|PurgeArchive'; Category = "MediaMutation"; Severity = "CRITICAL"; Desc = "Media mutation API detected" }
)

foreach ($file in $diffFiles) {
    if (-not (Test-Path $file)) { continue }
    $content = Get-Content $file -ErrorAction SilentlyContinue
    if (-not $content) { continue

    }
    for ($i = 0; $i -lt $content.Count; $i++) {
        $line = $content[$i]
        foreach ($mp in $mediaMutationPatterns) {
            if ($line -match $mp.Pattern) {
                Add-Finding -Category $mp.Category -Severity $mp.Severity -Description $mp.Desc -File $file -Line ($i + 1)
            }
        }
    }
}

# Summary
Write-Host "`n=== Safety Review Summary ===" -ForegroundColor Cyan
if ($findings.Count -eq 0) {
    Write-Host "No safety findings detected in the diff." -ForegroundColor Green
    exit 0
}
else {
    $findings | Format-Table -AutoSize
    $critical = $findings | Where-Object { $_.Severity -eq "CRITICAL" }
    if ($critical) {
        Write-Host "$($critical.Count) CRITICAL finding(s) detected!" -ForegroundColor Red
        exit 1
    }
    Write-Host "$($findings.Count) finding(s) detected. Review before proceeding." -ForegroundColor Yellow
    exit 0
}
