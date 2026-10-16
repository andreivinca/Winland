<#
.SYNOPSIS
  Register (or remove) Winland's processes to start at logon.

  Winland is two long-running elevated processes — winland-env (the window-manager service) and
  winland-keys (the hotkey daemon) — plus winland-indicator (the on-screen workspace circles), which
  runs at the user's normal level. This registers one Scheduled Task per process, triggered at logon;
  the daemons run with highest privileges (so there is no UAC prompt at sign-in).

  Run this script itself elevated (from an admin PowerShell).

.PARAMETER Dir
  Folder containing the Winland exes. Defaults to this script's folder.

.PARAMETER Uninstall
  Remove the scheduled tasks instead of creating them.

.EXAMPLE
  .\install-autostart.ps1
  .\install-autostart.ps1 -Uninstall
#>
param(
    [string]$Dir = $PSScriptRoot,
    [switch]$Uninstall
)

# Registering tasks with -RunLevel Highest needs admin; fail up front with a clear message instead
# of letting Register-ScheduledTask produce a cryptic one.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Error 'Run this script from an elevated (administrator) PowerShell.'
    return
}

$tasks = @(
    @{ Name = 'Winland Env';  Exe = Join-Path $Dir 'winland-env.exe';  RunLevel = 'Highest' },
    @{ Name = 'Winland Keys'; Exe = Join-Path $Dir 'winland-keys.exe'; RunLevel = 'Highest' },
    # Limited, not Highest: the indicator only reads workspace state off a pipe and draws a circle,
    # so it has no reason to hold administrator rights at all. Elevating a UI window that the user
    # clicks and drags would be gratuitous.
    @{ Name = 'Winland Indicator'; Exe = Join-Path $Dir 'winland-indicator.exe'; RunLevel = 'Limited' }
)

if ($Uninstall) {
    foreach ($t in $tasks) {
        Unregister-ScheduledTask -TaskName $t.Name -Confirm:$false -ErrorAction SilentlyContinue
        Write-Host "Removed task: $($t.Name)"
    }
    return
}

foreach ($t in $tasks) {
    if (-not (Test-Path $t.Exe)) {
        Write-Error "Not found: $($t.Exe). Pass -Dir <folder containing the exes>."
        continue
    }

    $action    = New-ScheduledTaskAction -Execute $t.Exe
    $trigger   = New-ScheduledTaskTrigger -AtLogOn
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel $t.RunLevel
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName $t.Name -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "Registered task: $($t.Name) -> $($t.Exe)"
}

Write-Host "Done. winland-env and winland-keys will start elevated at next logon, winland-indicator unelevated (or start them now)."
