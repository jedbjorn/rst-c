<#
  rst-c's own: AC8 on Windows CI (feature #17, spec #20 §5; F17 WU4). RST updates itself, tray included, from one
  RST.msi build of this commit to a newer one, through the tray's real updater copy:

    (1) install -OldMsi (this commit at -OldVersion) per user; the install starts its tray (--startup, SC-044)
    (2) RST.RuntimeTests TraySelfUpdateFixtureTests ("close"): -NewMsi, served at v<NewVersion> by a fake GitHub,
        passes RST's four checks against the INSTALLED RST (real msi.dll), lands in %LocalAppData%\RST\Tray\cache\rst\,
        and the real SelfUpdater copies the INSTALLED tray to %LocalAppData%\RST\Tray\updater\ as RST.TrayUpdater.exe
        and starts it with --apply <msi> --wait-pid <tray>; then the tray is closed the way it quits after the
        hand-off. The updater waits for it, runs msiexec, records the result and starts the new tray.
        Asserted here: the old tray is gone; exactly one RST.Tray.exe runs, a new process from the install folder,
        started by the updater; it shows the updater's result; Windows Installer and Add/Remove Programs list ONE
        RST, at the new version; the updater's own log lines. SC-044: RST.msi's RstStartTray, run by the updater's
        msiexec, found the updater holding the tray's mutex and started none (also in (3) and (5)).
    (3) the same with -NewerMsi in "race" mode: the tray is NOT closed, so msiexec meets it running: Windows
        Installer's Restart Manager closes it (and, since SC-044 review B1, does not restart it), and the updater starts one. The
        single-instance mutex must leave exactly one tray. SC-022: the old tray is never asked to quit here, so the
        updater cannot take the mutex in time; it must take it the moment msiexec's Restart Manager closes that tray
        and hand it over before it starts the new one.
    (3b) SC-022: RST.RuntimeTests TraySingleInstanceRaceTests starts two installed RST.Tray.exe at the same instant,
        -RaceRounds times, in every pairing of --startup and no switch, killing every fifth survivor while the test
        keeps a mutex handle open (an abandoned mutex next, taken over by exactly the next survivor): exactly one
        survives every round. Then a tray is started again for (4).
    Trays are counted only once every launch has made its single-instance decision (Wait-OneTray), never at a fixed
    delay: on a slow runner a launch can still be starting when a fixed delay runs out (SC-022).
    (4) uninstall: no tray, no updater, and %LocalAppData%\RST\Tray\ (updater copy included) is removed.
    (5) decision #34: RST installed by -OldBundle (RST-Setup.exe at -OldVersion). The tray must find the bundle's
        per-user registration and update with -NewBundle (RST-Setup.exe at -NewVersion, /quiet /norestart, no companion
        switches) through the same updater copy. Asserted: one tray, Windows Installer's one RST at the new version,
        and ONE Add/Remove Programs entry, the bundle's (RST.msi stays hidden behind it).
    (6) the bundle's own quiet uninstall (its Add/Remove Programs entry) removes RST and leaves no entry.

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
  # Decision #34: RST-Setup.exe (this commit's bundle, no companions) at OldVersion and NewVersion, for the bundle legs.
  [Parameter(Mandatory)][string]$OldBundle,
  [Parameter(Mandatory)][string]$NewBundle,
  [string]$Configuration = 'Release R25',
  # SC-022: rounds of two trays started at the same instant in (3b).
  [int]$RaceRounds = 40,
  [string]$OutDir = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'tray-selfupdate' } else { Join-Path $env:TEMP 'tray-selfupdate' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RstCode = '{AF0331FC-5280-492A-94BD-95EDF108BE74}'
$BundleCode = '{2BE2BE5D-9203-4344-A70D-E735D3B041A3}'   # installer-bundle/Bundle.wxs
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
foreach ($m in 'OldMsi', 'NewMsi', 'NewerMsi', 'OldBundle', 'NewBundle') { Set-Variable $m (Resolve-Path (Get-Variable $m -ValueOnly)).Path }

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
      $out += [pscustomobject]@{ Key = "$root\$($k.PSChildName)"; Version = $(if ($names -contains 'DisplayVersion') { $p.DisplayVersion } else { '' })
                                 Bundle = ($names -contains 'BundleUpgradeCode') -and (@($p.BundleUpgradeCode) -contains $BundleCode)
                                 QuietUninstall = $(if ($names -contains 'QuietUninstallString') { $p.QuietUninstallString } else { '' }) }
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
# SC-022: one tray, counted once every launch has decided. A launch has decided when it has exited (it found a tray:
# "already running (pid N exits)") or, as the tray, logged "RST tray … starting (pid N, …)" since $LinesBefore. Until
# then a slow runner can show two RST.Tray.exe that are both still starting, so a count at a fixed delay can see two
# (run 41135858346). Then it must stay one for a while: a second tray that stays running fails here.
function Wait-OneTray([int]$LinesBefore, [string]$Label, [string]$What = 'exactly one RST.Tray.exe runs', [int]$Seconds = 120) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  while ($true) {
    $t = Get-Trays
    if ($t.Count -eq 1) {
      $id = $t[0].ProcessId
      if (@((Get-TrayLog) | Select-Object -Skip $LinesBefore | Where-Object { $_ -match "RST tray \S+ starting \(pid $id," }).Count -gt 0) { break }
    }
    if ((Get-Date) -gt $deadline) {
      foreach ($p in $t) { Write-Host "  tray pid $($p.ProcessId) parent $($p.ParentProcessId) $($p.CommandLine)" }
      Assert $false "$Label $What (got $($t.Count) after $Seconds s: no single tray settled)"
    }
    Start-Sleep -Milliseconds 500
  }
  Start-Sleep -Seconds 5   # anything still to start a second tray (Restart Manager, the updater) has done so by now
  $trays = Get-Trays
  foreach ($p in $trays) { Write-Host "  tray pid $($p.ProcessId) parent $($p.ParentProcessId) $($p.CommandLine)" }
  Assert ($trays.Count -eq 1 -and $trays[0].ProcessId -eq $id) "$Label $What (got $($trays.Count), pid $id settled)"
  return $trays[0]
}
. (Join-Path $PSScriptRoot 'ci-tray-start.ps1')
# SC-044: RST.msi's RstStartTray ran during the updater's msiexec and started none, because the updater held the
# tray's single-instance mutex; the self-update's one tray is the updater's own start.
function Assert-StartTraySkipped([datetime]$Since, [string]$Label) {
  $lines = Get-StartTrayLines $Since $Label
  Assert (@($lines | Where-Object { $_ -match 'already holds Local\\RST\.Tray\.' }).Count -ge 1) "$Label RST.msi's RstStartTray found the updater holding the tray's mutex and started none"
  Assert (@($lines | Where-Object { $_ -match 'started RST\.Tray pid' }).Count -eq 0) "$Label RstStartTray started no tray of its own"
}
function Start-Tray {
  $p = Start-Process $TrayExe -ArgumentList '--startup' -PassThru
  Wait-Until { Test-TrayMutex } 45 'the tray takes its mutex'
  Write-Host "tray pid $($p.Id) holds $TrayMutex"
  return $p.Id
}
function Invoke-HandOff([string]$Msi, [string]$Version, [string]$Mode, [int]$TrayPid, [switch]$Bundle) {
  $result = Join-Path $OutDir "handoff-$Mode.json"
  $env:RST_SELFUPDATE_NEW_MSI = $Msi
  $env:RST_SELFUPDATE_VERSION = $Version
  $env:RST_SELFUPDATE_TRAY_PID = "$TrayPid"
  $env:RST_SELFUPDATE_MODE = $Mode
  $env:RST_SELFUPDATE_RESULT = $result
  $env:RST_SELFUPDATE_EXPECT_BUNDLE = $(if ($Bundle) { '1' } else { '0' })
  & dotnet test src/RST.RuntimeTests/RST.RuntimeTests.csproj -c $Configuration --no-build --nologo `
      --filter 'FullyQualifiedName~RST.RuntimeTests.Tray.TraySelfUpdateFixtureTests' --logger 'console;verbosity=normal' | Out-Host
  $code = $LASTEXITCODE
  Remove-Item Env:RST_SELFUPDATE_NEW_MSI, Env:RST_SELFUPDATE_VERSION, Env:RST_SELFUPDATE_TRAY_PID, Env:RST_SELFUPDATE_MODE, Env:RST_SELFUPDATE_RESULT, Env:RST_SELFUPDATE_EXPECT_BUNDLE
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
function Assert-OneRst([string]$Version, [string]$Label, [switch]$Bundle) {
  $r = Get-Related
  Assert ($r.Count -eq 1 -and $r[0].Version -eq $Version) "[$Label] Windows Installer lists one RST, $Version (got $(($r | ForEach-Object { $_.Version }) -join ', '))"
  $arp = Get-RstArp
  Assert ($arp.Count -eq 1 -and $arp[0].Version -eq $Version) "[$Label] Add/Remove Programs shows one RST entry, $Version (got $($arp.Count): $(($arp | ForEach-Object { $_.Version }) -join ', '))"
  Assert ($arp[0].Bundle -eq [bool]$Bundle) "[$Label] that entry is $(if ($Bundle) { 'RST-Setup.exe''s (the bundle''s registration)' } else { 'RST.msi''s own' })"
}

# ---- (1) the older build, and its tray
foreach ($y in '2025', '2026', '2027') {
  $dir = Join-Path $ProgramFiles64 "Autodesk\Revit $y"
  New-Item -ItemType Directory -Force $dir | Out-Null
  Set-Content -Path (Join-Path $dir 'Revit.exe') -Value 'rst-c ci stub' -NoNewline
}
Assert (@(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue).Count -eq 0) 'no Revit.exe runs (the stubs are files only)'
New-Item -ItemType Directory -Force (Split-Path $TraySettings) | Out-Null
Set-Content -Path $TraySettings -Value '{ "theme": "system", "checkForUpdates": false }' -NoNewline
$before = @(Get-TrayLog).Count
Invoke-Msiexec '/i' $OldMsi "install-$OldVersion"
Assert-OneRst $OldVersion '(1) installed'
# SC-044: the install started the tray itself, hidden.
$t1 = Wait-OneTray $before '(1)' 'the install started exactly one tray'
Assert ($t1.CommandLine -match '--startup') "(1) it runs hidden (--startup): $($t1.CommandLine)"
$oldTray = [int]$t1.ProcessId
Show-State '(1)'

# ---- (2) self-update, the tray quits after the hand-off
$before = @(Get-TrayLog).Count
$since2 = Get-Date
$h = Invoke-HandOff $NewMsi $NewVersion 'close' $oldTray
Assert ($h.closed) '(2) the old tray was asked to quit after the hand-off'
Wait-Until { -not (Get-Process -Id $oldTray -ErrorAction SilentlyContinue) } 60 '(2) the old tray exits'
$lines = Wait-Updater $before 'close'
Assert (@($lines | Where-Object { $_ -match "RST updater \(pid \d+\) applying .*\\cache\\rst\\$([regex]::Escape($NewVersion))\\RST\.msi" }).Count -eq 1) '(2) RST.TrayUpdater.exe applied the verified RST.msi from the cache'
Assert (@($lines | Where-Object { $_ -match "updater: waiting for the tray \(pid $oldTray\) to exit" }).Count -eq 1) '(2) the updater waited for the old tray'
Assert (@($lines | Where-Object { $_ -match 'updater: holding the tray.s single-instance mutex' }).Count -eq 1) '(2) the updater held the tray''s single-instance mutex while RST was replaced'
Assert (@($lines | Where-Object { $_ -match 'updater: released the tray.s single-instance mutex' }).Count -eq 1) '(2) the updater released the mutex'
Assert ([array]::IndexOf($lines, ($lines | Where-Object { $_ -match 'released the tray.s single-instance mutex' } | Select-Object -First 1)) -lt [array]::IndexOf($lines, ($lines | Where-Object { $_ -match 'updater: started the installed tray' } | Select-Object -First 1))) '(2) it handed the mutex over before starting the new tray'
Assert (@($lines | Where-Object { $_ -match 'updater: msiexec exited 0 ' }).Count -eq 1) '(2) msiexec exited 0'
Assert (@($lines | Where-Object { $_ -match 'updater: started the installed tray' }).Count -eq 1) '(2) the updater started the installed tray'
$updaterPid = [int](([regex]::Match(($lines | Where-Object { $_ -match 'RST updater \(pid' } | Select-Object -First 1), 'RST updater \(pid (\d+)\)')).Groups[1].Value)
$newTray = Wait-OneTray $before '(2)'
Assert ($newTray.ProcessId -ne $oldTray) "(2) it is a new process (pid $($newTray.ProcessId), the old was $oldTray)"
Assert ($newTray.ExecutablePath -eq $TrayExe) "(2) it runs from the install folder ($($newTray.ExecutablePath))"
Assert ($newTray.ParentProcessId -eq $updaterPid) "(2) the updater (pid $updaterPid) started it (parent $($newTray.ParentProcessId))"
# SC-044, unchanged self-update: the updater held the tray's mutex while msiexec ran, so RST.msi's RstStartTray started
# none; the updater's own start (no switch: the window, with the result) is the one tray.
Assert-StartTraySkipped $since2 '(2)'
Wait-Until { @((Get-TrayLog) | Select-Object -Skip $before | Where-Object { $_ -match "the updater reported RST $([regex]::Escape($NewVersion)): installed" }).Count -gt 0 } 30 '(2) the new tray shows the updater''s result'
Assert (-not (Test-Path (Join-Path $LocalRoot 'selfupdate.json'))) '(2) the result is shown once (selfupdate.json consumed)'
Assert (-not (Test-Path (Join-Path $LocalRoot 'updater\RST.Tray.exe'))) '(2) the updater copy never holds RST.Tray.exe'
# Decision #33: the updater copy (RST.TrayUpdater.exe and the installed tray's files) ships no third-party DLL.
$updaterDlls = @(Get-ChildItem (Join-Path $LocalRoot 'updater') -Recurse -Filter *.dll -ErrorAction SilentlyContinue)
$updaterForeign = @($updaterDlls | Where-Object { $_.Name -notmatch '^RST(\..+)?\.dll$' } | ForEach-Object { $_.Name })
Write-Host "(2) updater copy DLLs: $(($updaterDlls | ForEach-Object { $_.Name }) -join ', ')"
Assert ($updaterDlls.Count -gt 0 -and $updaterForeign.Count -eq 0) "(2) the updater copy ships no third-party DLL (decision #33)$(if ($updaterForeign) { ': ' + ($updaterForeign -join ', ') })"
Assert-OneRst $NewVersion '(2) after the self-update'
Show-State '(2)'

# ---- (3) the race: the tray still runs when msiexec does
$before = @(Get-TrayLog).Count
$since3 = Get-Date
$h = Invoke-HandOff $NewerMsi $NewerVersion 'race' $newTray.ProcessId
$lines = Wait-Updater $before 'race'
Assert (@($lines | Where-Object { $_ -match 'updater: msiexec exited 0 ' }).Count -eq 1) '(3) msiexec exited 0 with the tray running (no reboot needed)'
# SC-022: the old tray was never asked to quit, so the mutex is not free in time; the updater keeps waiting and takes it
# the moment msiexec closes that tray, then hands it over before it starts the new tray (never "going on without it").
Assert (@($lines | Where-Object { $_ -match 'updater: the tray.s single-instance mutex was not free within' }).Count -eq 1) '(3) the mutex was not free in time (the tray still ran) and the updater went on waiting for it'
Assert (@($lines | Where-Object { $_ -match 'updater: took the tray.s single-instance mutex .* once the tray let it go' }).Count -eq 1) '(3) the updater took the mutex once msiexec closed the tray'
Assert (@($lines | Where-Object { $_ -match 'not confirmed released' }).Count -eq 0) '(3) the updater confirmed the hand-over (review N3)'
Assert ([array]::IndexOf($lines, ($lines | Where-Object { $_ -match 'released the tray.s single-instance mutex' } | Select-Object -First 1)) -lt [array]::IndexOf($lines, ($lines | Where-Object { $_ -match 'updater: started the installed tray' } | Select-Object -First 1))) '(3) it handed the mutex over before starting the new tray'
$null = Wait-OneTray $before '(3)' "the updater's start leaves exactly one tray"
# One tray: a launch that came second (the updater's, if something else started one first) found it and exited.
$second = @((Get-TrayLog) | Select-Object -Skip $before | Where-Object { $_ -match 'RST tray already running for this user' })
$second | ForEach-Object { Write-Host "  log: $_" }
Write-Host "OBSERVE: (3) launches that found a tray running and exited: $($second.Count) (0 expected: Restart Manager no longer restarts the tray, MSIDISABLERMRESTART)"
Assert ($second.Count -le 1) '(3) at most one launch found a tray already running'
Assert-StartTraySkipped $since3 '(3)'
Wait-Until { @((Get-TrayLog) | Select-Object -Skip $before | Where-Object { $_ -match "the updater reported RST $([regex]::Escape($NewerVersion)): installed" }).Count -gt 0 } 30 '(3) the running tray shows the updater''s result'
Assert (-not (Test-Path (Join-Path $LocalRoot 'selfupdate.json'))) '(3) the result is shown once'
Assert-OneRst $NewerVersion '(3) after the race'
Show-State '(3)'

# ---- (3b) SC-022: two installed trays started at the same instant, round after round; exactly one survives each
$raceResult = Join-Path $OutDir 'single-instance-race.json'
$env:RST_TRAY_RACE_EXE = $TrayExe
$env:RST_TRAY_RACE_ROUNDS = "$RaceRounds"
$env:RST_TRAY_RACE_RESULT = $raceResult
& dotnet test src/RST.RuntimeTests/RST.RuntimeTests.csproj -c $Configuration --no-build --nologo `
    --filter 'FullyQualifiedName~RST.RuntimeTests.Tray.TraySingleInstanceRaceTests' --logger 'console;verbosity=normal' | Out-Host
$code = $LASTEXITCODE
Remove-Item Env:RST_TRAY_RACE_EXE, Env:RST_TRAY_RACE_ROUNDS, Env:RST_TRAY_RACE_RESULT
Assert ($code -eq 0) "(3b) two trays started together, $RaceRounds rounds: exactly one survived every round (TraySingleInstanceRaceTests)"
Assert (Test-Path $raceResult) "(3b) the race test ran (not skipped) and wrote $raceResult"
$race = Get-Content $raceResult -Raw | ConvertFrom-Json
$rounds = @($race.record)
Assert ($rounds.Count -eq $RaceRounds) "(3b) all $RaceRounds rounds ran (got $($rounds.Count))"
# Review B1: each killed survivor (the host keeps a mutex handle open) leaves an abandoned mutex: exactly the next round's
# survivor took it over (the test asserts it per round; this checks the count, the last round has no next one).
$killedBeforeNext = @($rounds | Select-Object -SkipLast 1 | Where-Object { $_.ended -like 'killed*' }).Count
$tookOver = @($rounds | Where-Object { $_.tookOverAbandoned }).Count
Assert ($killedBeforeNext -gt 0 -and $tookOver -eq $killedBeforeNext) "(3b) every killed tray's abandoned mutex was taken over by exactly the next survivor ($tookOver of $killedBeforeNext)"
$decided = @($rounds | ForEach-Object { [int]$_.decidedMs } | Sort-Object)
Write-Host "OBSERVE: (3b) $($rounds.Count) rounds; second launch gone after median $($decided[[int][math]::Floor($decided.Count / 2)]) ms, max $($decided[-1]) ms; survivors killed (abandoned mutex next): $(@($rounds | Where-Object { $_.ended -like 'killed*' }).Count)"
Wait-Until { (Get-Trays).Count -eq 0 } 30 '(3b) no tray is left running after the race'
$null = Start-Tray   # (4) uninstalls with a tray running, as before

# ---- (4) uninstall (the updater's msiexec logs are kept first: the uninstall removes them)
Copy-Item (Join-Path $LocalRoot 'logs\*') $OutDir -ErrorAction SilentlyContinue
$pc = (Get-Related)[0].ProductCode
$since4 = Get-Date
Invoke-Msiexec '/x' $pc 'uninstall'
Wait-Until { (Get-Trays).Count -eq 0 } 30 '(4) no tray runs after uninstall'
Assert ((Get-StartTrayLog $since4 '(4) uninstall').Count -eq 0) '(4) the uninstall ran no RstStartTray: nothing was started'
Assert ((Get-Updaters).Count -eq 0) '(4) no updater runs'
Assert ((Get-Related).Count -eq 0) '(4) RST is uninstalled'
Assert (-not (Test-Path $LocalRoot)) "(4) $LocalRoot (cache, updater copy, install logs) is removed"
Assert ((Get-RstArp).Count -eq 0) '(4) no RST entry is left in Add/Remove Programs'

# ---- (5) decision #34: RST installed by RST-Setup.exe is updated with RST-Setup.exe
function Invoke-Bundle([string]$Exe, [string[]]$BundleArgs, [string]$Name) {
  $log = Join-Path $OutDir "$Name.log"
  Write-Host "$Exe $($BundleArgs -join ' ') /log $log"
  $p = Start-Process $Exe -ArgumentList (@($BundleArgs) + @('/log', "`"$log`"")) -PassThru
  $null = $p.Handle
  if (-not $p.WaitForExit(600000)) { $p | Stop-Process -Force; throw "FAIL: $Name timed out" }   # its own handle, never -Wait
  Write-Host "  exit $($p.ExitCode)"
  if ($p.ExitCode -notin 0, 3010) { Get-Content $log -Tail 60 | Write-Host; throw "$Name exited $($p.ExitCode)" }
}
$before = @(Get-TrayLog).Count
Invoke-Bundle $OldBundle @('/quiet', '/norestart') "bundle-install-$OldVersion"
Assert-OneRst $OldVersion '(5) installed by RST-Setup.exe' -Bundle
$t5 = Wait-OneTray $before '(5)' 'the bundle''s install started exactly one tray'   # SC-044: RST.msi's RstStartTray
Assert ($t5.CommandLine -match '--startup') "(5) it runs hidden (--startup): $($t5.CommandLine)"
$bundleTray = [int]$t5.ProcessId
Show-State '(5)'

$before = @(Get-TrayLog).Count
$since5 = Get-Date
$h = Invoke-HandOff $NewBundle $NewVersion 'bundle' $bundleTray -Bundle
Assert ($h.kind -eq 'Bundle') '(5) the tray chose RST-Setup.exe: it found the bundle''s per-user registration'
Assert ($h.closed) '(5) the old tray was asked to quit after the hand-off'
Wait-Until { -not (Get-Process -Id $bundleTray -ErrorAction SilentlyContinue) } 60 '(5) the old tray exits'
$lines = Wait-Updater $before 'bundle'
Assert (@($lines | Where-Object { $_ -match "RST updater \(pid \d+\) applying .*\\cache\\rst\\$([regex]::Escape($NewVersion))\\RST-Setup\.exe" }).Count -eq 1) '(5) RST.TrayUpdater.exe applied the verified RST-Setup.exe from the cache'
Assert (@($lines | Where-Object { $_ -match 'updater: ".*RST-Setup\.exe" /quiet /norestart /log' }).Count -eq 1) '(5) it ran the bundle /quiet /norestart, with no companion switches'
Assert (@($lines | Where-Object { $_ -match 'updater: RST-Setup\.exe exited 0 ' }).Count -eq 1) '(5) RST-Setup.exe exited 0'
Assert (@($lines | Where-Object { $_ -match 'updater: holding the tray.s single-instance mutex' }).Count -eq 1) '(5) the updater held the tray''s single-instance mutex while RST was replaced'
$bundleNew = Wait-OneTray $before '(5)'
Assert ($bundleNew.ProcessId -ne $bundleTray) '(5) it is a new process'
Wait-Until { @((Get-TrayLog) | Select-Object -Skip $before | Where-Object { $_ -match "the updater reported RST $([regex]::Escape($NewVersion)): installed" }).Count -gt 0 } 30 '(5) the new tray shows the updater''s result'
Assert-OneRst $NewVersion '(5) after the bundle self-update' -Bundle
Assert-StartTraySkipped $since5 '(5)'
Show-State '(5) after'

# ---- (6) the bundle's own uninstall still works and leaves nothing
Copy-Item (Join-Path $LocalRoot 'logs\*') $OutDir -ErrorAction SilentlyContinue
$arp = (Get-RstArp)[0]
Write-Host "bundle uninstall: $($arp.QuietUninstall)"
Assert ($arp.QuietUninstall -match '^"?(.+?\.exe)"?\s+(.*)$') '(6) the bundle entry has a quiet uninstall command'
$uninstallExe = $Matches[1]
$uninstallArgs = @($Matches[2] -split '\s+' | Where-Object { $_ }) + @('/norestart')
$since6 = Get-Date
Invoke-Bundle $uninstallExe $uninstallArgs 'bundle-uninstall'
Wait-Until { (Get-Trays).Count -eq 0 } 30 '(6) no tray runs after the bundle uninstall'
Assert ((Get-StartTrayLog $since6 '(6) bundle uninstall').Count -eq 0) '(6) the bundle uninstall ran no RstStartTray: nothing was started'
Assert ((Get-Related).Count -eq 0) '(6) the bundle uninstall removed RST'
Assert ((Get-RstArp).Count -eq 0) '(6) no RST entry is left in Add/Remove Programs'
Assert (-not (Test-Path $LocalRoot)) "(6) $LocalRoot is removed"

Get-TrayLog | Set-Content -Path (Join-Path $OutDir 'tray.log')
Write-Host 'PASS: AC8 self-update: RST.msi (close, race) and RST-Setup.exe (decision #34); one tray and one RST entry each time; clean uninstalls'
