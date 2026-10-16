<#
.SYNOPSIS
  Start (or stop) all of Winland in one go.

.DESCRIPTION
  Winland is two long-running elevated processes plus one unelevated widget:
    * winland-env       — the window-manager service (workspaces, focus, tray). Has no keyboard hook;
                          it only reacts to commands on its control pipe.
    * winland-keys      — the hotkey daemon. Owns the global keyboard hook and, on each Super combo,
                          runs the matching command (e.g. "winlandctl workspace 1") which reaches
                          winland-env over the pipe. It needs winlandctl.exe sitting next to it.
    * winland-indicator — the on-screen workspace circles, one per monitor. Subscribes to
                          winland-env's event pipe; needs no elevation of its own.

  The daemons must run elevated. This script self-elevates with a SINGLE UAC prompt, then (in order):
  stops any previous instances, builds, and starts everything. Stopping first is what frees the
  build outputs — a running daemon holds a lock on its own DLLs.

.PARAMETER Dir
  Run pre-built exes from this one folder (e.g. a published .\dist) instead of building from source.

.PARAMETER Configuration
  Build configuration for source mode (default: Debug).

.PARAMETER NoBuild
  Source mode only: skip the build and launch whatever is already in bin.

.PARAMETER Stop
  Stop everything Winland is running and exit (does not build or start anything).

.EXAMPLE
  .\run.ps1                 # stop old, build Debug from source, start everything (daemons elevated)
  .\run.ps1 -Dir .\dist     # stop old, start the pre-built exes in .\dist
  .\run.ps1 -Stop           # stop every Winland process
#>
param(
    [string]$Dir,
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$Stop,
    # Internal: set by the self-elevating relaunch, whose window would otherwise close on an error too.
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# The elevated window closes by itself when done; on a failure keep it open so the error can be read.
trap {
    if ($Elevated) {
        Write-Host $_ -ForegroundColor Red
        Read-Host 'Winland failed to start. Press Enter to close'
    }
    break
}

# Resolve -Dir before any elevation: the elevated relaunch starts in System32, where a relative
# path like .\dist would no longer point at this repo.
if ($Dir) { $Dir = (Resolve-Path $Dir).Path }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Everything below needs admin (starting/stopping the elevated daemons). If we aren't elevated,
# relaunch this same script elevated — one UAC prompt.
if (-not (Test-Admin)) {
    $inner = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated -Configuration $Configuration"
    if ($Dir)     { $inner += " -Dir `"$Dir`"" }
    if ($NoBuild) { $inner += ' -NoBuild' }
    if ($Stop)    { $inner += ' -Stop' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $inner
    return
}

# --- elevated from here on ---

# Where the exes live: a single -Dir, or each project's build output in source mode.
if ($Dir) {
    $envExe       = Join-Path $Dir 'winland-env.exe'
    $keysExe      = Join-Path $Dir 'winland-keys.exe'
    $indicatorExe = Join-Path $Dir 'winland-indicator.exe'
}
else {
    $tfm          = 'net10.0-windows'
    $envExe       = Join-Path $root "src\winland-env\bin\$Configuration\$tfm\winland-env.exe"
    $keysExe      = Join-Path $root "src\winland-keys\bin\$Configuration\$tfm\winland-keys.exe"
    $indicatorExe = Join-Path $root "src\winland-indicator\bin\$Configuration\$tfm\winland-indicator.exe"
}

# 1. Stop any previous instances FIRST — both so we don't double up (two keys daemons would
#    double-fire every combo), and so a running process stops locking the DLLs we're about to rebuild.
Get-Process -Name winland-env, winland-keys, winland-indicator -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400  # give Windows a moment to release the file handles
if ($Stop) {
    Write-Host 'Stopped winland-env, winland-keys and winland-indicator.'
    return
}

# 2. Build (source mode only). winland-keys' build also copies winlandctl.exe beside it.
if (-not $Dir -and -not $NoBuild) {
    Write-Host "Building Winland ($Configuration)..."
    dotnet build (Join-Path $root 'src\winland-keys\winland-keys.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'winland-keys build failed.'; return }
    dotnet build (Join-Path $root 'src\winland-env\winland-env.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'winland-env build failed.'; return }
    dotnet build (Join-Path $root 'src\winland-indicator\winland-indicator.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'winland-indicator build failed.'; return }
}

# 3. Start the daemons. (Already elevated, so launching these admin-manifest exes does NOT prompt again.)
foreach ($exe in @($envExe, $keysExe)) {
    if (-not (Test-Path $exe)) {
        Write-Error "Not found: $exe`nBuild first (run .\run.ps1) or pass -Dir <folder with the exes>."
        return
    }
}

$ctl = Join-Path (Split-Path $keysExe) 'winlandctl.exe'
if (-not (Test-Path $ctl)) {
    Write-Warning "winlandctl.exe is missing next to winland-keys.exe ($ctl). Super+number / focus binds will not work."
}

Start-Process -FilePath $envExe
Start-Process -FilePath $keysExe

# 4. The on-screen indicator. It does NOT require elevation — started from this elevated script it
#    simply inherits it, which is harmless. Skipped when it isn't there (e.g. -Dir points at an
#    older packaged folder).
if (Test-Path $indicatorExe) {
    Start-Process -FilePath $indicatorExe
}
else {
    Write-Warning "Not found: $indicatorExe`nStarting without the on-screen workspace indicator."
}

Write-Host 'Started Winland (daemons elevated). Super+1..9 should switch workspaces now.'
