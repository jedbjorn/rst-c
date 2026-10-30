<#
  serilog-probe.ps1 — guest helper for VM step V3 of the v3.0.0 resolver hardening (decision #27).
  rst-c's own (not vendored); Windows PowerShell 5.1. Run from the unpacked SerilogProbe artifact
  folder, as the standard user (never elevated).

    .\serilog-probe.ps1 install   -Probe AAA -Year 2026   removes both probes, clears the %TEMP% probe
                                                          files, installs one probe next to RST.addin
    .\serilog-probe.ps1 uninstall -Year 2026              removes both probes and their %TEMP% files
    .\serilog-probe.ps1 launch    -Year 2026              starts Revit on the console desktop
    .\serilog-probe.ps1 kill                              stops Revit (only if a clean exit is impossible)
    .\serilog-probe.ps1 show      -Year 2026              manifests, probe reports, RST's newest boot
                                                          log, and the newest journal's add-in lines

  The launch verb is adapted from shared/f15/spike/tools/rv.ps1 (itself adapted from gantt-it
  tools/vm/revit.ps1): a one-shot scheduled task with an Interactive principal.
#>
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [ValidateSet('install', 'uninstall', 'launch', 'kill', 'show')]
  [string]$Action,
  [ValidateSet('AAA', 'ZZZ')]
  [string]$Probe = 'AAA',
  [ValidateSet(2026, 2027)]
  [int]$Year = 2026
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$names = @('AAA-SerilogProbe', 'ZZZ-SerilogProbe')
$addins = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$Year"

function Remove-Probes {
  foreach ($n in $names) {
    Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path $addins "$n.addin")
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $addins $n)
  }
  Get-ChildItem $env:TEMP -Filter 'probe-*-SerilogProbe*' -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
}

function Show-File([string]$path) {
  if (Test-Path $path) { "--- $path"; Get-Content $path } else { "--- $path MISSING" }
}

switch ($Action) {
  'install' {
    if (Get-Process Revit -ErrorAction SilentlyContinue) { 'Close Revit first'; exit 2 }
    $name = "$Probe-SerilogProbe"
    $src = Join-Path $here "$Year"
    if (-not (Test-Path (Join-Path $src "$name.addin"))) { "missing $src\$name.addin"; exit 2 }
    Remove-Probes
    New-Item -ItemType Directory -Force $addins | Out-Null
    Copy-Item (Join-Path $src "$name.addin") $addins -Force
    Copy-Item (Join-Path $src $name) $addins -Recurse -Force
    "installed $name for $Year in $addins"
    Get-ChildItem $addins -Filter '*.addin' | Sort-Object Name | ForEach-Object { "  manifest: $($_.Name)" }
  }
  'uninstall' {
    Remove-Probes
    "removed both probes from $addins and their %TEMP% files"
  }
  'launch' {
    if (Get-Process Revit -ErrorAction SilentlyContinue) { 'Revit already running'; exit 2 }
    $task = 'RstC-SerilogProbe-Revit'
    $act = New-ScheduledTaskAction -Execute "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
    $pri = New-ScheduledTaskPrincipal -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited
    $set = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero)
    Register-ScheduledTask -TaskName $task -Action $act -Principal $pri -Settings $set -Force | Out-Null
    Start-ScheduledTask -TaskName $task
    Start-Sleep 3
    "launched Revit $Year pid=$((Get-Process Revit -ErrorAction SilentlyContinue).Id) at $(Get-Date -Format 'HH:mm:ss')"
  }
  'kill' {
    Get-Process Revit -ErrorAction SilentlyContinue | Stop-Process -Force
    'Revit stopped'
  }
  'show' {
    "=== manifests in $addins"
    Get-ChildItem $addins -Filter '*.addin' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { "  $($_.Name)" }
    "=== probe reports (%TEMP%)"
    foreach ($n in $names) { Show-File (Join-Path $env:TEMP "probe-$n.txt") }
    Show-File (Join-Path $env:TEMP 'probe-AAA-SerilogProbe-resolver.txt')
    "=== RST newest boot log"
    $boot = Get-ChildItem (Join-Path $env:APPDATA 'RST\logs') -Filter 'rst_*_boot.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($boot) { Show-File $boot.FullName } else { '  no rst_*_boot.log' }
    "=== newest journal ($Year): add-in starts and version-conflict blocks"
    $j = Get-ChildItem (Join-Path $env:LOCALAPPDATA "Autodesk\Revit\Autodesk Revit $Year\Journals") -Filter 'journal.*.txt' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime | Select-Object -Last 1
    if ($j) {
      "--- $($j.FullName)"
      Select-String -Path $j.FullName -Pattern 'Starting External Application: (RST|AAA-SerilogProbe|ZZZ-SerilogProbe)' | ForEach-Object { "{0}: {1}" -f $_.LineNumber, $_.Line.Trim() }
      [regex]::Matches((Get-Content $j.FullName -Raw), 'API_ERROR \{ : Assembly version conflict[^}]*\}') |
        Where-Object { $_.Value -match 'RST\.Bootstrap|SerilogProbe' } | ForEach-Object { $_.Value }
    } else { '  no journal' }
  }
}
