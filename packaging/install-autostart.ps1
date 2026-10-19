<#
.SYNOPSIS
  Register (or remove) Winland to start at logon.

  Winland is one program, winland.exe, which starts and ends its companions (winland-keys and
  winland-indicator) itself. It runs elevated, so this registers one Scheduled Task, triggered at logon,
  that runs it with highest privileges — no UAC prompt at sign-in.

  Run this script itself elevated (from an admin PowerShell).

.PARAMETER Dir
  Folder containing winland.exe. Defaults to this script's folder.

.PARAMETER Uninstall
  Remove the scheduled task instead of creating it.

.EXAMPLE
  .\install-autostart.ps1
  .\install-autostart.ps1 -Uninstall
#>
param(
    [string]$Dir = $PSScriptRoot,
    [switch]$Uninstall
)

# Registering a task with -RunLevel Highest needs admin; fail up front with a clear message instead
# of letting Register-ScheduledTask produce a cryptic one.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole(
        [Security.Principal.WindowsBuiltinRole]::Administrator)) {
    Write-Error 'Run this script from an elevated (administrator) PowerShell.'
    return
}

$taskName = 'Winland'
$exe = Join-Path $Dir 'winland.exe'

# Earlier versions registered one task per process; Winland now starts the others itself. Remove them
# either way, so an old task can't start a second copy of a companion at logon.
foreach ($legacy in 'Winland Env', 'Winland Keys', 'Winland Indicator') {
    if (Get-ScheduledTask -TaskName $legacy -ErrorAction SilentlyContinue) {
        Unregister-ScheduledTask -TaskName $legacy -Confirm:$false
        Write-Host "Removed old task: $legacy"
    }
}

if ($Uninstall) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Removed task: $taskName"
    return
}

if (-not (Test-Path $exe)) {
    Write-Error "Not found: $exe. Pass -Dir <folder containing winland.exe>."
    return
}

$action    = New-ScheduledTaskAction -Execute $exe
$trigger   = New-ScheduledTaskTrigger -AtLogOn
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
$settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger `
    -Principal $principal -Settings $settings -Force | Out-Null
Write-Host "Registered task: $taskName -> $exe"
Write-Host 'Done. Winland will start at next logon (or start it now).'
