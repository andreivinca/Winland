<#
.SYNOPSIS
  Start (or stop) Winland — both daemons — from the folder this script sits in.

.DESCRIPTION
  Winland is two long-running elevated processes that live side by side in this folder:
    * winland-env.exe  — the window-manager service (workspaces, focus, tray). No keyboard hook;
                         it only reacts to commands on its control pipe.
    * winland-keys.exe — the hotkey daemon. Owns the global keyboard hook and, on each Super combo,
                         runs the matching command (e.g. "winlandctl workspace 1"), which reaches
                         winland-env over the pipe. It needs winlandctl.exe beside it (it is, here).

  Both must run elevated. This script self-elevates with a SINGLE UAC prompt, stops any previous
  instances, then starts both. Drop it next to the exes (it already is, in a packaged folder) and
  double-click it, or run:  powershell -ExecutionPolicy Bypass -File .\start-winland.ps1

.PARAMETER Stop
  Stop the running daemons and exit (does not start anything).
#>
param(
    [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Starting/stopping the elevated daemons needs admin. If we aren't elevated, relaunch this same
# script elevated — one UAC prompt. -NoExit keeps the window open so any error stays visible.
if (-not (Test-Admin)) {
    $inner = "-NoExit -NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    if ($Stop) { $inner += ' -Stop' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $inner
    return
}

# --- elevated from here on ---

# Stop any previous instances first (two keys daemons would double-fire every shortcut).
Get-Process -Name winland-env, winland-keys -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Stop) {
    Write-Host 'Stopped winland-env and winland-keys.'
    return
}

$envExe  = Join-Path $dir 'winland-env.exe'
$keysExe = Join-Path $dir 'winland-keys.exe'
$ctlExe  = Join-Path $dir 'winlandctl.exe'

foreach ($exe in @($envExe, $keysExe)) {
    if (-not (Test-Path $exe)) {
        Write-Error "Not found: $exe`nRun this script from the folder that contains the Winland exes."
        return
    }
}
if (-not (Test-Path $ctlExe)) {
    Write-Warning "winlandctl.exe is missing here ($ctlExe). Super+number / focus binds will not work."
}

# Already elevated, so launching these admin-manifest exes does NOT prompt again.
Start-Process -FilePath $envExe
Start-Process -FilePath $keysExe
Write-Host 'Started winland-env and winland-keys (elevated). Super+1..9 switches workspaces.'
