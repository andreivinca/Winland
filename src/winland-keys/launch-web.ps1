<#
.SYNOPSIS
  Open a URL as a chromeless desktop-style window using Chrome/Chromium's --app mode.
.PARAMETER Url
  The web address to open, e.g. https://gemini.google.com
.EXAMPLE
  launch-web.ps1 https://gemini.google.com
#>
param(
    [Parameter(Mandatory = $true)][string]$Url
)

function Resolve-Browser {
    # App Paths covers Google Chrome and Chromium (both register as chrome.exe).
    foreach ($root in 'HKLM:', 'HKCU:') {
        $p = (Get-ItemProperty "$root\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\chrome.exe" -ErrorAction SilentlyContinue).'(default)'
        if ($p -and (Test-Path $p)) { return $p }
    }

    $candidates = @(
        "$env:ProgramFiles\Google\Chrome\Application\chrome.exe",
        "${env:ProgramFiles(x86)}\Google\Chrome\Application\chrome.exe",
        "$env:LocalAppData\Google\Chrome\Application\chrome.exe",
        "$env:LocalAppData\Chromium\Application\chrome.exe"
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    return 'chrome' # last resort: PATH / App Paths
}

Start-Process (Resolve-Browser) -ArgumentList "--app=$Url"
