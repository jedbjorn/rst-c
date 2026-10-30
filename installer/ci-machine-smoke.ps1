<#
  ci-machine-smoke.ps1 - Windows Installer checks for RST-Machine.msi on a
  CI runner, which runs as an administrator (feature #17, spec #20 §7, AC9;
  F17 WU5). Called by .github/workflows/_build.yml (job msi-machine); no
  Revit needed: stub Revit.exe files in C:\Program Files\Autodesk\Revit
  <year>\ stand in for the installed years, as in ci-smoke.ps1.

  Package checks (the built RST-Machine.msi, against -UserMsi = RST.msi of
  the same commit):
    - identity: UpgradeCode 79C25959-..., per machine (ALLUSERS=1), name
      "RST (all users)"; RemoveExistingProducts inside the transaction (the
      MajorUpgrade rule of RST.msi, decision #9); the same launch conditions
      plus the per-user-copy check; Restart Manager off (N8)
    - components: each Revit year's carry 'REVIT<year>FOUND OR
      REMOVE~="ALL"' and Transitive (decision #10), per-year and tray counts
      match the staged files; NO ComponentId is shared with RST.msi (N6);
      TrayStartup has its own GUID
    - grants nothing: no service, permission (LockPermissions,
      MsiLockPermissionsEx, util PermissionEx/SecureObjects), user or group
      tables; every custom action is one of WiX's own XmlFile,
      CloseApplication or WixUI actions; the only registry row is the Run
      value
  Install checks:
    (p) RST.msi installed for this user -> a first RST-Machine.msi install
        is refused with the "already installed just for you" message
    (c) no Revit -> refused by the launch condition
    (a) Revit 2026 + 2027 -> only those years, per machine, each manifest's
        <Assembly> absolute; tray in Program Files, HKLM Run; nothing per
        user; the installer starts no tray (N8); ACLs: every folder and the
        Run key that existed before are unchanged (SDDL), and everything the
        install created only inherits (no explicit entry): nothing granted;
        no service or scheduled task added
    (d) Revit 2025 added, Repair -> 2025 added; ACLs still unchanged
    (e) uninstall -> nothing left, ACLs unchanged
    (g) the same commit built at 1.0.0 (-OlderMsi) installed, a tray started
        from Program Files, then this build over it: one product at the new
        version, the tray closed cleanly by WM_CLOSE and NOT restarted
        (Restart Manager may not restart it elevated, N8); uninstall clean
#>
param(
  [Parameter(Mandatory)][string]$Msi,
  [Parameter(Mandatory)][string]$UserMsi,
  [string]$OlderMsi,
  [string]$StagedRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Msi = (Resolve-Path $Msi).Path
$UserMsi = (Resolve-Path $UserMsi).Path
$UserCode = '{AF0331FC-5280-492A-94BD-95EDF108BE74}'
$AppData = $env:APPDATA
$LogDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$Transitive = 64

function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { throw "FAIL: $What" }
  Write-Host "PASS: $What"
}
. (Join-Path $PSScriptRoot 'ci-machine-layout.ps1')

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
      ProductCode    = $pc
      Name           = Get-ComProp $Installer 'ProductInfo' @($pc, 'ProductName')
      Version        = Get-ComProp $Installer 'ProductInfo' @($pc, 'VersionString')
      AssignmentType = Get-ComProp $Installer 'ProductInfo' @($pc, 'AssignmentType')
    }
  }
  return , $out
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
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  return , $rows
}
function Get-MsiProperty([string]$Path, [string]$Name) {
  $r = Get-MsiQuery $Path "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'"
  if ($r.Count) { return $r[0][0] } else { return $null }
}
function Get-Tables([string]$Path) { return , @((Get-MsiQuery $Path 'SELECT `Name` FROM `_Tables`') | ForEach-Object { $_[0] }) }

# Waits on msiexec itself (never on its process tree), with a hard timeout; returns the exit code.
function Invoke-Msiexec([string]$Verb, [string]$Path, [string]$Log, [switch]$ExpectFail, [string]$Extra = '') {
  Write-Host "msiexec $Verb $Path $Extra (log: $Log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Path`" /qn /norestart /l*v `"$Log`" $Extra" -PassThru
  $null = $p.Handle
  if (-not $p.WaitForExit(300000)) {
    if (Test-Path $Log) { Get-Content $Log -Tail 200 | Write-Host }
    $p | Stop-Process -Force -ErrorAction SilentlyContinue
    throw "FAIL: msiexec $Verb timed out"
  }
  Write-Host "  exit $($p.ExitCode)"
  if (-not $ExpectFail -and $p.ExitCode -ne 0) {
    if (Test-Path $Log) { Get-Content $Log -Tail 80 | Write-Host }
    throw "msiexec $Verb exited $($p.ExitCode) (expected 0: no failure, no reboot)"
  }
  return $p.ExitCode
}

function Set-RevitStubs([string[]]$Present) {
  foreach ($y in $MachineYears.Keys) {
    $dir = Join-Path $MachineProgramFiles "Autodesk\Revit $y"
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

function Assert-OneMachineProduct([string]$ProductCode, [string]$Version, [string]$Label) {
  $r = Get-Related $MachineUpgradeCode
  $r | ForEach-Object { Write-Host "  [$Label] $($_.ProductCode) '$($_.Name)' $($_.Version) AssignmentType=$($_.AssignmentType)" }
  Assert ($r.Count -eq 1 -and $r[0].ProductCode -eq $ProductCode -and $r[0].Version -eq $Version) "[$Label] exactly one RST-Machine product, $ProductCode at $Version"
  Assert ([string]$r[0].AssignmentType -eq '1') "[$Label] it is installed per machine (AssignmentType 1)"
  Assert ($r[0].Name -eq 'RST (all users)') "[$Label] its Add/Remove Programs name is 'RST (all users)'"
}

# ---- ACLs (AC9: grants nothing). SDDL of every folder that exists before the install, and of the Run key.
$AclRoots = @(
  $MachineProgramFiles,
  (Join-Path $MachineProgramFiles 'Autodesk'),
  (Join-Path $MachineProgramFiles 'Autodesk\Revit'),
  (Join-Path $MachineProgramFiles 'Autodesk\Revit\Addins'),
  $MachineProgramData,
  (Join-Path $MachineProgramData 'Autodesk'),
  (Join-Path $MachineProgramData 'Autodesk\Revit'),
  (Join-Path $MachineProgramData 'Autodesk\Revit\Addins')
) + @($MachineYears.Keys | ForEach-Object { Join-Path $MachineProgramFiles "Autodesk\Revit $_" })
function Get-AclSnapshot {
  $snap = [ordered]@{}
  foreach ($p in $AclRoots) { if (Test-Path $p) { $snap[$p] = (Get-Acl $p).Sddl } }
  $snap[$MachineRunKey] = (Get-Acl $MachineRunKey).Sddl
  return $snap
}
function Assert-AclsUnchanged($Before, [string]$Label) {
  foreach ($p in $Before.Keys) {
    if (-not (Test-Path $p)) {
      # Windows Installer may remove an empty folder of its Directory table on uninstall, even one that was there
      # before. That changes no permission on anything that remains.
      Write-Host "OBSERVED: [$Label] $p was removed (it was empty)"
      continue
    }
    $now = (Get-Acl $p).Sddl
    if ($now -ne $Before[$p]) { Write-Host "  before: $($Before[$p])"; Write-Host "  after:  $now" }
    Assert ($now -eq $Before[$p]) "[$Label] ACL of $p unchanged"
  }
}
# Everything the install created (folders and files under these roots) only inherits: no explicit access rule, no
# protected (non-inheriting) list. So it grants exactly what its parent already grants.
function Assert-OnlyInherited([string[]]$Roots, [string]$Label) {
  $items = @()
  foreach ($r in $Roots) { if (Test-Path $r) { $items += Get-Item $r; $items += @(Get-ChildItem $r -Recurse -Force) } }
  $bad = @()
  foreach ($i in $items) {
    $acl = Get-Acl $i.FullName
    $explicit = @($acl.Access | Where-Object { -not $_.IsInherited })
    if ($acl.AreAccessRulesProtected -or $explicit.Count -gt 0) {
      $bad += "$($i.FullName): protected=$($acl.AreAccessRulesProtected) explicit=$(($explicit | ForEach-Object { "$($_.IdentityReference) $($_.FileSystemRights) $($_.AccessControlType)" }) -join '; ')"
    }
  }
  $bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
  Assert ($items.Count -gt 0 -and $bad.Count -eq 0) "[$Label] all $($items.Count) folders and files RST-Machine.msi created only inherit their parent's access (nothing granted)"
}
# What the install created: Program Files\RST, each installed year's manifest folder, and every snapshot root that
# did not exist before (e.g. %ProgramData%\Autodesk on a runner without it).
function Get-CreatedRoots([string[]]$Years, $Before) {
  $roots = @($MachineRoot)
  foreach ($y in $Years) { $roots += Split-Path (Get-MachineManifest $y) }
  $roots += @($AclRoots | Where-Object { -not $Before.Contains($_) -and (Test-Path $_) })
  return , $roots
}
function Get-SystemInventory {
  return [pscustomobject]@{
    Services = @(Get-Service | ForEach-Object { $_.Name } | Sort-Object)
    Tasks    = @(Get-ScheduledTask -ErrorAction SilentlyContinue | ForEach-Object { "$($_.TaskPath)$($_.TaskName)" } | Sort-Object)
  }
}
function Assert-NothingAdded($Before, [string]$Label) {
  $now = Get-SystemInventory
  $newServices = @($now.Services | Where-Object { $Before.Services -notcontains $_ })
  $newTasks = @($now.Tasks | Where-Object { $Before.Tasks -notcontains $_ })
  Assert ($newServices.Count -eq 0) "[$Label] no service added ($($newServices -join ', '))"
  Assert ($newTasks.Count -eq 0) "[$Label] no scheduled task added ($($newTasks -join ', '))"
}

# ---- Tray helpers (the installing user's own tray from Program Files)
$UserSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$TrayMutex = "Local\RST.Tray.$UserSid"
function Test-TrayMutex {
  $m = $null
  $open = [Threading.Mutex]::TryOpenExisting($TrayMutex, [ref]$m)
  if ($m) { $m.Dispose() }
  return $open
}
function Seed-TraySettings {
  $f = Join-Path $AppData 'RST\tray_settings.json'
  New-Item -ItemType Directory -Force (Split-Path $f) | Out-Null
  Set-Content -Path $f -Value '{ "theme": "system", "checkForUpdates": true, "updateNoticeShownUtc": "2026-10-30T00:00:00+00:00", "shadowNoticeShownUtc": "2026-10-30T00:00:00+00:00" }' -NoNewline
}
function Get-Trays { return , @(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue) }
function Show-TrayLog {
  Get-ChildItem (Join-Path $AppData 'RST\logs') -Filter 'tray_*.log' -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "---- $($_.Name)"; Get-Content $_.FullName | Write-Host
  }
}

# ================================================================ package checks
$version = Get-MsiProperty $Msi 'ProductVersion'
$productCode = Get-MsiProperty $Msi 'ProductCode'
Write-Host "RST-Machine.msi: ProductVersion=$version ProductCode=$productCode"
Assert ((Get-MsiProperty $Msi 'UpgradeCode') -eq $MachineUpgradeCode) "UpgradeCode is RST-Machine.msi's own $MachineUpgradeCode"
Assert ((Get-MsiProperty $Msi 'UpgradeCode') -ne (Get-MsiProperty $UserMsi 'UpgradeCode')) 'its UpgradeCode differs from RST.msi''s (two products by design, decision #15)'
Assert ((Get-MsiProperty $Msi 'ALLUSERS') -eq '1') 'ALLUSERS=1: a per-machine package'
Assert ((Get-MsiProperty $Msi 'ProductName') -eq 'RST (all users)') "ProductName is 'RST (all users)'"
Assert ((Get-MsiProperty $Msi 'MSIRESTARTMANAGERCONTROL') -eq 'Disable') 'MSIRESTARTMANAGERCONTROL=Disable: Restart Manager never restarts a tray from this elevated install (N8)'

$seq = @{}
foreach ($row in (Get-MsiQuery $Msi 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`' 2)) { $seq[$row[0]] = [int]$row[1] }
Assert ($seq.ContainsKey('RemoveExistingProducts') -and $seq['RemoveExistingProducts'] -gt $seq['InstallInitialize'] -and $seq['RemoveExistingProducts'] -lt $seq['InstallFiles']) 'RemoveExistingProducts sequenced after InstallInitialize and before InstallFiles (as RST.msi)'
$majorRows = Get-MsiQuery $Msi 'SELECT `UpgradeCode`, `VersionMin`, `VersionMax`, `Attributes`, `ActionProperty` FROM `Upgrade`' 5
$majorRows | ForEach-Object { Write-Host "  Upgrade: $($_ -join ' | ')" }
Assert (@($majorRows | Where-Object { $_[0] -eq $MachineUpgradeCode }).Count -ge 1) 'the Upgrade table upgrades RST-Machine.msi''s own UpgradeCode'
Assert (@($majorRows | Where-Object { $_[0] -ne $MachineUpgradeCode }).Count -eq 0) 'and no other product (the per-user R27 preview row is RST.msi''s only)'

$launch = Get-MsiQuery $Msi 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 2
$launch | ForEach-Object { Write-Host "  LaunchCondition: $($_[0]) => $($_[1])" }
Assert (@($launch | Where-Object { $_[0] -eq 'Installed OR REVIT2025FOUND OR REVIT2026FOUND OR REVIT2027FOUND' }).Count -eq 1) 'the Revit launch condition is RST.msi''s'
Assert (@($launch | Where-Object { $_[0] -eq 'Installed OR RSTFROMSETUP OR NOT RSTUSERCOPY' }).Count -eq 1) 'the per-user-copy launch condition is present (skipped through RST-Setup.exe: RSTFROMSETUP)'

# Components: years, tray, and no ComponentId shared with RST.msi (N6).
$roots = @{}
foreach ($y in $MachineYears.Keys) { $roots["Addins${y}Dir"] = $y; $roots["$($MachineYears[$y])Dir"] = $y }
$parent = @{}
foreach ($row in (Get-MsiQuery $Msi 'SELECT `Directory`, `Directory_Parent` FROM `Directory`' 2)) { $parent[$row[0]] = $row[1] }
$perYear = @{ '2025' = 0; '2026' = 0; '2027' = 0 }
$tray = @(); $bad = @()
$components = Get-MsiQuery $Msi 'SELECT `Component`, `ComponentId`, `Directory_`, `Attributes`, `Condition` FROM `Component`' 5
foreach ($c in $components) {
  $d = $c[2]; $year = $null; $isTray = $false
  while ($d) { if ($d -eq 'TrayDir') { $isTray = $true; break }; if ($roots.ContainsKey($d)) { $year = $roots[$d]; break }; $d = $parent[$d] }
  if ($isTray) { if ($c[4]) { $bad += "$($c[0]) (tray) has condition '$($c[4])'" }; $tray += , $c; continue }
  if (-not $year) { $bad += "$($c[0]) in $($c[2]) is outside every Revit year's trees"; continue }
  $want = "REVIT${year}FOUND OR REMOVE~=`"ALL`""
  if ($c[4] -ne $want) { $bad += "$($c[0]) ($year) has condition '$($c[4])'" }
  if (([int]$c[3] -band $Transitive) -eq 0) { $bad += "$($c[0]) ($year) is not Transitive" }
  $perYear[$year]++
}
$bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
Write-Host "Components: $($components.Count) (2025=$($perYear['2025']) 2026=$($perYear['2026']) 2027=$($perYear['2027']) tray=$($tray.Count))"
Assert ($bad.Count -eq 0) "every Revit-year component carries its year's condition and Transitive; tray components are unconditional"
if ($StagedRoot) {
  foreach ($y in $MachineYears.Keys) {
    $nn = $MachineYears[$y]
    $staged = @(Get-ChildItem (Join-Path $StagedRoot "$nn\addins"), (Join-Path $StagedRoot "$nn\app") -Recurse -File).Count
    Assert ($perYear[$y] -eq $staged) "Revit $y components ($($perYear[$y])) = staged $nn files ($staged)"
  }
  $stagedTray = @(Get-ChildItem (Join-Path $StagedRoot 'Tray') -Recurse -File).Count
  Assert ($tray.Count -eq $stagedTray + 1) "tray components ($($tray.Count)) = staged Tray files ($stagedTray) + TrayStartup"
}
$trayStartup = @($components | Where-Object { $_[0] -eq 'TrayStartup' })
Assert ($trayStartup.Count -eq 1 -and $trayStartup[0][1] -eq '{A8082E2F-7DD4-4101-88F4-69C0BB6FBC43}') "TrayStartup has RST-Machine.msi's own GUID (got $(if ($trayStartup.Count) { $trayStartup[0][1] }))"
$userIds = [Collections.Generic.HashSet[string]]::new([string[]]((Get-MsiQuery $UserMsi 'SELECT `ComponentId` FROM `Component`') | ForEach-Object { $_[0] }), [StringComparer]::OrdinalIgnoreCase)
$shared = @($components | Where-Object { $userIds.Contains($_[1]) })
$shared | Select-Object -First 10 | ForEach-Object { Write-Host "  shared: $($_[0]) $($_[1])" }
Assert ($shared.Count -eq 0) "no ComponentId is shared with RST.msi ($($components.Count) machine, $($userIds.Count) user components; N6)"

# Grants nothing: no privileged tables, only WiX's own custom actions, one registry row.
$tables = Get-Tables $Msi
Write-Host "tables: $($tables -join ', ')"
$privileged = @($tables | Where-Object { $_ -match '^(ServiceInstall|ServiceControl|LockPermissions|MsiLockPermissionsEx)$|SecureObject|PermissionEx|User$|Group$|UserGroup' })
Assert ($privileged.Count -eq 0) "no service, permission, user or group table ($($privileged -join ', '))"
$cas = @((Get-MsiQuery $Msi 'SELECT `Action`, `Type`, `Source`, `Target` FROM `CustomAction`' 4))
$cas | ForEach-Object { Write-Host "  CustomAction: $($_ -join ' | ')" }
$unexpected = @($cas | Where-Object { $_[0] -notmatch '^(Wix4(Sched|Exec)XmlFile.*|Wix4ExecXmlFileRollback.*|Wix4CloseApplications.*|Wix4CheckRebootRequired.*|WixUI.*|WIXUI.*)$' })
Assert ($unexpected.Count -eq 0) "every custom action is one of WiX's XmlFile, CloseApplication or WixUI actions ($($unexpected | ForEach-Object { $_[0] }))"
$reg = Get-MsiQuery $Msi 'SELECT `Root`, `Key`, `Name`, `Value` FROM `Registry`' 4   # not @(...): a one-row result must stay one row
$reg | ForEach-Object { Write-Host "  Registry: $($_ -join ' | ')" }
Assert ($reg.Count -eq 1 -and $reg[0][0] -eq '-1' -and $reg[0][1] -eq 'Software\Microsoft\Windows\CurrentVersion\Run' -and $reg[0][2] -eq 'RST' -and $reg[0][3] -eq '"[TrayDir]RST.Tray.exe" --startup') 'the only registry row is the HKMU (HKLM here) Run value RST'
$xml = @((Get-Tables $Msi) | Where-Object { $_ -match 'XmlFile' })
Assert ($xml.Count -eq 1) 'the package has an XmlFile table (each manifest''s absolute <Assembly>)'
$xmlRows = Get-MsiQuery $Msi "SELECT * FROM ``$($xml[0])``" 5
$xmlRows | ForEach-Object { Write-Host "  $($xml[0]): $($_ -join ' | ')" }
foreach ($y in $MachineYears.Keys) {
  Assert (@($xmlRows | Where-Object { ($_ -join '|') -match "RstAddin$y" -and ($_ -join '|') -match [regex]::Escape("[$($MachineYears[$y])Dir]RST.Bootstrap.dll") }).Count -eq 1) "XmlFile sets Revit $y's <Assembly> to [$($MachineYears[$y])Dir]RST.Bootstrap.dll"
}

Assert ((Get-Related $MachineUpgradeCode).Count -eq 0 -and (Get-Related $UserCode).Count -eq 0) 'runner starts with no RST product'

# ================================================================ (p) the user's own copy blocks a first install
Set-RevitStubs @('2025', '2026', '2027')
Invoke-Msiexec '/i' $UserMsi (Join-Path $LogDir 'machine-p-user-install.log') | Out-Null
Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
$logP = Join-Path $LogDir 'machine-p-refused.log'
$codeP = Invoke-Msiexec '/i' $Msi $logP -ExpectFail
Assert ($codeP -eq 1603) "(p) RST-Machine.msi refuses a first install while RST.msi is installed for this user (exit $codeP)"
$msg = Select-String -Path $logP -Pattern 'RST is already installed just for you' -SimpleMatch | Select-Object -First 1
Assert ($null -ne $msg) '(p) its log carries the "already installed just for you" message'
Write-Host "  $($msg.Line.Trim())"
Assert ($msg.Line -match [regex]::Escape((Join-Path $AppData 'Autodesk\Revit\Addins'))) '(p) the message names the per-user manifest it found'
Assert ((Get-Related $MachineUpgradeCode).Count -eq 0) '(p) no RST-Machine product installed'
foreach ($p in (Get-Related $UserCode)) { Invoke-Msiexec '/x' $p.ProductCode (Join-Path $LogDir 'machine-p-user-remove.log') | Out-Null }
Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Assert ((Get-Related $UserCode).Count -eq 0) '(p) RST.msi removed again'

# ================================================================ (c) no Revit
Set-RevitStubs @()
$logC = Join-Path $LogDir 'machine-c-norevit.log'
$codeC = Invoke-Msiexec '/i' $Msi $logC -ExpectFail
Assert ($codeC -ne 0 -and $codeC -ne 3010) "(c) install without Revit fails (exit $codeC)"
Assert (Select-String -Path $logC -Pattern 'RST needs Revit 2025, 2026 or 2027' -SimpleMatch -Quiet) '(c) log shows the launch-condition message'
Assert ((Get-Related $MachineUpgradeCode).Count -eq 0) '(c) nothing installed'

# ================================================================ (a) Revit 2026 + 2027
Set-RevitStubs @('2026', '2027')
$acl = Get-AclSnapshot
Write-Host "ACL snapshot ($($acl.Count) paths):"; $acl.Keys | ForEach-Object { Write-Host "  $_ = $($acl[$_])" }
$inventory = Get-SystemInventory
$traysBefore = (Get-Trays).Count
Invoke-Msiexec '/i' $Msi (Join-Path $LogDir 'machine-a-install.log') | Out-Null
Assert-OneMachineProduct $productCode $version '(a)'
Assert-MachineYears @('2026', '2027') '(a)'
Assert-MachineTray $true '(a)'
Assert ($null -eq (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name RST -ErrorAction SilentlyContinue)) '(a) no HKCU Run value'
foreach ($y in '2025', '2026', '2027') { Assert (-not (Test-Path (Join-Path $AppData "Autodesk\Revit\Addins\$y\RST.addin"))) "(a) nothing per user in Addins\$y" }
Assert (-not (Test-Path (Join-Path $env:LOCALAPPDATA 'Programs\RST'))) '(a) nothing under %LocalAppData%\Programs\RST'
Start-Sleep -Seconds 10
Assert ((Get-Trays).Count -eq $traysBefore) "(a) the installer started no tray (N8: never elevated); it starts at the next sign-in"
Assert-AclsUnchanged $acl '(a)'
Assert-OnlyInherited (Get-CreatedRoots @('2026', '2027') $acl) '(a)'
Assert-NothingAdded $inventory '(a)'

# ================================================================ (d) Repair adds a year
Set-RevitStubs @('2025', '2026', '2027')
Invoke-Msiexec '/fa' $Msi (Join-Path $LogDir 'machine-d-repair.log') | Out-Null
Assert-OneMachineProduct $productCode $version '(d)'
Assert-MachineYears @('2025', '2026', '2027') '(d) repair added 2025'
Assert-AclsUnchanged $acl '(d)'
Assert-OnlyInherited (Get-CreatedRoots @('2025', '2026', '2027') $acl) '(d)'

# ================================================================ (e) uninstall
Invoke-Msiexec '/x' $Msi (Join-Path $LogDir 'machine-e-uninstall.log') | Out-Null
Assert ((Get-Related $MachineUpgradeCode).Count -eq 0) '(e) no RST-Machine product after uninstall'
Assert-MachineGone '(e)'
Assert-AclsUnchanged $acl '(e)'
Assert-NothingAdded $inventory '(e)'

# ================================================================ (g) upgrade from an older build, a tray running
if ($OlderMsi) {
  $OlderMsi = (Resolve-Path $OlderMsi).Path
  $vOld = Get-MsiProperty $OlderMsi 'ProductVersion'
  $pcOld = Get-MsiProperty $OlderMsi 'ProductCode'
  Assert ((Get-MsiProperty $OlderMsi 'UpgradeCode') -eq $MachineUpgradeCode -and [version]$vOld -lt [version]$version) "(g) the older build ($vOld) is RST-Machine.msi below $version"
  Invoke-Msiexec '/i' $OlderMsi (Join-Path $LogDir 'machine-g-older.log') | Out-Null
  Assert-OneMachineProduct $pcOld $vOld '(g) older'
  Seed-TraySettings
  $t = Start-Process $MachineTrayExe -ArgumentList '--startup' -PassThru
  $null = $t.Handle
  $deadline = (Get-Date).AddSeconds(45)
  while (-not (Test-TrayMutex)) { if ($t.HasExited -or (Get-Date) -gt $deadline) { Show-TrayLog; throw "FAIL: (g) tray did not start" }; Start-Sleep -Milliseconds 250 }
  Write-Host "(g) tray pid $($t.Id) from $MachineTrayExe"
  $logG = Join-Path $LogDir 'machine-g-upgrade.log'
  Invoke-Msiexec '/i' $Msi $logG | Out-Null
  Select-String -Path $logG -Pattern 'RESTART MANAGER', 'CloseApplication', 'RST.Tray.exe', $pcOld -SimpleMatch | Select-Object -First 30 | ForEach-Object { Write-Host "  $($_.Line)" }
  Assert-OneMachineProduct $productCode $version '(g) upgraded'
  Assert (Select-String -Path $logG -Pattern $pcOld -SimpleMatch -Quiet) "(g) the log names the removed older product $pcOld"
  Assert-MachineYears @('2025', '2026', '2027') '(g) upgraded'
  Assert-MachineTray $true '(g) upgraded'
  Assert ($t.WaitForExit(30000)) '(g) the running tray was closed'
  Assert ($t.ExitCode -eq 0) "(g) it exited cleanly (exit $($t.ExitCode))"
  $exitLine = @(Get-ChildItem (Join-Path $AppData 'RST\logs') -Filter 'tray_*.log' | ForEach-Object { Get-Content $_.FullName } | Where-Object { $_.Contains("[$($t.Id)]") -and $_.Contains('RST tray exited') })
  Write-Host "  tray log: $($exitLine -join ' / ')"
  Assert (@($exitLine | Where-Object { $_ -match 'WM_CLOSE' }).Count -eq 1) '(g) it was closed by util:CloseApplication''s WM_CLOSE (Restart Manager is off here)'
  Start-Sleep -Seconds 10
  $again = Get-Trays
  $again | ForEach-Object { Write-Host "  unexpected tray pid $($_.Id): $((Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine)" }
  Assert ($again.Count -eq 0) '(g) no tray was started again by the elevated installer (N8)'
  Invoke-Msiexec '/x' $Msi (Join-Path $LogDir 'machine-g-uninstall.log') | Out-Null
  Assert-MachineGone '(g) uninstalled'
  Assert-AclsUnchanged $acl '(g)'
}

Assert ((Get-Related $MachineUpgradeCode).Count -eq 0 -and (Get-Related $UserCode).Count -eq 0) 'runner ends with no RST product'
exit 0
