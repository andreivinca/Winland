<#
.SYNOPSIS
  Register (or remove) Winland's two daemons to start elevated at logon.

  Winland is two long-running processes — winland-env (the window-manager service) and winland-keys
  (the hotkey daemon) — both of which must run elevated. This registers one Scheduled Task per daemon,
  triggered at logon, running with highest privileges (so there is no UAC prompt at sign-in).

  Run this script itself elevated (from an admin PowerShell).

.PARAMETER Dir
  Folder containing winland-env.exe and winland-keys.exe. Defaults to this script's folder.

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

$tasks = @(
    @{ Name = 'Winland Env';  Exe = Join-Path $Dir 'winland-env.exe' },
    @{ Name = 'Winland Keys'; Exe = Join-Path $Dir 'winland-keys.exe' }
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
    $principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                    -ExecutionTimeLimit ([TimeSpan]::Zero)

    Register-ScheduledTask -TaskName $t.Name -Action $action -Trigger $trigger `
        -Principal $principal -Settings $settings -Force | Out-Null
    Write-Host "Registered task: $($t.Name) -> $($t.Exe)"
}

Write-Host "Done. winland-env and winland-keys will start elevated at next logon (or start them now)."
