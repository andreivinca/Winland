<#
.SYNOPSIS
  Start (or stop) Winland from the folder this script sits in.

.DESCRIPTION
  Winland is two long-running elevated processes that live side by side in this folder, plus one
  unelevated widget:
    * winland-env.exe       — the window-manager service (workspaces, focus, tray). No keyboard hook;
                              it only reacts to commands on its control pipe.
    * winland-keys.exe      — the hotkey daemon. Owns the global keyboard hook and, on each Super
                              combo, runs the matching command (e.g. "winlandctl workspace 1"), which
                              reaches winland-env over the pipe. It needs winlandctl.exe beside it
                              (it is, here).
    * winland-indicator.exe — the on-screen workspace circles, one per monitor, fed by winland-env's
                              event pipe. Optional: started only if it is present in this folder.

  The daemons must run elevated. This script self-elevates with a SINGLE UAC prompt, stops any
  previous instances, then starts them. Drop it next to the exes (it already is, in a packaged
  folder) and double-click it, or run:  powershell -ExecutionPolicy Bypass -File .\start-winland.ps1

.PARAMETER Stop
  Stop everything Winland is running and exit (does not start anything).
#>
param(
    [switch]$Stop,
    # Internal: set by the self-elevating relaunch, whose window would otherwise close on an error too.
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'
$dir = $PSScriptRoot

# The elevated window closes by itself when done; on a failure keep it open so the error can be read.
trap {
    if ($Elevated) {
        Write-Host $_ -ForegroundColor Red
        Read-Host 'Winland failed to start. Press Enter to close'
    }
    break
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal($id)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)
}

# Starting/stopping the elevated daemons needs admin. If we aren't elevated, relaunch this same
# script elevated — one UAC prompt.
if (-not (Test-Admin)) {
    $inner = "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`" -Elevated"
    if ($Stop) { $inner += ' -Stop' }
    Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $inner
    return
}

# --- elevated from here on ---

# Stop any previous instances first (two keys daemons would double-fire every shortcut).
Get-Process -Name winland-env, winland-keys, winland-indicator -ErrorAction SilentlyContinue | Stop-Process -Force
if ($Stop) {
    Write-Host 'Stopped winland-env, winland-keys and winland-indicator.'
    return
}

$envExe       = Join-Path $dir 'winland-env.exe'
$keysExe      = Join-Path $dir 'winland-keys.exe'
$ctlExe       = Join-Path $dir 'winlandctl.exe'
$indicatorExe = Join-Path $dir 'winland-indicator.exe'

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

# The indicator needs no elevation (it only reads workspace state off a pipe); started from here it
# just inherits ours. Optional, so folders packaged before it existed keep working.
if (Test-Path $indicatorExe) {
    Start-Process -FilePath $indicatorExe
}

Write-Host 'Started Winland (daemons elevated). Super+1..9 switches workspaces.'
