<#
  rst-c's own: AC8 on Windows CI (feature #17, spec #20 §5; F17 WU4). RST updates itself, tray included, from one
  RST.msi build of this commit to a newer one, through the tray's real updater copy:

    (1) install -OldMsi (this commit at -OldVersion) per user; start its tray (--startup)
    (2) RST.RuntimeTests TraySelfUpdateFixtureTests ("close"): -NewMsi, served at v<NewVersion> by a fake GitHub,
        passes RST's four checks against the INSTALLED RST (real msi.dll), lands in %LocalAppData%\RST\Tray\cache\rst\,
        and the real SelfUpdater copies the INSTALLED tray to %LocalAppData%\RST\Tray\updater\ as RST.TrayUpdater.exe
        and starts it with --apply <msi> --wait-pid <tray>; then the tray is closed the way it quits after the
        hand-off. The updater waits for it, runs msiexec, records the result and starts the new tray.
        Asserted here: the old tray is gone; exactly one RST.Tray.exe runs, a new process from the install folder,
        started by the updater; it shows the updater's result; Windows Installer and Add/Remove Programs list ONE
        RST, at the new version; the updater's own log lines.
    (3) the same with -NewerMsi in "race" mode: the tray is NOT closed, so msiexec meets it running: Windows
        Installer's Restart Manager closes it and starts it again, and the updater starts one as well. The
        single-instance mutex must leave exactly one tray.
    (4) uninstall: no tray, no updater, and %LocalAppData%\RST\Tray\ (updater copy included) is removed.

  Build first: dotnet build src/RST.RuntimeTests/RST.RuntimeTests.csproj -c "Release R25". Stub Revit.exe FILES let
  RST.msi's launch condition pass; nothing named Revit.exe runs.
#>
param(
  [Parameter(Mandatory)][string]$OldMsi,
  [Parameter(Mandatory)][string]$NewMsi,
  [Parameter(Mandatory)][string]$NewerMsi,
  [Parameter(Mandatory)][string]$OldVersion,
  [Parameter(Mandatory)][string]$NewVersion,
  [Parameter(Mandatory)][string]$NewerVersion,
  [string]$Configuration = 'Release R25',
  [string]$OutDir = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'tray-selfupdate' } else { Join-Path $env:TEMP 'tray-selfupdate' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RstCode = '{AF0331FC-5280-492A-94BD-95EDF108BE74}'
$ProgramFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$TrayDir = Join-Path $env:LOCALAPPDATA 'Programs\RST\Tray'
$TrayExe = Join-Path $TrayDir 'RST.Tray.exe'
$LocalRoot = Join-Path $env:LOCALAPPDATA 'RST\Tray'
$UpdaterExe = Join-Path $LocalRoot 'updater\RST.TrayUpdater.exe'
$Logs = Join-Path $env:APPDATA 'RST\logs'
$TraySettings = Join-Path $env:APPDATA 'RST\tray_settings.json'
$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$TrayMutex = "Local\RST.Tray.$UserSid"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
foreach ($m in 'OldMsi', 'NewMsi', 'NewerMsi') { Set-Variable $m (Resolve-Path (Get-Variable $m -ValueOnly)).Path }

$Installer = New-Object -ComObject WindowsInstaller.Installer
function Get-ComProp($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $Obj, $ArgList)
}
function Get-Related {
  $list = Get-ComProp $Installer 'RelatedProducts' @($RstCode)
  $n = Get-ComProp $list 'Count'
  $out = @()
  for ($i = 0; $i -lt $n; $i++) {
    $pc = Get-ComProp $list 'Item' @($i)
    $out += [pscustomobject]@{ ProductCode = $pc; Version = (Get-ComProp $Installer 'ProductInfo' @($pc, 'VersionString')) }
  }
  return , $out
}
# Add/Remove Programs: visible Uninstall entries named RST (HKCU and HKLM, both views), as ci-bundle-smoke.ps1 reads them.
function Get-RstArp {
  $out = @()
  foreach ($root in 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall', 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall') {
    if (-not (Test-Path $root)) { continue }
    foreach ($k in Get-ChildItem $root) {
      $p = Get-ItemProperty $k.PSPath
      $names = @($p.PSObject.Properties | ForEach-Object { $_.Name })
      if (-not ($names -contains 'DisplayName') -or $p.DisplayName -ne 'RST') { continue }
      if (($names -contains 'SystemComponent') -and [int]$p.SystemComponent -eq 1) { continue }
      $out += [pscustomobject]@{ Key = "$root\$($k.PSChildName)"; Version = $(if ($names -contains 'DisplayVersion') { $p.DisplayVersion } else { '' }) }
    }
  }
  return , $out
}
function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { Show-State 'failure'; throw "FAIL: $What" }
  Write-Host "PASS: $What"
}
function Get-Trays { return , @(Get-CimInstance Win32_Process -Filter "Name='RST.Tray.exe'") }
function Get-Updaters { return , @(Get-CimInstance Win32_Process -Filter "Name='RST.TrayUpdater.exe'") }
function Test-TrayMutex {
  $m = $null
  $open = [Threading.Mutex]::TryOpenExisting($TrayMutex, [ref]$m)
  if ($m) { $m.Dispose() }
  return $open
}
function Get-TrayLog { return @(Get-ChildItem $Logs -Filter 'tray_*.log' -ErrorAction SilentlyContinue | Sort-Object Name | ForEach-Object { Get-Content $_.FullName }) }
function Show-State([string]$Label) {
  Write-Host "---- state [$Label]"
  Write-Host "  RelatedProducts: $(((Get-Related) | ForEach-Object { "$($_.ProductCode) $($_.Version)" }) -join '; ')"
  Write-Host "  ARP: $(((Get-RstArp) | ForEach-Object { "$($_.Key) $($_.Version)" }) -join '; ')"
  foreach ($p in (Get-Trays) + (Get-Updaters)) { Write-Host "  $($p.ProcessId) parent $($p.ParentProcessId) $($p.Name) $($p.CommandLine)" }
  Write-Host '  ---- tray log (last 60 lines)'
  Get-TrayLog | Select-Object -Last 60 | ForEach-Object { Write-Host "  $_" }
  Write-Host '----'
}
function Invoke-Msiexec([string]$Verb, [string]$Target, [string]$Name) {
  $log = Join-Path $OutDir "$Name.log"
  Write-Host "msiexec $Verb $Target /qn (log: $log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Target`" /qn /norestart /l*v `"$log`"" -PassThru
  $null = $p.Handle
  if (-not $p.WaitForExit(300000)) { $p | Stop-Process -Force; throw "FAIL: msiexec $Verb timed out" } # never -Wait: see ci-smoke.ps1
  Write-Host "  exit $($p.ExitCode)"
  if ($p.ExitCode -notin 0, 3010) { Get-Content $log -Tail 60 | Write-Host; throw "msiexec $Verb exited $($p.ExitCode)" }
}
function Wait-Until([scriptblock]$Cond, [int]$Seconds, [string]$What) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  while (-not (& $Cond)) {
    if ((Get-Date) -gt $deadline) { Show-State "timeout: $What"; throw "FAIL: $What (not within $Seconds s)" }
    Start-Sleep -Milliseconds 500
  }
}
function Start-Tray {
  $p = Start-Process $TrayExe -ArgumentList '--startup' -PassThru
  Wait-Until { Test-TrayMutex } 45 'the tray takes its mutex'
  Write-Host "tray pid $($p.Id) holds $TrayMutex"
  return $p.Id
}
function Invoke-HandOff([string]$Msi, [string]$Version, [string]$Mode, [int]$TrayPid) {
  $result = Join-Path $OutDir "handoff-$Mode.json"
  $env:RST_SELFUPDATE_NEW_MSI = $Msi
  $env:RST_SELFUPDATE_VERSION = $Version
  $env:RST_SELFUPDATE_TRAY_PID = "$TrayPid"
  $env:RST_SELFUPDATE_MODE = $Mode
  $env:RST_SELFUPDATE_RESULT = $result
  & dotnet test src/RST.RuntimeTests/RST.RuntimeTests.csproj -c $Configuration --no-build --nologo `
      --filter 'FullyQualifiedName~RST.RuntimeTests.Tray.TraySelfUpdateFixtureTests' --logger 'console;verbosity=normal' | Out-Host
  $code = $LASTEXITCODE
  Remove-Item Env:RST_SELFUPDATE_NEW_MSI, Env:RST_SELFUPDATE_VERSION, Env:RST_SELFUPDATE_TRAY_PID, Env:RST_SELFUPDATE_MODE, Env:RST_SELFUPDATE_RESULT
  Assert ($code -eq 0) "[$Mode] TraySelfUpdateFixtureTests passed (verified $Version, updater started)"
  Assert (Test-Path $result) "[$Mode] the test ran (not skipped) and wrote $result"
  return (Get-Content $result -Raw | ConvertFrom-Json)
}
# The updater has finished: its last log line, after this run's start.
function Wait-Updater([int]$LinesBefore, [string]$Mode) {
  Wait-Until { @((Get-TrayLog) | Select-Object -Skip $LinesBefore | Where-Object { $_ -match 'RST updater exits' }).Count -gt 0 } 300 "[$Mode] the updater finishes"
  Wait-Until { (Get-Updaters).Count -eq 0 } 30 "[$Mode] the updater process has exited"
  $lines = @((Get-TrayLog) | Select-Object -Skip $LinesBefore)
  $lines | Where-Object { $_ -match 'updater|self-update' } | ForEach-Object { Write-Host "  log: $_" }
  return , $lines
}
function Assert-OneRst([string]$Version, [string]$Label) {
  $r = Get-Related
  Assert ($r.Count -eq 1 -and $r[0].Version -eq $Version) "[$Label] Windows Installer lists one RST, $Version (got $(($r | ForEach-Object { $_.Version }) -join ', '))"
  $arp = Get-RstArp
  Assert ($arp.Count -eq 1 -and $arp[0].Version -eq $Version) "[$Label] Add/Remove Programs shows one RST entry, $Version (got $($arp.Count): $(($arp | ForEach-Object { $_.Version }) -join ', '))"
}

# ---- (1) the older build, and its tray
foreach ($y in '2025', '2026', '2027') {
  $dir = Join-Path $ProgramFiles64 "Autodesk\Revit $y"
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content -Path (Join-Path $dir 'Revit.exe') -Value 'rst-c ci stub' -NoNewline
}
Assert (@(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue).Count -eq 0) 'no Revit.exe runs (the stubs are files only)'
New-Item -ItemType Directory -Force (Split-Path $TraySettings) | Out-Null
Set-Content -Path $TraySettings -Value '{ "theme": "system", "checkForUpdates": false, "updateNoticeShownUtc": "2026-10-30T00:00:00+00:00" }' -NoNewline
Invoke-Msiexec '/i' $OldMsi "install-$OldVersion"
Assert-OneRst $OldVersion '(1) installed'
$oldTray = Start-Tray
Show-State '(1)'

# ---- (2) self-update, the tray quits after the hand-off
$before = @(Get-TrayLog).Count
$h = Invoke-HandOff $NewMsi $NewVersion 'close' $oldTray
Assert ($h.closed) '(2) the old tray was asked to quit after the hand-off'
Wait-Until { -not (Get-Process -Id $oldTray -ErrorAction SilentlyContinue) } 60 '(2) the old tray exits'
$lines = Wait-Updater $before 'close'
Assert (@($lines | Where-Object { $_ -match "RST updater \(pid \d+\) applying .*\\cache\\rst\\$([regex]::Escape($NewVersion))\\RST\.msi" }).Count -eq 1) '(2) RST.TrayUpdater.exe applied the verified RST.msi from the cache'
Assert (@($lines | Where-Object { $_ -match "updater: waiting for the tray \(pid $oldTray\) to exit" }).Count -eq 1) '(2) the updater waited for the old tray'
Assert (@($lines | Where-Object { $_ -match 'updater: msiexec exited 0 ' }).Count -eq 1) '(2) msiexec exited 0'
Assert (@($lines | Where-Object { $_ -match 'updater: started the installed tray' }).Count -eq 1) '(2) the updater started the installed tray'
$updaterPid = [int](([regex]::Match(($lines | Where-Object { $_ -match 'RST updater \(pid' } | Select-Object -First 1), 'RST updater \(pid (\d+)\)')).Groups[1].Value)
Wait-Until { (Get-Trays).Count -ge 1 } 30 '(2) a tray runs again'
Start-Sleep -Seconds 10   # anything that would start a second tray has done so by now
$trays = Get-Trays
Assert ($trays.Count -eq 1) "(2) exactly one RST.Tray.exe runs (got $($trays.Count))"
$newTray = $trays[0]
Assert ($newTray.ProcessId -ne $oldTray) "(2) it is a new process (pid $($newTray.ProcessId), the old was $oldTray)"
Assert ($newTray.ExecutablePath -eq $TrayExe) "(2) it runs from the install folder ($($newTray.ExecutablePath))"
Assert ($newTray.ParentProcessId -eq $updaterPid) "(2) the updater (pid $updaterPid) started it (parent $($newTray.ParentProcessId))"
Wait-Until { @((Get-TrayLog) | Select-Object -Skip $before | Where-Object { $_ -match "the updater reported RST $([regex]::Escape($NewVersion)): installed" }).Count -gt 0 } 30 '(2) the new tray shows the updater''s result'
Assert (-not (Test-Path (Join-Path $LocalRoot 'selfupdate.json'))) '(2) the result is shown once (selfupdate.json consumed)'
Assert (-not (Test-Path (Join-Path $LocalRoot 'updater\RST.Tray.exe'))) '(2) the updater copy never holds RST.Tray.exe'
Assert-OneRst $NewVersion '(2) after the self-update'
Show-State '(2)'

# ---- (3) the race: the tray still runs when msiexec does
$before = @(Get-TrayLog).Count
$h = Invoke-HandOff $NewerMsi $NewerVersion 'race' $newTray.ProcessId
$lines = Wait-Updater $before 'race'
Assert (@($lines | Where-Object { $_ -match 'updater: msiexec exited 0 ' }).Count -eq 1) '(3) msiexec exited 0 with the tray running (no reboot needed)'
Wait-Until { (Get-Trays).Count -ge 1 } 30 '(3) a tray runs'
Start-Sleep -Seconds 15
$trays = Get-Trays
foreach ($t in $trays) { Write-Host "  tray pid $($t.ProcessId) parent $($t.ParentProcessId) $($t.CommandLine)" }
Assert ($trays.Count -eq 1) "(3) Restart Manager's restart and the updater's start leave exactly one tray (got $($trays.Count))"
$second = @($lines | Where-Object { $_ -match 'RST tray already running for this user' })
Write-Host "OBSERVE: (3) launches that found a tray running and exited: $($second.Count)"
Wait-Until { @((Get-TrayLog) | Select-Object -Skip $before | Where-Object { $_ -match "the updater reported RST $([regex]::Escape($NewerVersion)): installed" }).Count -gt 0 } 30 '(3) the running tray shows the updater''s result'
Assert (-not (Test-Path (Join-Path $LocalRoot 'selfupdate.json'))) '(3) the result is shown once'
Assert-OneRst $NewerVersion '(3) after the race'
Show-State '(3)'

# ---- (4) uninstall (the updater's msiexec logs are kept first: the uninstall removes them)
Copy-Item (Join-Path $LocalRoot 'logs\*') $OutDir -ErrorAction SilentlyContinue
$pc = (Get-Related)[0].ProductCode
Invoke-Msiexec '/x' $pc 'uninstall'
Wait-Until { (Get-Trays).Count -eq 0 } 30 '(4) no tray runs after uninstall'
Assert ((Get-Updaters).Count -eq 0) '(4) no updater runs'
Assert ((Get-Related).Count -eq 0) '(4) RST is uninstalled'
Assert (-not (Test-Path $LocalRoot)) "(4) $LocalRoot (cache, updater copy, install logs) is removed"
Get-TrayLog | Set-Content -Path (Join-Path $OutDir 'tray.log')
Write-Host 'PASS: AC8 self-update (close and race), one tray, one RST entry, clean uninstall'
