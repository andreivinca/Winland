<#
.SYNOPSIS
  Start (or stop) all of Winland in one go.

.DESCRIPTION
  Winland is two long-running elevated processes:
    * winland-env  — the window-manager service (workspaces, focus, tray). Has no keyboard hook;
                     it only reacts to commands on its control pipe.
    * winland-keys — the hotkey daemon. Owns the global keyboard hook and, on each Super combo,
                     runs the matching command (e.g. "winlandctl workspace 1") which reaches
                     winland-env over the pipe. It needs winlandctl.exe sitting next to it.

  Both must run elevated. This script self-elevates with a SINGLE UAC prompt, then (in order):
  stops any previous instances, builds, and starts both daemons. Stopping first is what frees the
  build outputs — a running daemon holds a lock on its own DLLs.

.PARAMETER Dir
  Run pre-built exes from this one folder (e.g. a published .\dist) instead of building from source.

.PARAMETER Configuration
  Build configuration for source mode (default: Debug).

.PARAMETER NoBuild
  Source mode only: skip the build and launch whatever is already in bin.

.PARAMETER Stop
  Stop the running daemons and exit (does not build or start anything).

.EXAMPLE
  .\run.ps1                 # stop old, build Debug from source, start both daemons elevated
  .\run.ps1 -Dir .\dist     # stop old, start the pre-built exes in .\dist
  .\run.ps1 -Stop           # stop both daemons
#>
param(
    [string]$Dir,
    [string]$Configuration = 'Debug',
    [switch]$NoBuild,
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Resolve -Dir before any elevation: the elevated relaunch starts in System32, where a relative
# path like .\dist would no longer point at this repo.
if ($Dir) { $Dir = (Resolve-Path $Dir).Path }

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Everything below needs admin (starting/stopping the elevated daemons). If we aren't elevated,
# relaunch this same script elevated — one UAC prompt. -NoExit keeps that window open so build
# output and any errors stay visible (handy while developing).
if (-not (Test-Admin)) {
    $inner = "-NoExit -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Configuration $Configuration"
    if ($Dir)     { $inner += " -Dir `"$Dir`"" }
    if ($NoBuild) { $inner += ' -NoBuild' }
    if ($Stop)    { $inner += ' -Stop' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $inner
    return
}

# --- elevated from here on ---

# Where the two exes live: a single -Dir, or each project's build output in source mode.
if ($Dir) {
    $envExe  = Join-Path $Dir 'winland-env.exe'
    $keysExe = Join-Path $Dir 'winland-keys.exe'
}
else {
    $tfm     = 'net10.0-windows'
    $envExe  = Join-Path $root "src\winland-env\bin\$Configuration\$tfm\winland-env.exe"
    $keysExe = Join-Path $root "src\winland-keys\bin\$Configuration\$tfm\winland-keys.exe"
}

# 1. Stop any previous instances FIRST — both so we don't double up (two keys daemons would
#    double-fire every combo), and so a running daemon stops locking the DLLs we're about to rebuild.
Get-Process -Name winland-env, winland-keys -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400  # give Windows a moment to release the file handles
if ($Stop) {
    Write-Host 'Stopped winland-env and winland-keys.'
    return
}

# 2. Build (source mode only). winland-keys' build also copies winlandctl.exe beside it.
if (-not $Dir -and -not $NoBuild) {
    Write-Host "Building Winland ($Configuration)..."
    dotnet build (Join-Path $root 'src\winland-keys\winland-keys.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'winland-keys build failed.'; return }
    dotnet build (Join-Path $root 'src\winland-env\winland-env.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'winland-env build failed.'; return }
}

# 3. Start both. (Already elevated, so launching these admin-manifest exes does NOT prompt again.)
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
Write-Host 'Started winland-env and winland-keys (elevated). Super+1..9 should switch workspaces now.'
