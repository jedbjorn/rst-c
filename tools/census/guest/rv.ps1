# rv.ps1 -Action launch|stop|status|answer -Year 2026|2027  (tools/census): Revit on the console desktop through a one-shot
# Interactive scheduled task (exec runs in a non-interactive session). stop also ends the crash reporter unsent.
param([Parameter(Mandatory)][string]$Action, [string]$Year = '2026')
switch ($Action) {
  'launch' {
    Get-Process cer_dialog, senddmp -ea 0 | Stop-Process -Force -ea 0
    if (Get-Process Revit -ea 0) { 'Revit already running'; exit 2 }
    Remove-Item C:\RstC\census\go, C:\RstC\census\current.txt -ea 0
    $exe = "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
    $a = New-ScheduledTaskAction -Execute $exe
    $p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName 'RstC-Census' -Action $a -Principal $p -Settings $s -Force | Out-Null
    Start-ScheduledTask -TaskName 'RstC-Census'; Start-Sleep 3
    "launched $Year pid=$((Get-Process Revit -ea 0).Id) at $(Get-Date -Format o)"
  }
  'answer' {
    # answer.ps1 in the console session (it needs the interactive desktop) through a one-shot Interactive task;
    # its output goes to C:\RstC\census\answer.log. No Windows MCP needed.
    Remove-Item C:\RstC\census\answer.log -ea 0
    $a = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -Command `"& C:\RstC\census-tools\answer.ps1 -Seconds 150 -Year $Year *> C:\RstC\census\answer.log`""
    $p = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::FromMinutes(5))
    Register-ScheduledTask -TaskName 'RstC-Census-Answer' -Action $a -Principal $p -Settings $s -Force | Out-Null
    Start-ScheduledTask -TaskName 'RstC-Census-Answer'
    "answer task started at $(Get-Date -Format o)"
  }
  'stop' {
    Get-Process Revit -ea 0 | Stop-Process -Force
    for ($i = 0; $i -lt 30 -and (Get-Process Revit -ea 0); $i++) { Start-Sleep 1 }
    Get-Process cer_dialog, senddmp, RevitAccelerator -ea 0 | Stop-Process -Force -ea 0
    "Revit running after stop: $([bool](Get-Process Revit -ea 0))"
  }
  'status' {
    Get-Process Revit -ea 0 | % { "Revit pid=$($_.Id) start=$($_.StartTime) title='$($_.MainWindowTitle)' resp=$($_.Responding) ws=$([int]($_.WorkingSet64/1MB))MB" }
    if (-not (Get-Process Revit -ea 0)) { 'Revit not running' }
  }
}
