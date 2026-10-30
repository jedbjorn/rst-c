<#
  ci-bundle-smoke.ps1 - Burn chaining checks for RST-Setup.exe on a clean CI
  runner (feature #13 Phase 2, spec #15). Called by .github/workflows/_build.yml.
  Helpers adapted from installer/ci-smoke.ps1 (COM queries, Revit stubs).

  RST.msi refuses a first install when no Revit is found (decision #10), so
  stub Revit.exe files in C:\Program Files\Autodesk\Revit <year>\ stand in
  for Revit 2025/2026/2027, exactly as installer/ci-smoke.ps1 does.

  -Mode Chain (AC4)    the test bundle (-Bundle) chains RST.msi and the
                       fixture companion (-FixtureDir holds the 1.0.0, 1.1.0
                       and 2.0.0 fixture MSIs; the bundle embeds 1.1.0).
                       Walks the spec's install-order table:
      A  ticked install            RST + fixture installed, fixture has its
                                   own ARP entry, RST.msi's is hidden,
                                   bundle registered per-user, no elevation
      B  re-run, same command      no change
      C  bundle uninstalled (ARP)  RST gone, fixture stays
      D  unticked                  fixture never installed; installed later
                                   from its own MSI: normal, RST unaffected
      E  InstallX=0, older fixture installed   untouched
      F  ticked, older installed   upgraded in place to 1.1.0
      G  ticked, newer installed   left alone at 2.0.0
      H  ticked, same installed    detected present, skipped
  -Mode BareMsiFirst (AC5)  bare RST.msi (-RstMsi, the same file the bundle
                       embeds) installed first, then the bundle, then the
                       bundle uninstalled. Records what Burn does with an
                       RST.msi it did not install.
  -Mode Companions     the real RST-Setup.exe (-Bundle) with Gantt-It and
                       Splash-It ticked (-Registry, -PayloadDir): both install
                       silently with their own ARP entries and .addin files
                       (C4, C6), survive the bundle's uninstall (D3), uninstall
                       cleanly with msiexec /x, and install alone without RST
                       (C7). Revit is a stub: nothing here loads in Revit (AC6
                       is the VM's).
  -Mode Scope (feature #17, spec #20 §7, AC10)  the test bundle on both
                       install-scope paths. The elevation check is Burn's own:
                       an elevated engine is launched only for a per-machine
                       package ("Launching elevated engine process", logged even
                       on this already-elevated runner).
      S1  default (no InstallScope)   "Just for me": RST.msi per user, no
                                      elevated engine, RST-Machine and the
                                      helper not run; uninstall, no elevation
      S2  InstallScope=machine, an older RST.msi (-OlderRstMsi, 1.0.0)
          installed for this user, the fixture ticked
                                      one elevated engine; the helper removes
                                      the user's own 1.0.0 BEFORE RST-Machine
                                      installs; RST-Machine per machine (the
                                      layout, HKLM Run); the fixture per user;
                                      the bundle stays registered per user
      S3  re-run with no InstallScope, then with InstallScope=user, while
          RST-Machine is installed    stays "machine": no per-user RST is
                                      installed, nothing is switched
      S4  bundle uninstalled (ARP)    RST-Machine removed (one elevation), the
                                      fixture stays (Permanent)
      S5  InstallScope=machine over the SAME-version RST.msi installed bare
                                      the user's copy is gone first, then
                                      RST-Machine; uninstall clean
      S6  InstallScope=machine over the public v2.2.0 RST.msi AND the
          retired RST-R27.msi preview (-V220RstMsi, -V220PreviewMsi)
                                      the helper removes both; exactly "RST
                                      (all users)" and nothing per user left
      S7  InstallScope=machine over a hand-copied per-user RST.addin
                                      installs anyway (RSTFROMSETUP); the copy
                                      stays, as the shadow it is
      S8  InstallScope=machine with Revit running in this session (a renamed
          ping.exe), an older RST.msi installed
                                      the helper stops before removing
                                      anything (exit 32): the user keeps RST,
                                      RST-Machine is not installed

  Product queries go through the WindowsInstaller.Installer COM object via
  InvokeMember (it has no type library PowerShell can bind to).
#>
param(
  [Parameter(Mandatory)][ValidateSet('Chain', 'BareMsiFirst', 'Companions', 'Scope')][string]$Mode,
  [Parameter(Mandatory)][string]$Bundle,
  [string]$RstMsi,
  [string]$OlderRstMsi,
  [string]$V220RstMsi,
  [string]$V220PreviewMsi,
  [string]$FixtureDir,
  [string]$Registry = 'companions/companions.json',
  [string]$PayloadDir = 'installer-bundle/payload'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RstCode = '{AF0331FC-5280-492A-94BD-95EDF108BE74}'
$BundleCode = '{2BE2BE5D-9203-4344-A70D-E735D3B041A3}'
$FixtureCode = '{A0E85F7A-BF17-4052-AB6A-010781D8CF6D}'
$PreviewCode = '{02BE4399-A563-4B15-BD12-ECF5D8486FB3}'   # the retired RST-R27.msi preview (installer/Product.wxs)
$AppData = $env:APPDATA
$LogDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$ProgramFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$Bundle = (Resolve-Path $Bundle).Path
$Years = [ordered]@{ '2025' = 'R25'; '2026' = 'R26'; '2027' = 'R27' }
$FixtureTxt = Join-Path $AppData 'RST-CompanionFixture\fixture.txt'
$Step = 0

$Installer = New-Object -ComObject WindowsInstaller.Installer
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
function Get-MsiProperty([string]$Path, [string]$Name) {
  $db = Invoke-ComMethod $Installer 'OpenDatabase' @($Path, 0)
  $view = Invoke-ComMethod $db 'OpenView' @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'")
  Invoke-ComMethod $view 'Execute' | Out-Null
  $rec = Invoke-ComMethod $view 'Fetch'
  $value = if ($rec) { Get-ComProp $rec 'StringData' @(1) } else { $null }
  Invoke-ComMethod $view 'Close' | Out-Null
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  return $value
}

function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { throw "FAIL: $What" }
  Write-Host "PASS: $What"
}
function Observe([string]$What) { Write-Host "OBSERVED: $What" }

# Add/Remove Programs entries: the Uninstall keys of HKCU and HKLM (both views).
function Get-Arp {
  $roots = @('HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
             'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall',
             'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall')
  $out = @()
  foreach ($root in $roots) {
    if (-not (Test-Path $root)) { continue }
    foreach ($k in Get-ChildItem $root) {
      $p = Get-ItemProperty $k.PSPath
      # Not .Properties.Name: a key with no values makes that an empty
      # member enumeration, which StrictMode rejects.
      $names = @($p.PSObject.Properties | ForEach-Object { $_.Name })
      if (-not ($names -contains 'DisplayName')) { continue }
      $out += [pscustomobject]@{
        Hive              = $root.Substring(0, 4)
        Key               = $k.PSChildName
        DisplayName       = $p.DisplayName
        DisplayVersion    = if ($names -contains 'DisplayVersion') { $p.DisplayVersion } else { '' }
        SystemComponent   = if ($names -contains 'SystemComponent') { [int]$p.SystemComponent } else { 0 }
        BundleUpgradeCode = if ($names -contains 'BundleUpgradeCode') { @($p.BundleUpgradeCode) -join ';' } else { '' }
        QuietUninstall    = if ($names -contains 'QuietUninstallString') { $p.QuietUninstallString } else { '' }
      }
    }
  }
  return , $out
}
# Visible = shown in Settings > Apps (SystemComponent not set).
function Get-ArpVisible([string]$DisplayName) { return , @((Get-Arp) | Where-Object { $_.DisplayName -eq $DisplayName -and $_.SystemComponent -ne 1 }) }
function Get-BundleArp { return , @((Get-Arp) | Where-Object { $_.BundleUpgradeCode -like "*$BundleCode*" }) }

function Show-State([string]$Label, [string[]]$Codes = @($RstCode, $FixtureCode)) {
  Write-Host "---- state [$Label]"
  foreach ($code in $Codes) {
    $r = Get-Related $code
    Write-Host "  RelatedProducts $code -> $($r.Count): $(($r | ForEach-Object { "$($_.ProductCode) '$($_.Name)' $($_.Version)" }) -join '; ')"
  }
  foreach ($e in (Get-Arp) | Where-Object { $_.DisplayName -match '^(RST|Gantt-It|Splash-It)' }) {
    Write-Host "  ARP $($e.Hive) $($e.Key): '$($e.DisplayName)' $($e.DisplayVersion) SystemComponent=$($e.SystemComponent)$(if ($e.BundleUpgradeCode) { " BundleUpgradeCode=$($e.BundleUpgradeCode)" })"
  }
  Write-Host '----'
}

function Set-RevitStubs {
  foreach ($y in $Years.Keys) {
    $dir = Join-Path $ProgramFiles64 "Autodesk\Revit $y"
    New-Item -ItemType Directory -Force $dir | Out-Null
    Set-Content -Path (Join-Path $dir 'Revit.exe') -Value 'rst-c ci stub' -NoNewline
  }
  Write-Host "Revit stubs: $($Years.Keys -join ', ')"
}

function Invoke-Msiexec([string]$Verb, [string]$Target, [string]$Name) {
  $log = Join-Path $LogDir "$Name.log"
  Write-Host "msiexec $Verb $Target /qn (log: $log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Target`" /qn /l*v `"$log`"" -Wait -PassThru
  Write-Host "  exit $($p.ExitCode)"
  if ($p.ExitCode -ne 0) {
    if (Test-Path $log) { Get-Content $log -Tail 60 | Write-Host }
    throw "msiexec $Verb $Target exited $($p.ExitCode) (C4 needs exactly 0: no failure, no reboot)"
  }
}

# Runs the bundle; returns the Burn log path. Exit must be 0 (3010 = reboot).
function Invoke-Bundle([string]$Exe, [string[]]$BundleArgs, [string]$Name, [switch]$ExpectFail) {
  $script:Step++
  $log = Join-Path $LogDir ("bundle-{0:d2}-$Name.log" -f $script:Step)
  $argList = @($BundleArgs) + @('/log', "`"$log`"")
  Write-Host "$([IO.Path]::GetFileName($Exe)) $($argList -join ' ')"
  $p = Start-Process $Exe -ArgumentList $argList -Wait -PassThru
  Write-Host "  exit $($p.ExitCode)"
  $script:LastBundleExit = $p.ExitCode
  Show-BurnLog $log
  if ($ExpectFail) {
    if ($p.ExitCode -eq 0) { throw "bundle $($BundleArgs -join ' ') exited 0; a failure was expected" }
    return $log
  }
  if ($p.ExitCode -ne 0) {
    if (Test-Path $log) { Get-Content $log -Tail 80 | Write-Host }
    throw "bundle $($BundleArgs -join ' ') exited $($p.ExitCode)"
  }
  return $log
}
function Show-BurnLog([string]$Log) {
  if (-not (Test-Path $Log)) { Write-Host "  (no log at $Log)"; return }
  Write-Host "---- $([IO.Path]::GetFileName($Log)): detect / plan / registration lines"
  Select-String -Path $Log -Pattern 'Session begin', 'Detected package', 'Detected related package', 'Detected related bundle',
    'Detected forward compatible', 'Planned package', 'Planned related bundle', 'Condition ''', 'elevated engine',
    'Applied execute package', 'Apply complete', 'Plan complete', 'Ignoring attempt' -SimpleMatch |
    ForEach-Object { Write-Host "  $($_.Line)" }
  Write-Host '----'
}
function Assert-PerUser([string]$Log, [string]$Label) {
  Assert (Select-String -Path $Log -Pattern 'scope: PerUser' -SimpleMatch -Quiet) "[$Label] Burn registered the bundle per-user (log: 'scope: PerUser')"
  Assert (-not (Select-String -Path $Log -Pattern 'Launching elevated engine process' -SimpleMatch -Quiet)) "[$Label] no elevated engine was launched"
}

# Uninstalls the bundle the way a user does: its Add/Remove Programs entry's
# QuietUninstallString (the cached RST-Setup.exe /uninstall /quiet), plus
# /norestart as documented for silent use.
function Uninstall-BundleViaArp([string]$Name) {
  $e = Get-BundleArp
  Assert ($e.Count -eq 1) "[$Name] exactly one RST-Setup bundle ARP entry before uninstall"
  if ($e[0].QuietUninstall -notmatch '^"([^"]+)"\s*(.*)$') { throw "unexpected QuietUninstallString: $($e[0].QuietUninstall)" }
  $log = Invoke-Bundle $Matches[1] (@($Matches[2] -split '\s+' | Where-Object { $_ }) + '/norestart') $Name
  Assert ((Get-BundleArp).Count -eq 0) "[$Name] bundle ARP entry removed"
  return $log
}

function Get-YearTrees([string]$Year) {
  $nn = $Years[$Year]
  [ordered]@{
    "Autodesk\Revit\Addins\$Year" = @('RST.addin', 'RST.Bootstrap.dll')
    "RST\$nn\app"                 = @('RST.Engine.dll', 'RST.UI.dll', 'RST.Core.dll', 'RST.Engine.deps.json')
  }
}
function Assert-Rst([bool]$Present, [string]$Label) {
  $r = Get-Related $RstCode
  if ($Present) {
    Assert ($r.Count -eq 1) "[$Label] exactly one RST product"
    foreach ($y in $Years.Keys) {
      $t = Get-YearTrees $y
      foreach ($d in $t.Keys) { foreach ($f in $t[$d]) { Assert (Test-Path (Join-Path $AppData "$d\$f") -PathType Leaf) "[$Label] $d\$f present" } }
    }
  } else {
    Assert ($r.Count -eq 0) "[$Label] no RST product"
    foreach ($y in $Years.Keys) {
      Assert (-not (Test-Path (Join-Path $AppData "Autodesk\Revit\Addins\$y\RST.addin"))) "[$Label] Addins\$y\RST.addin gone"
      Assert (-not (Test-Path (Join-Path $AppData "RST\$($Years[$y])\app"))) "[$Label] RST\$($Years[$y])\app gone"
    }
  }
}

# ---------------------------------------------------------------- Chain (AC4)
function Get-FixtureMsi([string]$Version) {
  $p = Join-Path $FixtureDir "RST-CompanionFixture-$Version.msi"
  if (-not (Test-Path $p)) { throw "fixture MSI missing: $p" }
  return (Resolve-Path $p).Path
}
function Assert-Fixture([string]$Version, [string]$Label, [string]$ProductCode) {
  $r = Get-Related $FixtureCode
  if ($Version) {
    Assert ($r.Count -eq 1 -and $r[0].Version -eq $Version) "[$Label] exactly one fixture product, at $Version"
    if ($ProductCode) { Assert ($r[0].ProductCode -eq $ProductCode) "[$Label] fixture product is $ProductCode" }
    Assert ((Test-Path $FixtureTxt) -and ((Get-Content $FixtureTxt -Raw).Trim() -eq "RST Companion Fixture $Version")) "[$Label] fixture.txt says $Version"
    Assert ((Get-ArpVisible 'RST Companion Fixture').Count -eq 1) "[$Label] fixture has its own visible Add/Remove Programs entry"
  } else {
    Assert ($r.Count -eq 0) "[$Label] no fixture product"
    Assert (-not (Test-Path $FixtureTxt)) "[$Label] no fixture.txt"
  }
}
function Remove-Fixture([string]$Label) {
  $rel = Get-Related $FixtureCode   # assign first: foreach over the call would see one empty array
  foreach ($p in $rel) { Invoke-Msiexec '/x' $p.ProductCode "fixture-remove-$Label" }
  Assert-Fixture $null "$Label cleanup"
}

if ($Mode -eq 'Chain') {
  Set-RevitStubs
  $f100 = Get-FixtureMsi '1.0.0'; $f110 = Get-FixtureMsi '1.1.0'; $f200 = Get-FixtureMsi '2.0.0'
  $pc110 = Get-MsiProperty $f110 'ProductCode'
  Show-State 'start'
  Assert-Rst $false 'start'; Assert-Fixture $null 'start'
  Assert ((Get-BundleArp).Count -eq 0) '[start] no bundle registered'

  # A - ticked install.
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=1') 'A-ticked'
  Show-State 'A'
  Assert-PerUser $log 'A'
  Assert-Rst $true 'A'
  Assert-Fixture '1.1.0' 'A' $pc110
  $b = Get-BundleArp
  Assert ($b.Count -eq 1 -and $b[0].DisplayName -eq 'RST' -and $b[0].Hive -eq 'HKCU' -and $b[0].SystemComponent -ne 1) '[A] one visible per-user "RST" bundle entry in Add/Remove Programs'
  Assert ((Get-ArpVisible 'RST').Count -eq 1) '[A] Settings > Apps shows one "RST" (RST.msi hidden behind the bundle)'

  # B - re-run, same command: nothing changes.
  $before = (Get-Item $FixtureTxt).LastWriteTimeUtc
  $rstBefore = (Get-Related $RstCode)[0].ProductCode
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=1') 'B-rerun'
  Show-State 'B'
  Assert-Rst $true 'B'
  Assert ((Get-Related $RstCode)[0].ProductCode -eq $rstBefore) '[B] same RST product'
  Assert-Fixture '1.1.0' 'B' $pc110
  Assert ((Get-Item $FixtureTxt).LastWriteTimeUtc -eq $before) '[B] fixture.txt not rewritten'
  Assert ((Get-BundleArp).Count -eq 1) '[B] still one bundle entry'

  # C - uninstall the bundle: RST goes, the fixture stays (Permanent).
  Uninstall-BundleViaArp 'C-uninstall' | Out-Null
  Show-State 'C'
  Assert-Rst $false 'C'
  Assert-Fixture '1.1.0' 'C' $pc110
  Remove-Fixture 'C'

  # D - unticked (default): fixture never installed. Then installed from its
  # own MSI: a normal install, RST unaffected. Bundle uninstall leaves it.
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart') 'D-unticked'
  Show-State 'D'
  Assert-Rst $true 'D'
  Assert-Fixture $null 'D unticked'
  Invoke-Msiexec '/i' $f110 'fixture-D-standalone'
  Assert-Fixture '1.1.0' 'D standalone later' $pc110
  Assert-Rst $true 'D after standalone fixture'
  Uninstall-BundleViaArp 'D-uninstall' | Out-Null
  Assert-Rst $false 'D'
  Assert-Fixture '1.1.0' 'D after bundle uninstall' $pc110
  Remove-Fixture 'D'

  # E - explicitly unticked from the command line, an older fixture
  # installed: untouched. ("=0" arrives as a string: a bare-variable
  # InstallCondition would wrongly install here.)
  Invoke-Msiexec '/i' $f100 'fixture-E-older'
  $pc100 = (Get-Related $FixtureCode)[0].ProductCode
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=0') 'E-explicit-0'
  Show-State 'E'
  Assert-Rst $true 'E'
  Assert-Fixture '1.0.0' 'E untouched' $pc100

  # F - ticked, older installed: upgraded in place (one product, 1.1.0).
  # The bundle is installed (E), so this is an install over an installed
  # bundle with the box now ticked.
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=1') 'F-upgrade'
  Show-State 'F'
  Assert-Rst $true 'F'
  Assert-Fixture '1.1.0' 'F upgraded in place' $pc110
  Uninstall-BundleViaArp 'F-uninstall' | Out-Null
  Assert-Rst $false 'F'
  Assert-Fixture '1.1.0' 'F after bundle uninstall' $pc110
  Remove-Fixture 'F'

  # G - ticked, newer installed: left alone, the bundle still succeeds.
  Invoke-Msiexec '/i' $f200 'fixture-G-newer'
  $pc200 = (Get-Related $FixtureCode)[0].ProductCode
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=1') 'G-newer'
  Show-State 'G'
  Assert-Rst $true 'G'
  Assert-Fixture '2.0.0' 'G newer left alone' $pc200
  Assert (Select-String -Path $log -Pattern 'Detected related package' -SimpleMatch -Quiet) '[G] Burn detected the newer fixture as a related package'
  Uninstall-BundleViaArp 'G-uninstall' | Out-Null
  Assert-Rst $false 'G'
  Assert-Fixture '2.0.0' 'G after bundle uninstall' $pc200
  Remove-Fixture 'G'

  # H - ticked, the same version already installed from its own MSI:
  # detected present and skipped.
  Invoke-Msiexec '/i' $f110 'fixture-H-same'
  $before = (Get-Item $FixtureTxt).LastWriteTimeUtc
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=1') 'H-same'
  Show-State 'H'
  Assert (Select-String -Path $log -Pattern 'Detected package: CompanionFixture, state: Present' -SimpleMatch -Quiet) '[H] Burn detected the fixture as present'
  Assert-Fixture '1.1.0' 'H skipped' $pc110
  Assert ((Get-Item $FixtureTxt).LastWriteTimeUtc -eq $before) '[H] fixture.txt not rewritten'
  Uninstall-BundleViaArp 'H-uninstall' | Out-Null
  Assert-Rst $false 'H'
  Assert-Fixture '1.1.0' 'H after bundle uninstall' $pc110
  Remove-Fixture 'H'

  Show-State 'end'
  exit 0
}

# --------------------------------------------------------- BareMsiFirst (AC5)
if ($Mode -eq 'BareMsiFirst') {
  if (-not $RstMsi) { throw '-Mode BareMsiFirst needs -RstMsi' }
  $RstMsi = (Resolve-Path $RstMsi).Path
  $pcBare = Get-MsiProperty $RstMsi 'ProductCode'
  Set-RevitStubs
  Assert-Rst $false 'start'

  Invoke-Msiexec '/i' $RstMsi 'bare-rst'
  Show-State 'bare RST.msi'
  Assert-Rst $true 'bare'
  Assert ((Get-Related $RstCode)[0].ProductCode -eq $pcBare) "[bare] RST product is $pcBare"
  Assert ((Get-ArpVisible 'RST').Count -eq 1) '[bare] one visible "RST" entry (the MSI)'

  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallCompanionFixture=1') 'bundle-over-bare'
  Show-State 'bundle over bare'
  Assert-Rst $true 'bundle over bare'
  Assert ((Get-Related $RstCode)[0].ProductCode -eq $pcBare) '[bundle over bare] one RST product, the same one (detected, not reinstalled)'
  Assert-Fixture '1.1.0' 'bundle over bare'
  Observe "Burn detect: $((Select-String -Path $log -Pattern 'Detected package: RST,' -SimpleMatch | Select-Object -First 1).Line)"
  Observe "Burn plan:   $((Select-String -Path $log -Pattern 'Planned package: RST,' -SimpleMatch | Select-Object -First 1).Line)"
  $visible = Get-ArpVisible 'RST'
  Observe "visible 'RST' entries after the bundle: $($visible.Count) ($(($visible | ForEach-Object { "$($_.Hive)\$($_.Key)" }) -join ', '))"
  # The behavior first observed in PR #134 (installer-bundle/README.md, AC5),
  # pinned so a Burn or packaging change that alters it fails here:
  # Burn adopts the RST.msi it did not install (detected "install
  # registration state: Ignored", planned to "Present": the bundle takes
  # ownership), leaves its visible ARP entry as it was, and so Settings >
  # Apps shows two "RST" entries.
  Assert (Select-String -Path $log -Pattern 'Detected package: RST, state: Present, cached: No, install registration state: Ignored' -SimpleMatch -Quiet) '[AC5] Burn detects the bare RST.msi as present and not its own (registration Ignored)'
  Assert (Select-String -Path $log -Pattern 'Planned package: RST, state: Present, default requested: Present, ba requested: Present, execute: None' -SimpleMatch -Quiet) '[AC5] Burn does not reinstall it (execute: None)'
  Assert (Select-String -Path $log -Pattern 'Planned package: RST,.*expected install registration state: Present' -Quiet) '[AC5] Burn adopts it (expected install registration state: Present)'
  Assert ($visible.Count -eq 2) '[AC5] Settings > Apps shows two "RST" entries: the bundle and the bare MSI'

  $log = Uninstall-BundleViaArp 'uninstall-bundle'
  Show-State 'bundle uninstalled'
  Observe "Burn plan on uninstall: $((Select-String -Path $log -Pattern 'Planned package: RST,' -SimpleMatch | Select-Object -First 1).Line)"
  $left = Get-Related $RstCode
  Observe "RST products after the bundle's uninstall: $($left.Count)"
  Observe "RST files after the bundle's uninstall: $(if (Test-Path (Join-Path $AppData 'RST\R26\app\RST.Engine.dll')) { 'present' } else { 'gone' })"
  Observe "visible 'RST' entries after the bundle's uninstall: $((Get-ArpVisible 'RST').Count)"
  # Pinned (AC5): uninstalling the bundle removes the adopted RST.msi too.
  Assert (Select-String -Path $log -Pattern 'Planned package: RST, state: Present, default requested: Absent, ba requested: Absent, execute: Uninstall' -SimpleMatch -Quiet) '[AC5] the bundle uninstall plans RST.msi for removal'
  Assert-Rst $false '[AC5] after the bundle uninstall: the bare-installed RST is removed'
  Assert ((Get-ArpVisible 'RST').Count -eq 0) '[AC5] no "RST" entry left in Settings > Apps'
  Assert-Fixture '1.1.0' 'after bundle uninstall'

  foreach ($p in $left) { Invoke-Msiexec '/x' $p.ProductCode 'bare-rst-remove' }
  Assert-Rst $false 'cleanup'
  Remove-Fixture 'cleanup'

  # The other way out: bare RST.msi, then the bundle, then the user removes
  # the MSI's own "RST" entry. Recorded (OBSERVED), not asserted.
  Invoke-Msiexec '/i' $RstMsi 'bare-rst-2'
  Invoke-Bundle $Bundle @('/quiet', '/norestart') 'bundle-over-bare-2' | Out-Null
  Invoke-Msiexec '/x' $pcBare 'bare-rst-2-remove-msi'
  Show-State 'MSI entry removed under the bundle'
  Observe "after msiexec /x of the adopted RST.msi: RST products $((Get-Related $RstCode).Count), bundle entries $((Get-BundleArp).Count), visible 'RST' entries $((Get-ArpVisible 'RST').Count)"
  if ((Get-BundleArp).Count -gt 0) {
    $log = Uninstall-BundleViaArp 'uninstall-orphan-bundle'
    Observe "orphaned bundle uninstall plan: $((Select-String -Path $log -Pattern 'Planned package: RST,' -SimpleMatch | Select-Object -First 1).Line)"
    Observe "after the orphaned bundle's uninstall: RST products $((Get-Related $RstCode).Count), bundle entries $((Get-BundleArp).Count)"
  }
  $rel = Get-Related $RstCode
  foreach ($p in $rel) { Invoke-Msiexec '/x' $p.ProductCode 'bare-rst-2-cleanup' }
  Assert-Rst $false 'final cleanup'
  exit 0
}

# ---------------------------------------------------------- Scope (AC10, F17 WU5)
if ($Mode -eq 'Scope') {
  if (-not $RstMsi -or -not $OlderRstMsi -or -not $FixtureDir -or -not $V220RstMsi -or -not $V220PreviewMsi) { throw '-Mode Scope needs -RstMsi, -OlderRstMsi, -FixtureDir, -V220RstMsi and -V220PreviewMsi' }
  $RstMsi = (Resolve-Path $RstMsi).Path
  $OlderRstMsi = (Resolve-Path $OlderRstMsi).Path
  $V220RstMsi = (Resolve-Path $V220RstMsi).Path
  $V220PreviewMsi = (Resolve-Path $V220PreviewMsi).Path
  . (Join-Path $PSScriptRoot '..\installer\ci-machine-layout.ps1')
  $codes = @($RstCode, $MachineUpgradeCode, $FixtureCode)

  function Get-Elevations([string]$Log) { return @(Select-String -Path $Log -Pattern 'Launching elevated engine process' -SimpleMatch).Count }
  function Get-PlanLine([string]$Log, [string]$Id) {
    $l = Select-String -Path $Log -Pattern "Planned package: $Id," -SimpleMatch | Select-Object -First 1
    if ($l) { return $l.Line } else { return '' }
  }
  function Assert-Plan([string]$Log, [string]$Id, [string]$Execute, [string]$Label) {
    $l = Get-PlanLine $Log $Id
    Assert ($l -match "execute: $Execute,") "[$Label] Burn plans ${Id}: execute $Execute ($($l.Trim()))"
  }
  function Get-AppliedIndex([string]$Log, [string]$Id) {
    $l = Select-String -Path $Log -Pattern "Applied execute package: $Id," -SimpleMatch | Select-Object -First 1
    if ($l) { return $l.LineNumber } else { return -1 }
  }
  # The helper's own log: Burn's [WixBundleLog_RemoveUserRst], beside the bundle log.
  function Get-HelperLog([string]$Log) {
    $base = [IO.Path]::GetFileNameWithoutExtension($Log)
    $f = @(Get-ChildItem (Split-Path $Log) -Filter "${base}_*RemoveUserRst*.log" -ErrorAction SilentlyContinue |
           Where-Object { $_.Name -notmatch '_\{?[0-9A-F]{8}-' } | Sort-Object Name)
    if ($f.Count -eq 0) {
      Write-Host "no helper log for $base; logs there: $((Get-ChildItem (Split-Path $Log) -Filter "$base*" | ForEach-Object { $_.Name }) -join ', ')"
      return $null
    }
    Write-Host "---- helper log $($f[0].Name)"; Get-Content $f[0].FullName | ForEach-Object { Write-Host "  $_" }; Write-Host '----'
    return $f[0].FullName
  }
  function Assert-MachineProduct([bool]$Present, [string]$Label) {
    $r = Get-Related $MachineUpgradeCode
    if ($Present) {
      Assert ($r.Count -eq 1) "[$Label] exactly one RST-Machine product"
      Assert ([string](Get-ComProp $Installer 'ProductInfo' @($r[0].ProductCode, 'AssignmentType')) -eq '1') "[$Label] installed per machine"
      Assert ((Get-ArpVisible 'RST (all users)').Count -eq 1 -and (Get-ArpVisible 'RST (all users)')[0].Hive -eq 'HKLM') "[$Label] its own visible 'RST (all users)' entry, for every user (HKLM)"
      Assert-MachineYears @('2025', '2026', '2027') $Label
      Assert-MachineTray $true $Label
    } else {
      Assert ($r.Count -eq 0) "[$Label] no RST-Machine product"
      Assert-MachineGone $Label
    }
  }
  function Show-Scope([string]$Log) {
    Select-String -Path $Log -Pattern 'Variable: InstallScope', 'Variable: RstMachineState', 'Setting string variable ''InstallScope''', 'Setting numeric variable ''RstMachineState''' -SimpleMatch |
      ForEach-Object { Write-Host "  $($_.Line)" }
  }

  Set-RevitStubs
  Show-State 'start' $codes
  Assert-Rst $false 'start'
  Assert-MachineProduct $false 'start'

  # S1 - the default is "Just for me": never elevates.
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart') 'S1-default'
  Show-State 'S1' $codes; Show-Scope $log
  Assert-PerUser $log 'S1'
  Assert ((Get-Elevations $log) -eq 0) '[S1] the per-user path launched no elevated engine'
  Assert-Rst $true 'S1'
  Assert-MachineProduct $false 'S1'
  Assert-Plan $log 'RST' 'Install' 'S1'
  Assert-Plan $log 'RemoveUserRst' 'None' 'S1'
  Assert-Plan $log 'RSTMachine' 'None' 'S1'
  $log = Uninstall-BundleViaArp 'S1-uninstall'
  Assert ((Get-Elevations $log) -eq 0) '[S1] its uninstall launched no elevated engine either'
  Assert-Rst $false 'S1 uninstalled'

  # S2 - "Everyone on this PC" over an older RST.msi installed for this user, with the fixture companion.
  Invoke-Msiexec '/i' $OlderRstMsi 'S2-older-user-rst'
  $older = Get-Related $RstCode
  Assert ($older.Count -eq 1 -and $older[0].Version -eq '1.0.0') "[S2] this user has RST.msi 1.0.0 ($($older | ForEach-Object { "$($_.ProductCode) $($_.Version)" }))"
  Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallScope=machine', 'InstallCompanionFixture=1') 'S2-machine'
  Show-State 'S2' $codes; Show-Scope $log
  Assert ((Get-Elevations $log) -eq 1) "[S2] the per-machine path launched exactly one elevated engine (one UAC prompt; got $(Get-Elevations $log))"
  Assert (Select-String -Path $log -Pattern 'scope: PerUser' -SimpleMatch -Quiet) '[S2] the bundle itself stays registered per user'
  Assert-Plan $log 'RemoveUserRst' 'Install' 'S2'
  Assert-Plan $log 'RSTMachine' 'Install' 'S2'
  Observe "S2 RST plan: $((Get-PlanLine $log 'RST').Trim())"
  $helper = Get-HelperLog $log
  Assert ($null -ne $helper) '[S2] the helper wrote its log beside the bundle log'
  Assert (Select-String -Path $helper -Pattern "$($older[0].ProductCode): RST 1.0.0 is installed for this user; cached package" -SimpleMatch -Quiet) '[S2] the helper found and removed the user''s own RST 1.0.0'
  Assert (Select-String -Path $helper -Pattern "$($older[0].ProductCode): Windows Installer returned 0" -SimpleMatch -Quiet) '[S2] that removal returned 0'
  $iHelper = Get-AppliedIndex $log 'RemoveUserRst'; $iMachine = Get-AppliedIndex $log 'RSTMachine'
  Assert ($iHelper -gt 0 -and $iMachine -gt $iHelper) "[S2] the user's copy was removed before RST-Machine installed (log lines $iHelper < $iMachine)"
  Assert-Rst $false 'S2 user copy removed'
  Assert-MachineProduct $true 'S2'
  Assert-Fixture '1.1.0' 'S2 companion for this user'
  $fx = Get-Related $FixtureCode
  Assert ([string](Get-ComProp $Installer 'ProductInfo' @($fx[0].ProductCode, 'AssignmentType')) -eq '0') '[S2] the companion is installed per user (contract C1)'
  $b = Get-BundleArp
  Assert ($b.Count -eq 1 -and $b[0].Hive -eq 'HKCU') '[S2] one per-user RST-Setup bundle entry'

  # S3 - re-runs while RST-Machine is installed stay "machine": nothing installed per user, nothing switched.
  foreach ($run in @(@{ Name = 'S3-rerun-default'; Args = @('/quiet', '/norestart') }, @{ Name = 'S3-rerun-user'; Args = @('/quiet', '/norestart', 'InstallScope=user') })) {
    $log = Invoke-Bundle $Bundle $run.Args $run.Name
    Show-Scope $log
    Assert-Plan $log 'RST' 'None' $run.Name
    Assert-Plan $log 'RSTMachine' 'None' $run.Name
    Assert-Rst $false $run.Name
    Assert-MachineProduct $true $run.Name
  }

  # S4 - uninstall the bundle: RST-Machine goes (one elevation), the companion stays.
  $log = Uninstall-BundleViaArp 'S4-uninstall'
  Show-State 'S4' $codes
  Assert ((Get-Elevations $log) -eq 1) '[S4] the uninstall elevated once, for RST-Machine'
  Assert-MachineProduct $false 'S4'
  Assert-Rst $false 'S4'
  Assert-Fixture '1.1.0' 'S4 companion stays'
  Remove-Fixture 'S4'

  # S5 - "Everyone on this PC" over the same-version RST.msi installed bare.
  Invoke-Msiexec '/i' $RstMsi 'S5-same-user-rst'
  Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallScope=Machine') 'S5-machine-same'
  Show-State 'S5' $codes; Show-Scope $log
  Observe "S5 RST plan: $((Get-PlanLine $log 'RST').Trim())"
  $null = Get-HelperLog $log
  Assert ((Get-Elevations $log) -eq 1) '[S5] one elevated engine'
  Assert-Rst $false 'S5 user copy removed'
  Assert-MachineProduct $true 'S5 (InstallScope=Machine: any case)'
  $iMachine = Get-AppliedIndex $log 'RSTMachine'
  $iUser = @((Get-AppliedIndex $log 'RST'), (Get-AppliedIndex $log 'RemoveUserRst')) | Where-Object { $_ -gt 0 } | Sort-Object | Select-Object -First 1
  Assert ($iUser -gt 0 -and $iMachine -gt $iUser) "[S5] the user's copy was removed before RST-Machine installed (log lines $iUser < $iMachine)"
  Uninstall-BundleViaArp 'S5-uninstall' | Out-Null
  Assert-MachineProduct $false 'S5 uninstalled'

  # S6 (review B2) - a v2.2.0 user: the public RST.msi 2.2.0 and the retired RST-R27.msi preview, both installed
  # for this user. "Everyone on this PC" must end with exactly "RST (all users)" and nothing per user.
  Invoke-Msiexec '/i' $V220RstMsi 'S6-v220-rst'
  Invoke-Msiexec '/i' $V220PreviewMsi 'S6-v220-preview'
  Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $userRst = Get-Related $RstCode; $userPreview = Get-Related $PreviewCode
  Assert ($userRst.Count -eq 1 -and $userPreview.Count -eq 1) "[S6] this user has RST.msi $($userRst | ForEach-Object Version) and the 2027 preview $($userPreview | ForEach-Object Version)"
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallScope=machine') 'S6-machine-over-v220'
  Show-State 'S6' (@($codes) + $PreviewCode)
  $helper = Get-HelperLog $log
  Assert ($null -ne $helper) '[S6] the helper wrote its log'
  Assert (Select-String -Path $helper -Pattern "$($userPreview[0].ProductCode): Windows Installer returned 0" -SimpleMatch -Quiet) '[S6] the helper removed the 2027 preview'
  Assert (Select-String -Path $helper -Pattern "$($userRst[0].ProductCode): Windows Installer returned 0" -SimpleMatch -Quiet) '[S6] the helper removed RST.msi 2.2.0'
  Assert ((Get-Related $PreviewCode).Count -eq 0) '[S6] no 2027 preview product left'
  Assert-Rst $false 'S6 no per-user RST left'
  Assert-MachineProduct $true 'S6'
  $visible = @((Get-Arp) | Where-Object { $_.DisplayName -match '^RST' -and $_.SystemComponent -ne 1 -and -not $_.BundleUpgradeCode })
  $visible | ForEach-Object { Write-Host "  visible: $($_.Hive) '$($_.DisplayName)' $($_.DisplayVersion)" }
  Assert ($visible.Count -eq 1 -and $visible[0].DisplayName -eq 'RST (all users)') '[S6] the only RST product entry left is "RST (all users)"'
  Uninstall-BundleViaArp 'S6-uninstall' | Out-Null
  Assert-MachineProduct $false 'S6 uninstalled'

  # S7 (review B2) - a hand-copied per-user RST.addin (no product, so nothing can remove it). Through setup,
  # RST-Machine.msi installs anyway (RSTFROMSETUP): the leftover is the shadow the engine logs and the tray reports.
  # A bare RST-Machine.msi still refuses it (installer/ci-machine-smoke.ps1 (p)).
  $handCopy = Join-Path $AppData 'Autodesk\Revit\Addins\2026\RST.addin'
  New-Item -ItemType Directory -Force (Split-Path $handCopy) | Out-Null
  Set-Content -Path $handCopy -Value '<RevitAddIns><!-- rst-c ci: a hand-copied RST manifest --></RevitAddIns>'
  $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallScope=machine') 'S7-machine-over-hand-copy'
  $helper = Get-HelperLog $log
  Assert (Select-String -Path $helper -Pattern 'nothing to remove' -SimpleMatch -Quiet) '[S7] the helper had no installed product to remove'
  Assert-MachineProduct $true 'S7 installed despite the hand copy'
  Assert (Test-Path $handCopy) '[S7] the hand copy is left alone (reported as installed twice, never deleted)'
  Remove-Item $handCopy -Force
  Uninstall-BundleViaArp 'S7-uninstall' | Out-Null
  Assert-MachineProduct $false 'S7 uninstalled'

  # S8 (review N2, B2) - Revit running in this session: the helper stops before it removes anything, so the user
  # keeps their RST and RST-Machine is not installed. A copy of ping.exe named Revit.exe stands in for Revit.
  Invoke-Msiexec '/i' $OlderRstMsi 'S8-older-user-rst'
  Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  $fakeDir = New-Item -ItemType Directory -Force (Join-Path $LogDir 'fake-revit')
  $fakeRevit = Join-Path $fakeDir 'Revit.exe'
  Copy-Item (Join-Path $env:SystemRoot 'System32\PING.EXE') $fakeRevit -Force
  $revit = Start-Process $fakeRevit -ArgumentList '-n', '600', '127.0.0.1' -WindowStyle Hidden -PassThru
  try {
    Start-Sleep -Seconds 1
    Assert (@(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue).Count -ge 1) '[S8] a process named Revit runs in this session'
    $log = Invoke-Bundle $Bundle @('/quiet', '/norestart', 'InstallScope=machine') 'S8-machine-revit-running' -ExpectFail
    Observe "S8 bundle exit code: $script:LastBundleExit"
    $helper = Get-HelperLog $log
    Assert ($null -ne $helper) '[S8] the helper wrote its log'
    Assert (Select-String -Path $helper -Pattern 'Revit is running in this session' -SimpleMatch -Quiet) '[S8] the helper saw Revit running'
    Assert (Select-String -Path $helper -Pattern 'stopped before removing anything' -SimpleMatch -Quiet) '[S8] it stopped before removing anything'
    Assert (Select-String -Path $log -Pattern 'The process for package: RemoveUserRst exited with code: 0x20' -SimpleMatch -Quiet) '[S8] Burn records the helper''s exit 32 (Revit is running)'
    Assert ((Get-AppliedIndex $log 'RSTMachine') -lt 0) '[S8] RST-Machine was never applied'
  } finally {
    $revit | Stop-Process -Force -ErrorAction SilentlyContinue
  }
  $kept = Get-Related $RstCode
  Assert ($kept.Count -eq 1 -and $kept[0].Version -eq '1.0.0') '[S8] the user still has their RST 1.0.0'
  Assert (Test-Path (Join-Path $AppData 'Autodesk\Revit\Addins\2026\RST.addin')) '[S8] and its files'
  Assert ((Get-Related $MachineUpgradeCode).Count -eq 0) '[S8] RST-Machine is not installed'
  foreach ($p in $kept) { Invoke-Msiexec '/x' $p.ProductCode 'S8-cleanup' }
  Assert ((Get-BundleArp).Count -eq 0) '[S8] no bundle left registered'
  Assert-Rst $false 'end'
  Show-State 'end' $codes
  exit 0
}

# ------------------------------------------------------------------ Companions
$reg = Get-Content -Raw -Encoding UTF8 -Path $Registry | ConvertFrom-Json
$companions = @($reg.companions | Where-Object { $_.status -eq 'shippable' })
Assert ($companions.Count -gt 0) "registry has shippable companions ($($companions.Count))"
function PascalId([string]$id) { -join ($id -split '-' | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) }
$codes = @($RstCode) + @($companions | ForEach-Object { "{$($_.release.upgradeCode)}" })

# The companion's .addin files: any Addins\<year>\*.addin carrying its ClientId.
function Get-CompanionAddins($c) {
  $found = @()
  foreach ($y in $Years.Keys) {
    $dir = Join-Path $AppData "Autodesk\Revit\Addins\$y"
    if (-not (Test-Path $dir)) { continue }
    foreach ($a in Get-ChildItem $dir -Filter *.addin -File) {
      $x = Get-Content $a.FullName -Raw
      if ($x -match [regex]::Escape($c.addinClientId)) { $found += [pscustomobject]@{ Year = $y; Path = $a.FullName; Text = $x } }
    }
  }
  return , $found
}
function Assert-Companion($c, [bool]$Present, [string]$Label) {
  $r = Get-Related "{$($c.release.upgradeCode)}"
  $addins = Get-CompanionAddins $c
  if ($Present) {
    Assert ($r.Count -eq 1 -and $r[0].Version -eq $c.release.version) "[$Label] $($c.name) installed at $($c.release.version)"
    Assert ((Get-ArpVisible $c.name).Count -eq 1) "[$Label] $($c.name) has its own visible Add/Remove Programs entry"
    foreach ($y in @($c.revitYears)) {
      $a = @($addins | Where-Object { $_.Year -eq [string]$y })
      Assert ($a.Count -eq 1) "[$Label] $($c.name): one .addin with ClientId $($c.addinClientId) in Addins\$y ($(($a | ForEach-Object { Split-Path $_.Path -Leaf }) -join ', '))"
      Assert ($a[0].Text -match "<VendorId>\s*$($c.vendorId)\s*</VendorId>") "[$Label] $($c.name): Addins\$y .addin VendorId $($c.vendorId)"
    }
    $rstDir = Join-Path $AppData 'RST'
    $stray = @(if (Test-Path $rstDir) { Get-ChildItem $rstDir -Recurse -File | Where-Object { $_.FullName -like "*$(PascalId $c.id)*" } })
    Assert ($stray.Count -eq 0) "[$Label] $($c.name) wrote nothing under %AppData%\RST (C6)"
  } else {
    Assert ($r.Count -eq 0) "[$Label] $($c.name) not installed"
    Assert ($addins.Count -eq 0) "[$Label] no $($c.name) .addin left"
  }
}

Set-RevitStubs
Show-State 'start' $codes
Assert-Rst $false 'start'
foreach ($c in $companions) { Assert-Companion $c $false 'start' }

# Both ticked, silently, exactly as documented for IT.
$vars = @($companions | ForEach-Object { "Install$(PascalId $_.id)=1" })
$log = Invoke-Bundle $Bundle (@('/quiet', '/norestart') + $vars) 'companions-ticked'
Show-State 'ticked' $codes
Assert-PerUser $log 'ticked'
Assert-Rst $true 'ticked'
foreach ($c in $companions) { Assert-Companion $c $true 'ticked' }

# Uninstall the bundle: RST goes, both companions stay (D3).
Uninstall-BundleViaArp 'companions-uninstall' | Out-Null
Show-State 'bundle uninstalled' $codes
Assert-Rst $false 'bundle uninstalled'
foreach ($c in $companions) { Assert-Companion $c $true 'bundle uninstalled' }

# C4: each uninstalls silently with exit 0; nothing left.
foreach ($c in $companions) {
  $rel = Get-Related "{$($c.release.upgradeCode)}"
  foreach ($p in $rel) { Invoke-Msiexec '/x' $p.ProductCode "$($c.id)-remove" }
  Assert-Companion $c $false 'removed'
}

# C7 + C4: each installs and uninstalls alone, with no RST on the computer.
foreach ($c in $companions) {
  $msi = (Resolve-Path (Join-Path $PayloadDir $c.release.asset)).Path
  Invoke-Msiexec '/i' $msi "$($c.id)-standalone"
  Assert-Rst $false "$($c.id) standalone"
  Assert-Companion $c $true "$($c.id) standalone, no RST"
  Invoke-Msiexec '/x' $msi "$($c.id)-standalone-remove"
  Assert-Companion $c $false "$($c.id) standalone removed"
}
Show-State 'end' $codes
exit 0
