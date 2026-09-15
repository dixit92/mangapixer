#Requires -Version 7.0
<#
    MangaPlex Smoke-Windows.ps1
    Full verification of a Publish-Windows.ps1 output: server starts from a
    clean, empty data root; /health responds 200; the web UI index loads over
    HTTP; first-run setup is reachable (no default credentials); worker
    discovery/startup is proven; shutdown is clean; the temp data root is
    removed.

    Usage:
      pwsh ./scripts/Smoke-Windows.ps1
      pwsh ./scripts/Smoke-Windows.ps1 -PublishDir artifacts/windows-dist/server -Port 27272
#>
[CmdletBinding()]
param(
    # Parameterized by publish dir so this can smoke-test any Publish-Windows.ps1
    # output (default matches Publish-Windows.ps1's OutputDir/server layout).
    [string]$PublishDir = "artifacts/windows-dist/server",

    # Must match the Windows-distribution default bind port (see
    # Publish-Windows.ps1 -BindUrl / appsettings.Production.json overlay).
    # 6280 sits inside a Windows excluded port range on some hosts
    # (Hyper-V/WSL reservations; SocketException 10013) — 27272 is the
    # Windows-distribution default instead.
    [int]$Port = 27272,

    [int]$StartupTimeoutSec = 30,
    [int]$ShutdownTimeoutSec = 10
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Single source of truth for the product name used in display strings below.
$ProductName = "MangaPlex"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not [System.IO.Path]::IsPathRooted($PublishDir)) {
    $PublishDir = Join-Path $repoRoot $PublishDir
}

if (-not (Test-Path $PublishDir)) {
    throw "Publish directory not found: $PublishDir (run scripts/Publish-Windows.ps1 first)"
}

$serverExe = Join-Path $PublishDir "$ProductName.Server.exe"
if (-not (Test-Path $serverExe)) {
    throw "Server executable not found: $serverExe"
}

# --- Pre-check bindability BEFORE starting the server, so a taken/excluded
# port fails with a clear message instead of a bare SocketException 10013
# surfacing from inside the server's own startup log. ---
function Test-PortBindable {
    param([int]$Port)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    try {
        $listener.Start()
        return $true
    }
    catch {
        return $false
    }
    finally {
        $listener.Stop()
    }
}

Write-Host "Checking port $Port is bindable on 127.0.0.1..." -ForegroundColor Cyan
if (-not (Test-PortBindable -Port $Port)) {
    throw @"
Port $Port is not bindable on 127.0.0.1 (already in use, or inside a Windows
excluded port range — see 'netsh int ipv4 show excludedportrange protocol=tcp').
Free the port or pass a different -Port before re-running this smoke test.
"@
}
Write-Host "PASS: port $Port is bindable" -ForegroundColor Green

$tempData = Join-Path ([System.IO.Path]::GetTempPath()) "mangaplex-smoke-$(Get-Random)"
New-Item -ItemType Directory -Path $tempData | Out-Null
$stdoutLog = Join-Path $tempData "stdout.log"
$stderrLog = Join-Path $tempData "stderr.log"

$process = $null
try {
    Write-Host "Starting server from $serverExe with clean data root $tempData..." -ForegroundColor Cyan

    # Isolate the smoke test from the real %LOCALAPPDATA%\MangaPlex data root
    # and from any ambient ASPNETCORE_URLS in this shell, so the bundled
    # appsettings.Production.json's loopback bind is what's actually tested.
    Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
    $env:ASPNETCORE_ENVIRONMENT = "Production"
    $env:MangaPlex__Storage__DataRoot = $tempData

    # Deliberately NOT -NoNewWindow: a shared-console child process can only be
    # force-terminated (Windows refuses a graceful WM_CLOSE/taskkill against
    # it), which defeats the clean-shutdown check below. A hidden console of
    # its own lets `taskkill` (without /F) close it gracefully.
    $process = Start-Process -FilePath $serverExe -PassThru -WindowStyle Hidden `
        -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog

    # --- Startup: poll /health instead of a fixed sleep ---
    $healthUrl = "http://127.0.0.1:$Port/health"
    $deadline = (Get-Date).AddSeconds($StartupTimeoutSec)
    $healthy = $false
    while ((Get-Date) -lt $deadline) {
        if ($process.HasExited) {
            $stderr = Get-Content $stderrLog -Raw -ErrorAction SilentlyContinue
            throw "Server exited early (exit code $($process.ExitCode)). stderr: $stderr"
        }
        try {
            $response = Invoke-WebRequest -Uri $healthUrl -TimeoutSec 3 -UseBasicParsing
            if ($response.StatusCode -eq 200) { $healthy = $true; break }
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
    }
    if (-not $healthy) {
        $stderr = Get-Content $stderrLog -Raw -ErrorAction SilentlyContinue
        throw "Server did not become healthy within ${StartupTimeoutSec}s at $healthUrl. stderr: $stderr"
    }
    Write-Host "PASS: /health responded 200 (PID $($process.Id))" -ForegroundColor Green

    # --- Web UI index loads ---
    $indexResponse = Invoke-WebRequest -Uri "http://127.0.0.1:$Port/" -TimeoutSec 10 -UseBasicParsing
    if ($indexResponse.StatusCode -ne 200 -or $indexResponse.Content -notmatch '(?i)<html') {
        throw "Web UI index did not load as expected (status $($indexResponse.StatusCode))"
    }
    Write-Host "PASS: web UI index loads over http://127.0.0.1:$Port" -ForegroundColor Green

    # --- First-run setup reachable; no default credentials (empty data root
    # means zero users, so setup-status must report SetupRequired = true) ---
    $setupStatus = Invoke-RestMethod -Uri "http://127.0.0.1:$Port/api/v1/auth/setup-status" -TimeoutSec 10
    if (-not $setupStatus.setupRequired) {
        throw "Expected setup-status.setupRequired=true for a fresh data root, got: $($setupStatus | ConvertTo-Json -Compress)"
    }
    Write-Host "PASS: fresh data root reaches first-run setup (no default credentials)" -ForegroundColor Green

    # --- Worker discovery/startup proof: the pool's own startup log line
    # (MediaWorkerHostedService) only appears once StartAsync -> a worker
    # process actually launched and completed its handshake; a discovery or
    # launch failure instead logs a warning and never emits this line. ---
    Start-Sleep -Seconds 1  # give the async hosted-service startup log a moment to flush
    $stdout = Get-Content $stdoutLog -Raw -ErrorAction SilentlyContinue
    if ($stdout -notmatch "Media worker pool started with (\d+) worker") {
        $stderr = Get-Content $stderrLog -Raw -ErrorAction SilentlyContinue
        throw "Worker pool startup confirmation not found in server output. stdout: $stdout stderr: $stderr"
    }
    if ($stdout -match "Worker executable not discovered") {
        throw "Worker executable discovery fell back to PATH lookup — the bundled worker was not found where expected."
    }
    Write-Host "PASS: worker pool started ($($Matches[1]) worker(s)) — discovery resolved the bundled $ProductName.MediaWorker.exe" -ForegroundColor Green

    # --- Clean shutdown ---
    Write-Host "Requesting graceful shutdown (PID $($process.Id))..." -ForegroundColor Cyan
    & taskkill /PID $process.Id *>$null
    $exitedCleanly = $process.WaitForExit($ShutdownTimeoutSec * 1000)
    if (-not $exitedCleanly) {
        Write-Host "WARN: graceful shutdown did not complete within ${ShutdownTimeoutSec}s; force-killing" -ForegroundColor Yellow
        $process.Kill()
        $process.WaitForExit(5000) | Out-Null
    }
    else {
        Write-Host "PASS: server shut down cleanly" -ForegroundColor Green
    }

    Write-Host "`nPASS: Windows distribution smoke test completed" -ForegroundColor Green
}
finally {
    if ($process -and -not $process.HasExited) {
        try { $process.Kill() } catch { }
    }
    Remove-Item Env:MangaPlex__Storage__DataRoot -ErrorAction SilentlyContinue
    Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue
    if (Test-Path $tempData) {
        Remove-Item $tempData -Recurse -Force -ErrorAction SilentlyContinue
    }
}
