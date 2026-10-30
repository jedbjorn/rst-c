<#
  ci-smoke.ps1 — Windows Installer checks for RST.msi on a clean CI runner
  (feature #11). Called by .github/workflows/_build.yml; no Revit needed.

  Revit detection (decision #10) is driven through the default-folder
  fallback: stub Revit.exe files in C:\Program Files\Autodesk\Revit <year>\
  stand in for installed Revit years. (The uninstall-entry path is
  exercised on the VM, AC6.)

  Every mode first checks the built package: RemoveExistingProducts is
  sequenced inside the install transaction, every component belongs to one
  Revit year and carries Condition 'REVIT<year>FOUND OR REMOVE~="ALL"' and
  the Transitive attribute, the per-year component counts match the staged
  files (-StagedRoot), and the launch condition is present.

  -Mode Install  (permanent smoke, D6 / AC3; seeds a user-data probe)
      (c) no Revit           -> the launch condition refuses the install,
                                nothing is installed
      (a) Revit 2026 + 2027  -> only those years' trees are installed
      (d) + Revit 2025, Repair (/fa) -> 2025 is added; then Revit 2026
          removed, Repair -> 2026 is dropped; 2026 back, Repair -> restored
      (b) all three, a second build of the same commit (-SameVersionMsi:
          same ProductVersion, new ProductCode, the rc -> final case) over
          the first -> exactly one RST product with all six trees; the same
          file again -> still one product
      (e) stubs removed, uninstall -> every tree gone, probe survives
      The tray (feature #17, spec #20 AC1), alongside:
      (a) RST.Tray.exe installed and the HKCU Run value RST = "<exe>" --startup
      (t) --startup starts hidden; a second launch exits 0 and the first shows
          its window; a second --startup exits 0; one RST.Tray process
      (d) the repair closes that running tray (Restart Manager, clean exit 0)
          and Restart Manager starts it again, hidden, when the install ends
      (b) the same-version upgrade with a tray running: closed cleanly, the
          upgrade completes, the cache (RemoveFolderEx) survives the old
          version's removal
      (e) uninstall with a tray running and Restart Manager off: CloseApplication's
          WM_CLOSE closes it cleanly (exit 0, logged as WM_CLOSE),
          the Run value, %LocalAppData%\Programs\RST and the cache
          (RemoveFolderEx) are gone; the settings and log under %AppData%\RST
          (user data) stay
      (u) -OlderMsi (this commit built with a lower ProductVersion) installed,
          a tray running, then this RST.msi over it: a real MajorUpgrade; the
          tray is closed cleanly and restarted, one product at the new version,
          the cache survives; then uninstall with a tray running and Restart
          Manager on (the Settings > Apps path) leaves no tray running and
          removes the cache, the Run value and the files
      The package checks also cover the tray's components (unconditional, one
      per staged build/Tray file), the Run value, CloseApplication and
      RemoveFolderEx rows.

  -Mode Migrate -From Preview|Unified -OldMsi <released asset>
      Upgrade from a public release, with all three Revit stubs.
        Preview (AC4): RST-R27.msi v2.2.0 -> new RST.msi. The Upgrade row on
          the retired R27 code must remove "RST (Revit 2027 preview)" and the
          installed R27 engine must be the newly staged one.
        Unified (AC5): RST.msi v2.2.0 or v3.0.0 -> new RST.msi. MajorUpgrade must leave
          exactly one RST product, at the new version, with all six trees.
        Both: the tray and its Run value are installed after the upgrade.

  Product queries go through the WindowsInstaller.Installer COM object via
  InvokeMember (it has no type library PowerShell can bind to).
#>
param(
  [Parameter(Mandatory)][ValidateSet('Install', 'Migrate')][string]$Mode,
  [string]$Msi = 'installer/bin/Release/RST.msi',
  [ValidateSet('Preview', 'Unified')][string]$From,
  [string]$OldMsi,
  [string]$StagedR27 = 'build/R27',
  [string]$StagedRoot,
  [string]$SameVersionMsi,
  [string]$OlderMsi
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$UnifiedCode = '{AF0331FC-5280-492A-94BD-95EDF108BE74}'
$PreviewCode = '{02BE4399-A563-4B15-BD12-ECF5D8486FB3}'   # retired R27 preview
$AppData = $env:APPDATA
$LogDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$ProgramFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$Msi = (Resolve-Path $Msi).Path
$LaunchConditionText = 'Installed OR REVIT2025FOUND OR REVIT2026FOUND OR REVIT2027FOUND'
$LaunchMessage = 'RST needs Revit 2025, 2026 or 2027, and none was found on this computer.'
$Transitive = 64   # msidbComponentAttributesTransitive

# The RST tray (feature #17, spec #20 §1, AC1).
$TrayDirId = 'TrayDir'
$RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run'
$RunValueName = 'RST'
$TrayDir = Join-Path $env:LOCALAPPDATA 'Programs\RST\Tray'
$TrayExe = Join-Path $TrayDir 'RST.Tray.exe'
$TrayCache = Join-Path $env:LOCALAPPDATA 'RST\Tray\cache'
$TraySettings = Join-Path $AppData 'RST\tray_settings.json'
$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$TrayMutex = "Local\RST.Tray.$UserSid"

# Revit year -> engine major, and the key files of each year's two trees.
$Years = [ordered]@{ '2025' = 'R25'; '2026' = 'R26'; '2027' = 'R27' }
function Get-YearTrees([string]$Year) {
  $nn = $Years[$Year]
  [ordered]@{
    "Autodesk\Revit\Addins\$Year" = @('RST.addin', 'RST.Bootstrap.dll')
    "RST\$nn\app"                 = @('RST.Engine.dll', 'RST.UI.dll', 'RST.Core.dll', 'RST.Engine.deps.json')
  }
}

$Installer = New-Object -ComObject WindowsInstaller.Installer
# `return , $x` stops PowerShell unrolling an enumerable COM result (a
# StringList, an empty one would otherwise arrive as $null).
function Get-ComProp($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $Obj, $ArgList)
}
function Invoke-ComMethod($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Obj, $ArgList)
}

function Get-Related([string]$UpgradeCode) {
  $list = Get-ComProp $Installer 'RelatedProducts' @($UpgradeCode)
  $n = Get-ComProp $list 'Count'
  $out = @()
  for ($i = 0; $i -lt $n; $i++) {
    $pc = Get-ComProp $list 'Item' @($i)
    $out += [pscustomobject]@{
      ProductCode = $pc
      Name        = Get-ComProp $Installer 'ProductInfo' @($pc, 'ProductName')
      Version     = Get-ComProp $Installer 'ProductInfo' @($pc, 'VersionString')
    }
  }
  return , $out
}

function Show-Related([string]$Label) {
  foreach ($code in $UnifiedCode, $PreviewCode) {
    $r = Get-Related $code
    $desc = if ($r.Count) { ($r | ForEach-Object { "$($_.ProductCode) '$($_.Name)' $($_.Version)" }) -join '; ' } else { '(none)' }
    Write-Host "RelatedProducts[$Label] $code -> $($r.Count): $desc"
  }
}

function Get-MsiQuery([string]$Path, [string]$Sql, [int]$Cols = 1) {
  $db = Invoke-ComMethod $Installer 'OpenDatabase' @($Path, 0)
  $view = Invoke-ComMethod $db 'OpenView' @($Sql)
  Invoke-ComMethod $view 'Execute' | Out-Null
  $rows = @()
  while ($rec = Invoke-ComMethod $view 'Fetch') {
    $row = @(); for ($c = 1; $c -le $Cols; $c++) { $row += Get-ComProp $rec 'StringData' @($c) }
    $rows += , $row
  }
  Invoke-ComMethod $view 'Close' | Out-Null
  # Release the read-only handle so msiexec can open the package.
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  return , $rows
}
function Get-MsiRows([string]$Path, [string]$Table) {
  $db = Invoke-ComMethod $Installer 'OpenDatabase' @($Path, 0)
  $view = Invoke-ComMethod $db 'OpenView' @("SELECT * FROM ``$Table``")
  Invoke-ComMethod $view 'Execute' | Out-Null
  $rows = @()
  while ($rec = Invoke-ComMethod $view 'Fetch') {
    $n = Get-ComProp $rec 'FieldCount'
    $row = @(); for ($c = 1; $c -le $n; $c++) { $row += Get-ComProp $rec 'StringData' @($c) }
    $rows += , $row
  }
  Invoke-ComMethod $view 'Close' | Out-Null
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  return , $rows
}
function Get-MsiProperty([string]$Path, [string]$Name) {
  (Get-MsiQuery $Path "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'")[0][0]
}

# Runs msiexec; returns the exit code. Throws on failure unless -ExpectFail.
# Waits on msiexec itself, never on its process tree: PowerShell 7's Start-Process -Wait waits for every
# descendant, and Restart Manager starts the RST tray again from msiexec's tree at the end of an install
# (RegisterApplicationRestart), so -Wait would never return while the restarted tray runs. A hard timeout
# dumps the verbose log and the process list, ends msiexec and fails, so CI cannot hang.
$MsiexecTimeoutSeconds = 300
$script:LastMsiexecEnd = $null
function Invoke-Msiexec([string]$Verb, [string]$Path, [string]$Log, [switch]$ExpectFail, [string]$Extra = '') {
  Write-Host "msiexec $Verb $Path $Extra (log: $Log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Path`" /qn /l*v `"$Log`" $Extra" -PassThru
  $null = $p.Handle   # keep a handle so ExitCode is readable after the process exits
  if (-not $p.WaitForExit($MsiexecTimeoutSeconds * 1000)) {
    Write-Host "TIMEOUT: msiexec $Verb did not finish in $MsiexecTimeoutSeconds s; processes:"
    Get-CimInstance Win32_Process | Where-Object { $_.Name -match 'msiexec|RST' } |
      ForEach-Object { Write-Host "  $($_.ProcessId) parent $($_.ParentProcessId) $($_.Name) $($_.CommandLine)" }
    if (Test-Path $Log) { Write-Host "---- last 200 lines of $Log"; Get-Content $Log -Tail 200 | Write-Host }
    Show-TrayLog
    $p | Stop-Process -Force -ErrorAction SilentlyContinue
    throw "FAIL: msiexec $Verb timed out after $MsiexecTimeoutSeconds s"
  }
  $script:LastMsiexecEnd = Get-Date
  Write-Host "  exit $($p.ExitCode)"
  if (-not $ExpectFail -and $p.ExitCode -notin 0, 3010) {
    if (Test-Path $Log) { Get-Content $Log -Tail 60 | Write-Host }
    throw "msiexec $Verb exited $($p.ExitCode)"
  }
  return $p.ExitCode
}

function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { throw "FAIL: $What" }
  Write-Host "PASS: $What"
}

# Stub Revit.exe files for exactly the given years; the others are removed.
function Set-RevitStubs([string[]]$Present) {
  foreach ($y in $Years.Keys) {
    $dir = Join-Path $ProgramFiles64 "Autodesk\Revit $y"
    $exe = Join-Path $dir 'Revit.exe'
    if ($Present -contains $y) {
      New-Item -ItemType Directory -Force $dir | Out-Null
      Set-Content -Path $exe -Value 'rst-c ci stub' -NoNewline
    } elseif (Test-Path $exe) {
      Remove-Item $exe -Force
      if (-not (Get-ChildItem $dir -Force)) { Remove-Item $dir -Force }
    }
  }
  Write-Host "Revit stubs: $(if ($Present) { $Present -join ', ' } else { '(none)' })"
}

# Asserts that exactly the given years are installed: their key files are
# present, and the other years have no RST file in either tree.
function Assert-Years([string[]]$Present, [string]$Label) {
  foreach ($y in $Years.Keys) {
    $trees = Get-YearTrees $y
    foreach ($t in $trees.Keys) {
      $dir = Join-Path $AppData $t
      if ($Present -contains $y) {
        foreach ($f in $trees[$t]) {
          Assert (Test-Path (Join-Path $dir $f) -PathType Leaf) "[$Label] $t\$f present"
        }
      } else {
        # @(...) around the whole if: an if expression yielding @() unrolls to $null.
        $left = @(if (Test-Path $dir) { Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -like 'RST*' -or $t -like 'RST\*' } })
        Assert ($left.Count -eq 0) "[$Label] $t has no RST files (Revit $y not detected)"
      }
    }
  }
}

function Assert-OneProduct([string]$ProductCode, [string]$Version, [string]$Label) {
  Show-Related $Label
  $r = Get-Related $UnifiedCode
  Assert ($r.Count -eq 1 -and $r[0].ProductCode -eq $ProductCode -and $r[0].Version -eq $Version) "[$Label] exactly one RST product, $ProductCode at $Version"
}

function Show-LogLines([string]$Log, [string[]]$Extra = @()) {
  $pat = @('FindRelatedProducts', 'RemoveExistingProducts', 'R27PREVIEWFOUND', 'WIX_UPGRADE_DETECTED',
           'REVIT2025FOUND', 'REVIT2026FOUND', 'REVIT2027FOUND') + $Extra
  Write-Host "---- $([IO.Path]::GetFileName($Log)): upgrade / detection lines"
  Select-String -Path $Log -Pattern $pat -SimpleMatch |
    Where-Object { $_.Line -notmatch 'SecureCustomProperties' } |
    ForEach-Object { Write-Host $_.Line }
  Write-Host '----'
}

function Seed-Probe {
  $probe = Join-Path $AppData 'RST\profiles\probe.txt'
  New-Item -ItemType Directory -Force (Split-Path $probe) | Out-Null
  Set-Content -Path $probe -Value 'rst-c ci probe' -NoNewline
  return $probe
}
function Assert-Probe([string]$Probe, [string]$Label) {
  Assert ((Test-Path $Probe) -and (Get-Content $Probe -Raw) -eq 'rst-c ci probe') "[$Label] user data probe survives"
}

# ---- The tray (feature #17, AC1)
function Get-RunValue {
  $k = Get-ItemProperty -Path "HKCU:\$RunKey" -Name $RunValueName -ErrorAction SilentlyContinue
  if ($k) { return $k.$RunValueName } else { return $null }
}
function Assert-TrayInstalled([string]$Label) {
  Assert (Test-Path $TrayExe -PathType Leaf) "[$Label] $TrayExe present"
  $run = Get-RunValue
  Assert ($run -eq "`"$TrayExe`" --startup") "[$Label] HKCU Run value RST = `"$TrayExe`" --startup (got '$run')"
}
function Test-TrayMutex {
  $m = $null
  $open = [Threading.Mutex]::TryOpenExisting($TrayMutex, [ref]$m)
  if ($m) { $m.Dispose() }
  return $open
}
# Seeds the tray's settings so the one-time update-check notice does not open a window of its own.
function Seed-TraySettings {
  New-Item -ItemType Directory -Force (Split-Path $TraySettings) | Out-Null
  Set-Content -Path $TraySettings -Value '{ "theme": "system", "checkForUpdates": true, "updateNoticeShownUtc": "2026-10-30T00:00:00+00:00" }' -NoNewline
}
# Starts the installed tray and waits until it holds its single-instance mutex.
function Start-Tray([string[]]$TrayArgs, [string]$Label) {
  $p = if ($TrayArgs) { Start-Process $TrayExe -ArgumentList $TrayArgs -PassThru } else { Start-Process $TrayExe -PassThru }
  $null = $p.Handle   # keep a handle so ExitCode is readable after the process exits
  $deadline = (Get-Date).AddSeconds(45)
  while (-not (Test-TrayMutex)) {
    if ($p.HasExited) { Show-TrayLog; throw "FAIL: [$Label] tray exited ($($p.ExitCode)) before taking its mutex" }
    if ((Get-Date) -gt $deadline) { Show-TrayLog; throw "FAIL: [$Label] tray did not take $TrayMutex within 45 s" }
    Start-Sleep -Milliseconds 250
  }
  Write-Host "[$Label] tray pid $($p.Id) holds $TrayMutex"
  return $p
}
# A tray other than -OldPid (one Restart Manager started again), or $null after -Seconds.
function Wait-TrayRestart([int]$OldPid, [int]$Seconds) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  do {
    $p = @(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Where-Object { $_.Id -ne $OldPid })
    if ($p.Count -gt 0) { return $p[0] }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  return $null
}
# Ends every running tray (a test reset, not a product path) and waits for the mutex to go.
function Stop-Trays {
  Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | ForEach-Object { Write-Host "stopping tray pid $($_.Id)"; $_ | Stop-Process -Force; $_.WaitForExit(15000) | Out-Null }
  $deadline = (Get-Date).AddSeconds(15)
  while ((Test-TrayMutex) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
}
function Seed-CacheProbe {
  New-Item -ItemType Directory -Force $TrayCache | Out-Null
  $f = Join-Path $TrayCache 'probe.msi'
  Set-Content -Path $f -Value 'rst-c ci cache probe' -NoNewline
  return $f
}
function Wait-MainWindow($Proc, [int]$Seconds) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  do {
    $Proc.Refresh()
    if ($Proc.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
    Start-Sleep -Milliseconds 250
  } while ((Get-Date) -lt $deadline -and -not $Proc.HasExited)
  return $false
}
function Show-TrayLog {
  Get-ChildItem (Join-Path $AppData 'RST\logs') -Filter 'tray_*.log' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "---- $($_.Name)"
    Get-Content $_.FullName | Write-Host
  }
  Write-Host '----'
}
# Single instance (AC1): a second launch exits 0 and the first, started hidden, shows its window.
function Test-TraySingleInstance([string]$Label) {
  Seed-TraySettings
  $first = Start-Tray @('--startup') "$Label first"
  Start-Sleep -Seconds 2
  $first.Refresh()
  Assert ($first.MainWindowHandle -eq [IntPtr]::Zero) "[$Label] --startup starts hidden (no main window)"

  $second = Start-Process $TrayExe -PassThru
  $null = $second.Handle
  Assert ($second.WaitForExit(30000)) "[$Label] a second launch exits"
  Assert ($second.ExitCode -eq 0) "[$Label] the second launch exits with 0 (got $($second.ExitCode))"
  Assert (-not $first.HasExited) "[$Label] the first tray is still running"
  Assert (Wait-MainWindow $first 20) "[$Label] the second launch showed the first tray's window"
  Assert ($first.MainWindowTitle -eq 'RST') "[$Label] the shown window is 'RST' (got '$($first.MainWindowTitle)')"
  Assert (@(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue).Count -eq 1) "[$Label] exactly one RST.Tray process"

  $third = Start-Process $TrayExe -ArgumentList '--startup' -PassThru
  $null = $third.Handle
  Assert ($third.WaitForExit(30000) -and $third.ExitCode -eq 0) "[$Label] a second --startup launch exits with 0"
  return $first
}
# A running tray is closed by Windows Installer and exits cleanly (code 0).
# -How is a pattern for the path in the tray's own exit line: 'WM_CLOSE' (util:CloseApplication) or 'session ending' (Windows
# Installer's Restart Manager, which reaches the tray first when it is on).
# Without -NoRestart a tray Restart Manager started again afterwards (RegisterApplicationRestart) is allowed and
# recorded; with it (Restart Manager off, or the files gone) nothing may be running.
# -NoRestart: after a 10 s grace (Restart Manager's restart runs as msiexec exits), nothing may be running.
# Otherwise the tray must come back (RegisterApplicationRestart), hidden; the restarted process is returned.
function Assert-TrayClosed($Proc, [string]$Label, [string]$How, [switch]$NoRestart) {
  $exited = $Proc.WaitForExit(30000)
  if (-not $exited) { Show-TrayLog }
  Assert $exited "[$Label] the running tray (pid $($Proc.Id)) was closed"
  Assert ($Proc.ExitCode -eq 0) "[$Label] it exited cleanly (exit $($Proc.ExitCode))"
  $again = $null
  if ($NoRestart) {
    $late = Wait-TrayRestart $Proc.Id 10
    if ($late) { Write-Host "  unexpected tray pid $($late.Id): $((Get-CimInstance Win32_Process -Filter "ProcessId=$($late.Id)").CommandLine)" }
    Assert ($null -eq $late) "[$Label] no tray started again within 10 s"
    Assert (-not (Test-TrayMutex)) "[$Label] its mutex is released"
    Assert (@(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue).Count -eq 0) "[$Label] no RST.Tray process running"
  } else {
    $again = Wait-TrayRestart $Proc.Id 20
    Assert ($null -ne $again) "[$Label] Restart Manager started the tray again after the install (RegisterApplicationRestart)"
    if ($again) {
      $w = Get-CimInstance Win32_Process -Filter "ProcessId=$($again.Id)"
      $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($w.ParentProcessId)" -ErrorAction SilentlyContinue
      $when = if ($script:LastMsiexecEnd) { "{0:N1} s after msiexec exited" -f ($again.StartTime - $script:LastMsiexecEnd).TotalSeconds } else { 'n/a' }
      Write-Host "OBSERVE: [$Label] Restart Manager restarted the tray: pid $($again.Id), started $($again.StartTime.ToString('HH:mm:ss.fff')) ($when), parent $($w.ParentProcessId) $(if ($parent) { $parent.Name }), args '$($w.CommandLine)'"
      Assert ($w.CommandLine -match '--startup') "[$Label] the restarted tray runs hidden (--startup)"
    }
  }
  $exit = @(Get-ChildItem (Join-Path $AppData 'RST\logs') -Filter 'tray_*.log' | ForEach-Object { Get-Content $_.FullName } |
            Where-Object { $_.Contains("[$($Proc.Id)]") -and $_.Contains('RST tray exited') })
  Write-Host "[$Label] tray log: $($exit -join ' / ')"
  Assert (@($exit | Where-Object { $_ -match $How }).Count -eq 1) "[$Label] the tray logged its exit by $How"
  return $again
}

# ---- Package checks (every mode)
$newVersion = Get-MsiProperty $Msi 'ProductVersion'
$newProductCode = Get-MsiProperty $Msi 'ProductCode'
Write-Host "New RST.msi: ProductVersion=$newVersion ProductCode=$newProductCode"

# MajorUpgrade Schedule="afterInstallInitialize": RemoveExistingProducts
# must sit after InstallInitialize (inside the install transaction, so a
# failed install rolls the old product back in) and before InstallFiles.
$seq = @{}
foreach ($row in (Get-MsiQuery $Msi 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) { $seq[$row[0]] = [int]$row[1] }
Write-Host "InstallExecuteSequence: InstallValidate=$($seq['InstallValidate']) InstallInitialize=$($seq['InstallInitialize']) RemoveExistingProducts=$($seq['RemoveExistingProducts']) InstallFiles=$($seq['InstallFiles'])"
Assert ($seq.ContainsKey('RemoveExistingProducts') -and $seq['RemoveExistingProducts'] -gt $seq['InstallInitialize'] -and $seq['RemoveExistingProducts'] -lt $seq['InstallFiles']) 'RemoveExistingProducts sequenced after InstallInitialize and before InstallFiles'

# Component table (decision #10): every component sits under one year's
# Addins<year>Dir or R<NN>AppDir and carries that year's condition and the
# Transitive attribute.
$roots = @{}
foreach ($y in $Years.Keys) { $roots["Addins${y}Dir"] = $y; $roots["$($Years[$y])AppDir"] = $y }
$parent = @{}
foreach ($row in (Get-MsiQuery $Msi 'SELECT `Directory`, `Directory_Parent` FROM `Directory`' 2)) { $parent[$row[0]] = $row[1] }
$perYear = @{ '2025' = 0; '2026' = 0; '2027' = 0 }
$bad = @()
$trayComponents = @()
$components = Get-MsiQuery $Msi 'SELECT `Component`, `Directory_`, `Attributes`, `Condition` FROM `Component`' 4
foreach ($c in $components) {
  $d = $c[1]; $year = $null; $isTray = $false
  while ($d) { if ($d -eq $TrayDirId) { $isTray = $true; break }; if ($roots.ContainsKey($d)) { $year = $roots[$d]; break }; $d = $parent[$d] }
  # The tray (feature #17) is not per Revit year: unconditional, not Transitive.
  if ($isTray) {
    if ($c[3]) { $bad += "$($c[0]) (tray) has condition '$($c[3])', expected none" }
    $trayComponents += , $c
    continue
  }
  if (-not $year) { $bad += "$($c[0]) in $($c[1]) is outside every Revit year's trees"; continue }
  $want = "REVIT${year}FOUND OR REMOVE~=`"ALL`""
  if ($c[3] -ne $want) { $bad += "$($c[0]) ($year) has condition '$($c[3])', expected '$want'" }
  if (([int]$c[2] -band $Transitive) -eq 0) { $bad += "$($c[0]) ($year) is not Transitive (attributes $($c[2]))" }
  $perYear[$year]++
}
Write-Host "Components: $($components.Count) (2025=$($perYear['2025']) 2026=$($perYear['2026']) 2027=$($perYear['2027']) tray=$($trayComponents.Count)); problems: $($bad.Count)"
$bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
Assert ($components.Count -gt 0 -and $bad.Count -eq 0) "every Revit-year component carries 'REVIT<year>FOUND OR REMOVE~=`"ALL`"' for its year and is Transitive; tray components are unconditional"
if ($StagedRoot) {
  foreach ($y in $Years.Keys) {
    $nn = $Years[$y]
    $staged = @(Get-ChildItem (Join-Path $StagedRoot "$nn\addins"), (Join-Path $StagedRoot "$nn\app") -Recurse -File).Count
    Assert ($perYear[$y] -eq $staged) "Revit $y components ($($perYear[$y])) = staged $nn files ($staged)"
  }
  # One component per staged tray file, plus TrayStartup (the Run value and the cache's RemoveFolderEx).
  $stagedTray = @(Get-ChildItem (Join-Path $StagedRoot 'Tray') -Recurse -File).Count
  Assert ($trayComponents.Count -eq $stagedTray + 1) "tray components ($($trayComponents.Count)) = staged Tray files ($stagedTray) + TrayStartup"
}

# The tray's package rows (feature #17, AC1): the HKMU Run value, CloseApplication with WM_CLOSE and no
# reboot prompt, RemoveFolderEx on the cache folder.
$runRows = Get-MsiQuery $Msi 'SELECT `Root`, `Key`, `Name`, `Value`, `Component_` FROM `Registry`' 5
$run = @($runRows | Where-Object { $_[1] -eq $RunKey -and $_[2] -eq $RunValueName })
Assert ($run.Count -eq 1 -and $run[0][0] -eq '-1' -and $run[0][3] -eq '"[TrayDir]RST.Tray.exe" --startup') "Registry table: HKMU Run value RST = `"[TrayDir]RST.Tray.exe`" --startup (got $(if ($run.Count) { "root $($run[0][0]) '$($run[0][3])'" } else { 'none' }))"
# The util extension's tables are found by name (Wix4CloseApplication / Wix4RemoveFolderEx in WiX v4+) and
# read whole; the uninstall below proves the behavior (a clean WM_CLOSE exit, the cache gone).
$utilTables = @((Get-MsiQuery $Msi 'SELECT `Name` FROM `_Tables`') | ForEach-Object { $_[0] } | Where-Object { $_ -match 'CloseApplication|RemoveFolderEx' })
Write-Host "util tables: $($utilTables -join ', ')"
$closeTable = @($utilTables | Where-Object { $_ -match 'CloseApplication' })
Assert ($closeTable.Count -eq 1) 'the package has a CloseApplication table'
$close = Get-MsiRows $Msi $closeTable[0]
$close | ForEach-Object { Write-Host "  $($closeTable[0]): $($_ -join ' | ')" }
Assert (@($close | Where-Object { $_ -contains 'RST.Tray.exe' }).Count -eq 1) 'CloseApplication targets RST.Tray.exe'
$rmfTable = @($utilTables | Where-Object { $_ -match 'RemoveFolderEx' })
Assert ($rmfTable.Count -eq 1) 'the package has a RemoveFolderEx table'
$rmf = Get-MsiRows $Msi $rmfTable[0]
$rmf | ForEach-Object { Write-Host "  $($rmfTable[0]): $($_ -join ' | ')" }
Assert (@($rmf | Where-Object { $_ -contains 'RSTTRAYCACHEDIR' }).Count -eq 1) 'RemoveFolderEx removes RSTTRAYCACHEDIR (the tray cache)'
$launch = Get-MsiQuery $Msi 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 2
Assert (@($launch | Where-Object { $_[0] -eq $LaunchConditionText -and $_[1] -eq $LaunchMessage }).Count -eq 1) 'LaunchCondition table holds the "none was found" check'
$secure = Get-MsiProperty $Msi 'SecureCustomProperties'
$notSecure = @(@('REVIT2025FOUND', 'REVIT2026FOUND', 'REVIT2027FOUND') | Where-Object { ($secure -split ';') -notcontains $_ })
Assert ($notSecure.Count -eq 0) "REVIT<year>FOUND properties are Secure (missing: $($notSecure -join ', '))"

Show-Related 'start'
Assert ((Get-Related $UnifiedCode).Count -eq 0 -and (Get-Related $PreviewCode).Count -eq 0) 'runner starts with no RST product'

if ($Mode -eq 'Install') {
  $probe = Seed-Probe

  # (c) no Revit: the launch condition refuses a first install.
  Set-RevitStubs @()
  $logC = Join-Path $LogDir 'rst-install-none.log'
  $code = Invoke-Msiexec '/i' $Msi $logC -ExpectFail
  Assert ($code -ne 0 -and $code -ne 3010) "(c) install without Revit fails (exit $code)"
  Assert (Select-String -Path $logC -Pattern $LaunchMessage -SimpleMatch -Quiet) '(c) log shows the launch-condition message'
  Assert ((Get-Related $UnifiedCode).Count -eq 0) '(c) no RST product installed'
  Assert-Years @() '(c)'

  # (a) Revit 2026 + 2027 only.
  Set-RevitStubs @('2026', '2027')
  $logA = Join-Path $LogDir 'rst-install-2026-2027.log'
  Invoke-Msiexec '/i' $Msi $logA | Out-Null
  Show-LogLines $logA
  Assert-OneProduct $newProductCode $newVersion '(a)'
  Assert-Years @('2026', '2027') '(a)'
  # (t) the tray (feature #17, AC1): installed with the Run value; one per user.
  Assert-TrayInstalled '(a)'
  $trayA = Test-TraySingleInstance '(t)'
  $sm = Get-ChildItem (Join-Path $AppData 'RST\R27\app') -Recurse -Filter System.Management.dll |
        ForEach-Object { $_.FullName.Substring($AppData.Length + 1) }
  Write-Host "R27 System.Management.dll: $($sm -join ', ')"

  # (d) Revit 2025 installed later: Repair adds it. Then 2026 removed:
  # Repair drops it. Then 2026 back: Repair restores it.
  Set-RevitStubs @('2025', '2026', '2027')
  $logD = Join-Path $LogDir 'rst-repair-add-2025.log'
  $codeD = Invoke-Msiexec '/fa' $Msi $logD
  Show-LogLines $logD @('CloseApplication', 'RST.Tray.exe', 'RESTART MANAGER')
  # 3010 would mean a file was still in use (e.g. a tray restarted mid-install); the restart must come after.
  Assert ($codeD -eq 0) "(d) the repair with a tray running needs no reboot (exit $codeD)"
  Assert-OneProduct $newProductCode $newVersion '(d) repair'
  Assert-Years @('2025', '2026', '2027') '(d) repair added 2025'
  # The repair replaced the tray's files: Windows Installer (Restart Manager) closed the tray from (t) first.
  $null = Assert-TrayClosed $trayA '(d) repair' 'session ending|WM_CLOSE'
  Assert-TrayInstalled '(d) repair'

  Set-RevitStubs @('2025', '2027')
  Invoke-Msiexec '/fa' $Msi (Join-Path $LogDir 'rst-repair-drop-2026.log') | Out-Null
  Assert-Years @('2025', '2027') '(d) repair dropped 2026'

  Set-RevitStubs @('2025', '2026', '2027')
  Invoke-Msiexec '/fa' $Msi (Join-Path $LogDir 'rst-repair-restore-2026.log') | Out-Null
  Assert-Years @('2025', '2026', '2027') '(d) repair restored 2026'
  Assert-Probe $probe '(d)'

  # (b) all three Revit years: a second build of the same commit (same
  # ProductVersion, new ProductCode: the rc -> final case) replaces the
  # first (AllowSameVersionUpgrades) and installs all six trees.
  $uninstallMsi = $Msi
  if ($SameVersionMsi) {
    $SameVersionMsi = (Resolve-Path $SameVersionMsi).Path
    $v2 = Get-MsiProperty $SameVersionMsi 'ProductVersion'
    $pc2 = Get-MsiProperty $SameVersionMsi 'ProductCode'
    Write-Host "Same-version MSI: ProductVersion=$v2 ProductCode=$pc2"
    Assert ($v2 -eq $newVersion -and $pc2 -ne $newProductCode) '(b) second build has the same ProductVersion and a new ProductCode'
    # A tray running and a cache file in place across the upgrade (review N3): the old version's removal
    # (UPGRADINGPRODUCTCODE set) must leave the cache.
    Stop-Trays
    $trayB = Start-Tray @('--startup') '(b)'
    $cacheB = Seed-CacheProbe
    $logB = Join-Path $LogDir 'rst-install-samever.log'
    $codeB = Invoke-Msiexec '/i' $SameVersionMsi $logB
    Show-LogLines $logB @($newProductCode, 'RemoveFolder', 'RESTART MANAGER')
    Assert ($codeB -eq 0) "(b) the upgrade with a tray running needs no reboot (exit $codeB)"
    Assert-OneProduct $pc2 $v2 '(b) same-version install'
    Assert-Years @('2025', '2026', '2027') '(b) all three detected'
    $trayB2 = Assert-TrayClosed $trayB '(b) upgrade' 'session ending|WM_CLOSE'
    Assert (Test-Path $cacheB) '(b) the tray cache survives the upgrade (RemoveFolderEx skips UPGRADINGPRODUCTCODE)'

    # A no-op reinstall of the installed product (delta review N1): CloseApplication's condition
    # (NOT Installed OR REINSTALL OR REMOVE OR UPGRADINGPRODUCTCODE) is false, nothing is replaced, so the
    # restarted tray is left running.
    Invoke-Msiexec '/i' $SameVersionMsi (Join-Path $LogDir 'rst-install-repeat.log') | Out-Null
    Assert-OneProduct $pc2 $v2 '(b) repeated install'
    Start-Sleep -Seconds 2
    Assert (-not $trayB2.HasExited) "[(b) repeated install] the running tray (pid $($trayB2.Id)) is left alone by a no-op reinstall"
    $uninstallMsi = $SameVersionMsi
  }

  Assert-TrayInstalled '(b)'

  # (e) Revit gone, a tray running, then uninstall: everything is removed and the tray is closed.
  Set-RevitStubs @()
  Stop-Trays
  $trayE = Start-Tray @('--startup') '(e)'
  $null = Seed-CacheProbe
  # Restart Manager off, so util:CloseApplication's WM_CLOSE is what closes the tray (the repair in (d)
  # covered the Restart Manager path).
  $logE = Join-Path $LogDir 'rst-uninstall.log'
  Invoke-Msiexec '/x' $uninstallMsi $logE -Extra 'MSIRESTARTMANAGERCONTROL=Disable' | Out-Null
  Show-LogLines $logE @('CloseApplication', 'RST.Tray.exe', 'RemoveFolder')
  Show-Related '(e) after uninstall'
  Assert ((Get-Related $UnifiedCode).Count -eq 0) '(e) no RST product after uninstall'
  foreach ($y in $Years.Keys) {
    foreach ($t in (Get-YearTrees $y).Keys) {
      Assert (-not (Test-Path (Join-Path $AppData $t))) "(e) $t removed"
    }
  }
  Assert-Probe $probe '(e)'

  # AC1: uninstall closed the running tray, removed the Run value, its files and its cache; only user data
  # under %AppData%\RST (settings, log) remains.
  $null = Assert-TrayClosed $trayE '(e) uninstall' 'WM_CLOSE' -NoRestart
  Assert ($null -eq (Get-RunValue)) '(e) HKCU Run value RST removed'
  Assert (-not (Test-Path $TrayDir)) "(e) $TrayDir removed"
  Assert (-not (Test-Path (Split-Path $TrayDir))) "(e) $(Split-Path $TrayDir) removed"
  Assert (-not (Test-Path $TrayCache)) "(e) $TrayCache removed (RemoveFolderEx)"
  $left = @(foreach ($d in (Join-Path $env:LOCALAPPDATA 'Programs'), (Join-Path $env:LOCALAPPDATA 'RST\Tray')) {
              if (Test-Path $d) { Get-ChildItem $d -Recurse -File -Force -ErrorAction SilentlyContinue |
                                  Where-Object { $_.Name -like 'RST.Tray*' -or $_.FullName -like (Join-Path $env:LOCALAPPDATA 'RST\Tray\*') } } })
  $left | ForEach-Object { Write-Host "  left: $($_.FullName)" }
  Assert ($left.Count -eq 0) '(e) no tray file left under %LocalAppData%\Programs or %LocalAppData%\RST\Tray'
  Assert (@(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue).Count -eq 0) '(e) no RST.Tray process running'
  Assert (Test-Path $TraySettings) '(e) the tray settings (user data) survive under %AppData%\RST'
  Assert (@(Get-ChildItem (Join-Path $AppData 'RST\logs') -Filter 'tray_*.log').Count -ge 1) '(e) the tray log (user data) survives under %AppData%\RST\logs'

  # (u) a real MajorUpgrade from this commit built with a lower ProductVersion (review N3), a tray running.
  if ($OlderMsi) {
    $OlderMsi = (Resolve-Path $OlderMsi).Path
    $vOld = Get-MsiProperty $OlderMsi 'ProductVersion'
    $pcOld = Get-MsiProperty $OlderMsi 'ProductCode'
    Write-Host "Older MSI: ProductVersion=$vOld ProductCode=$pcOld"
    Assert ([version]$vOld -lt [version]$newVersion) "(u) the older build's ProductVersion $vOld is below $newVersion"
    Set-RevitStubs @('2025', '2026', '2027')
    Invoke-Msiexec '/i' $OlderMsi (Join-Path $LogDir 'rst-install-older.log') | Out-Null
    Assert-OneProduct $pcOld $vOld '(u) older installed'
    Assert-TrayInstalled '(u) older'
    Stop-Trays
    $trayU = Start-Tray @('--startup') '(u)'
    $cacheU = Seed-CacheProbe
    $logU = Join-Path $LogDir 'rst-upgrade-from-older.log'
    $codeU = Invoke-Msiexec '/i' $uninstallMsi $logU
    Show-LogLines $logU @($pcOld, 'RemoveFolder', 'RESTART MANAGER')
    Assert ($codeU -eq 0) "(u) the MajorUpgrade with a tray running needs no reboot (exit $codeU)"
    $pcNew = Get-MsiProperty $uninstallMsi 'ProductCode'
    Assert-OneProduct $pcNew $newVersion '(u) upgraded'
    Assert ((Select-String -Path $logU -Pattern $pcOld -SimpleMatch -Quiet)) "(u) log names the removed older product $pcOld"
    Assert-Years @('2025', '2026', '2027') '(u) upgraded'
    $null = Assert-TrayClosed $trayU '(u) upgrade' 'session ending|WM_CLOSE'
    Assert-TrayInstalled '(u) upgraded'
    Assert (Test-Path $cacheU) '(u) the tray cache survives the MajorUpgrade'

    # Uninstall the normal way (Settings > Apps: Restart Manager on) with a tray running (delta review B1):
    # Restart Manager closes it, and its restart at the end finds no exe, so nothing is left running.
    Stop-Trays
    Set-RevitStubs @()
    $trayX = Start-Tray @('--startup') '(u) uninstall'
    $logX = Join-Path $LogDir 'rst-uninstall-after-upgrade.log'
    $codeX = Invoke-Msiexec '/x' $uninstallMsi $logX
    Show-LogLines $logX @('RESTART MANAGER', 'RST.Tray.exe')
    Assert ($codeX -eq 0) "(u) uninstall with a tray running and Restart Manager on needs no reboot (exit $codeX)"
    $null = Assert-TrayClosed $trayX '(u) uninstall' 'session ending|WM_CLOSE' -NoRestart
    Assert (-not (Test-Path $TrayExe)) "(u) $TrayExe removed"
    Assert ((Get-Related $UnifiedCode).Count -eq 0) '(u) no RST product after uninstall'
    Assert (-not (Test-Path $TrayCache)) '(u) uninstall removes the tray cache'
    Assert ($null -eq (Get-RunValue)) '(u) HKCU Run value RST removed'
    Assert (-not (Test-Path (Split-Path $TrayDir))) "(u) $(Split-Path $TrayDir) removed"
    Assert-Probe $probe '(u)'
  }
  Show-TrayLog
  exit 0
}

# ---- Migrate
if (-not $From -or -not $OldMsi) { throw '-Mode Migrate needs -From and -OldMsi' }
Set-RevitStubs @('2025', '2026', '2027')
$OldMsi = (Resolve-Path $OldMsi).Path
$oldCode = Get-MsiProperty $OldMsi 'UpgradeCode'
$oldPC = Get-MsiProperty $OldMsi 'ProductCode'
$oldVersion = Get-MsiProperty $OldMsi 'ProductVersion'
Write-Host "Old $([IO.Path]::GetFileName($OldMsi)): UpgradeCode=$oldCode ProductCode=$oldPC ProductVersion=$oldVersion"
$expectCode = if ($From -eq 'Preview') { $PreviewCode } else { $UnifiedCode }
Assert ($oldCode -eq $expectCode) "old MSI carries UpgradeCode $expectCode"

$probe = Seed-Probe
Invoke-Msiexec '/i' $OldMsi (Join-Path $LogDir 'old-install.log') | Out-Null
Show-Related 'before'
$before = Get-Related $oldCode
Assert ($before.Count -eq 1 -and $before[0].ProductCode -eq $oldPC) "old product $oldPC installed"

$engine27 = Join-Path $AppData 'RST\R27\app\RST.Engine.dll'
$stagedHash = (Get-FileHash (Join-Path $StagedR27 'app\RST.Engine.dll') -Algorithm SHA256).Hash
if ($From -eq 'Preview') {
  $oldHash = (Get-FileHash $engine27 -Algorithm SHA256).Hash
  Write-Host "R27 RST.Engine.dll before: $oldHash (preview)"
  Write-Host "R27 RST.Engine.dll staged: $stagedHash (new build)"
  Assert ($oldHash -ne $stagedHash) 'preview R27 engine differs from the new build (hash check below is meaningful)'

  # Component identity: do the preview's components share GUIDs with the
  # unified MSI's R27 components? (Informational: removal runs before
  # install, so the migration does not depend on it.)
  $oldComp = Get-MsiQuery $OldMsi 'SELECT `ComponentId` FROM `Component`'
  $newComp = Get-MsiQuery $Msi 'SELECT `ComponentId` FROM `Component`'
  $newSet = [Collections.Generic.HashSet[string]]::new([string[]]($newComp | ForEach-Object { $_[0] }))
  $shared = @($oldComp | Where-Object { $newSet.Contains($_[0]) }).Count
  Write-Host "Component GUIDs: preview=$($oldComp.Count) unified=$($newComp.Count) shared=$shared"
}

$log = Join-Path $LogDir 'new-install.log'
Invoke-Msiexec '/i' $Msi $log | Out-Null
Show-LogLines $log @($oldPC)

Assert-OneProduct $newProductCode $newVersion 'after'
Assert ((Get-Related $PreviewCode).Count -eq 0) 'no product left on the retired R27 preview UpgradeCode'
Assert ((Select-String -Path $log -Pattern 'RemoveExistingProducts' -SimpleMatch -Quiet)) 'log shows RemoveExistingProducts'
Assert ((Select-String -Path $log -Pattern $oldPC -SimpleMatch -Quiet)) "log names the removed product $oldPC"
Assert-Years @('2025', '2026', '2027') 'after upgrade'
$newHash = (Get-FileHash $engine27 -Algorithm SHA256).Hash
Write-Host "R27 RST.Engine.dll after:  $newHash"
Assert ($newHash -eq $stagedHash) 'installed R27 engine equals the newly staged engine'
Assert-Probe $probe 'after upgrade'
Assert-TrayInstalled 'after upgrade'
exit 0
