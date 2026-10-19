<#
.SYNOPSIS
  Developer launcher: stop Winland, build it from source, and start it again.

.DESCRIPTION
  Winland is one program, winland.exe. It starts its companions itself — winland-keys (the keyboard
  hook) and winland-indicator (the workspace circles) — and ends them when it exits (tray -> Exit).
  Building src\winland builds the companions too and puts them next to winland.exe.

  Winland runs elevated, so stopping it needs admin too: this script self-elevates with a SINGLE UAC
  prompt, then stops a running Winland, builds, and starts winland.exe. Stopping first is what frees the
  build output — a running process holds a lock on its own DLLs.

.PARAMETER Dir
  Run the pre-built winland.exe in this folder (e.g. a published .\dist) instead of building.

.PARAMETER Configuration
  Build configuration for source mode (default: Debug).

.PARAMETER NoBuild
  Source mode only: skip the build and start whatever is already in bin.

.PARAMETER Stop
  Stop Winland and exit (does not build or start anything).

.EXAMPLE
  .\run.ps1                 # stop, build Debug from source, start Winland
  .\run.ps1 -Dir .\dist     # stop, start the pre-built winland.exe in .\dist
  .\run.ps1 -Stop           # stop Winland
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

# Everything below needs admin (stopping and starting elevated Winland). If we aren't elevated,
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

$exe = if ($Dir) { Join-Path $Dir 'winland.exe' }
       else { Join-Path $root "src\winland\bin\$Configuration\net10.0-windows\winland.exe" }

# 1. Stop Winland FIRST, so the rebuild below isn't blocked by locked DLLs. Its companions are listed
#    too: a stopped (killed) Winland takes them down itself, but not one from before it started them —
#    and winland-env is Winland's former name, from when every process was started on its own.
Get-Process -Name winland, winland-keys, winland-indicator, winland-env -ErrorAction SilentlyContinue |
    Stop-Process -Force
Start-Sleep -Milliseconds 400  # give Windows a moment to release the file handles
if ($Stop) {
    Write-Host 'Stopped Winland.'
    return
}

# 2. Build (source mode only).
if (-not $Dir -and -not $NoBuild) {
    Write-Host "Building Winland ($Configuration)..."
    dotnet build (Join-Path $root 'src\winland\winland.csproj') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Write-Error 'Winland build failed.'; return }
}

# 3. Start Winland; it starts its companions. (Already elevated, so its admin manifest doesn't prompt.)
if (-not (Test-Path $exe)) {
    Write-Error "Not found: $exe`nBuild first (run .\run.ps1) or pass -Dir <folder with winland.exe>."
    return
}

Start-Process -FilePath $exe
Write-Host 'Started Winland. Super+1..9 should switch workspaces now.'
