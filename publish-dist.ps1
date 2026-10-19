<#
.SYNOPSIS
  Assemble a fresh, self-contained dist/ folder from the CURRENT source.

.DESCRIPTION
  dist/ is the gitignored folder the packaged app (and the logon auto-start) runs from. It is NOT
  produced by a plain `dotnet build`, so it silently goes stale unless it is re-assembled after a
  version bump. This script rebuilds it from scratch so what runs on the next restart matches source.

  It publishes Winland (winland.exe) and its companions (winland-keys, winland-indicator)
  self-contained (win-x64) into one folder so they share a single copy of the .NET runtime — Winland
  starts the companions from its own folder — then drops winlandctl (the tiny CLI the keys companion
  shells out to) and the auto-start installer beside them. winland-keys' own build already copies
  config.conf and the launch-*/show-desktop helper scripts into its output, so those come along
  automatically.

.PARAMETER Configuration
  Build configuration (default: Release).

.PARAMETER Runtime
  Self-contained runtime identifier for the daemons (default: win-x64).

.PARAMETER OutDir
  Destination folder (default: .\dist next to this script).

.EXAMPLE
  .\publish-dist.ps1                 # rebuild .\dist from current source (Release, win-x64)
  .\publish-dist.ps1 -OutDir D:\wl   # publish somewhere else
#>
param(
	[string]$Configuration = 'Release',
	[string]$Runtime = 'win-x64',
	[string]$OutDir
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
if (-not $OutDir) { $OutDir = Join-Path $root 'dist' }

# Stop Winland first: a live process holds a lock on its own DLLs, which would make the publish (or the
# folder wipe below) fail on a locked file. winland-env is Winland's former name.
Get-Process -Name winland, winland-keys, winland-indicator, winland-env -ErrorAction SilentlyContinue |
	Stop-Process -Force
Start-Sleep -Milliseconds 400  # let Windows release the file handles

# Start from a clean folder so files dropped by an older version (or a renamed exe) can't linger.
if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutDir | Out-Null

# Winland and its companions, self-contained, all into the same folder (one shared runtime).
$selfContained = @(
	'src\winland\winland.csproj',
	'src\winland-keys\winland-keys.csproj',
	'src\winland-indicator\winland-indicator.csproj'
)
foreach ($proj in $selfContained) {
	Write-Host "Publishing $proj ..."
	dotnet publish (Join-Path $root $proj) -c $Configuration -r $Runtime --self-contained true `
		-o $OutDir --nologo
	if ($LASTEXITCODE -ne 0) { throw "publish failed: $proj" }
}

# winlandctl must be self-contained too. It is a separate process the keys companion shells out to on
# every Super+<n> / focus bind, so it needs a runtime of its own — a framework-dependent build fails
# with "No frameworks were found" on any machine without the matching x64 .NET runtime installed
# (the others don't care because they each carry their own). Published last so nothing overwrites it.
Write-Host 'Publishing src\winlandctl\winlandctl.csproj ...'
dotnet publish (Join-Path $root 'src\winlandctl\winlandctl.csproj') -c $Configuration -r $Runtime `
	--self-contained true -o $OutDir --nologo
if ($LASTEXITCODE -ne 0) { throw 'publish failed: winlandctl' }

# The auto-start installer lives in packaging/ (tracked source, not build output).
Copy-Item (Join-Path $root 'packaging\*') $OutDir -Force

Write-Host ''
Write-Host "dist assembled at: $OutDir"
Get-ChildItem $OutDir -Filter *.exe |
	Where-Object { $_.Name -like 'winland*' } |
	Select-Object Name, @{n = 'Version'; e = { $_.VersionInfo.FileVersion } }, LastWriteTime |
	Format-Table -AutoSize
