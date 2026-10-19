<#
.SYNOPSIS
  Focus the app window already open for a URL, or open one with launch-web.ps1.
.DESCRIPTION
  launch-or-focus for web apps. "winlandctl focus-web" focuses the Chrome/Chromium --app window
  opened for this URL (restoring it if minimized). When there is none, or it is already in the
  foreground, a new window is opened — so pressing the shortcut again on a focused window opens
  another instance.
.PARAMETER Url
  The web address of the app, e.g. https://gemini.google.com
.EXAMPLE
  launch-or-focus-web.ps1 https://gemini.google.com
#>
param(
    [Parameter(Mandatory = $true)][string]$Url
)

# winlandctl sits next to this script (packaged layout). Exit code 0 means a window was focused; any
# failure (no window, already foreground, winland not running) means "launch instead".
& (Join-Path $PSScriptRoot 'winlandctl.exe') focus-web $Url 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    return
}

& (Join-Path $PSScriptRoot 'launch-web.ps1') $Url
