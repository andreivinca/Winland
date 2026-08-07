<#
.SYNOPSIS
  Focus an already-running app's window, or launch it if it isn't running.
.DESCRIPTION
  The window work is done by winland-env ("winlandctl focus-app"): it focuses the app's frontmost
  window, restoring a minimized one if that is all there is. It reports failure when there is nothing
  to focus or the app is already in the foreground — in both cases this script starts a new instance.
.PARAMETER App
  Executable / command to launch (e.g. firefox, code, notepad).
.PARAMETER Match
  Process name to match an existing window against. Defaults to App's file name without extension.
.PARAMETER LaunchArgs
  Arguments passed to the app when launching it. Everything after a "--" token is captured here, so
  dash-prefixed args pass through verbatim, e.g.  launch-or-focus wt.exe WindowsTerminal -- -d E:\
.EXAMPLE
  launch-or-focus.ps1 firefox
  launch-or-focus.ps1 code Code
  launch-or-focus.ps1 wt.exe WindowsTerminal -- -d E:\
#>
param(
    [Parameter(Mandatory = $true, Position = 0)][string]$App,
    [Parameter(Position = 1)][string]$Match,
    [Parameter(ValueFromRemainingArguments = $true)][string[]]$LaunchArgs
)

if ([string]::IsNullOrWhiteSpace($Match)) {
    $Match = [System.IO.Path]::GetFileNameWithoutExtension($App)
}

# winlandctl sits next to this script (packaged layout). Exit code 0 means a window was focused; any
# failure (no window, already foreground, winland-env not running) means "launch instead".
& (Join-Path $PSScriptRoot 'winlandctl.exe') focus-app $Match 2>$null | Out-Null
if ($LASTEXITCODE -eq 0) {
    return
}

if ($null -eq $LaunchArgs -or $LaunchArgs.Count -eq 0) {
    Start-Process $App
}
else {
    Start-Process $App -ArgumentList $LaunchArgs
}
