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

  -Mode Migrate -From Preview|Unified -OldMsi <v2.2.0 asset>
      Upgrade from the last two-MSI release, with all three Revit stubs.
        Preview (AC4): RST-R27.msi v2.2.0 -> new RST.msi. The Upgrade row on
          the retired R27 code must remove "RST (Revit 2027 preview)" and the
          installed R27 engine must be the newly staged one.
        Unified (AC5): RST.msi v2.2.0 -> new RST.msi. MajorUpgrade must leave
          exactly one RST product, at the new version, with all six trees.

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
  [string]$SameVersionMsi
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
function Get-MsiProperty([string]$Path, [string]$Name) {
  (Get-MsiQuery $Path "SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'")[0][0]
}

# Runs msiexec; returns the exit code. Throws on failure unless -ExpectFail.
function Invoke-Msiexec([string]$Verb, [string]$Path, [string]$Log, [switch]$ExpectFail) {
  Write-Host "msiexec $Verb $Path (log: $Log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Path`" /qn /l*v `"$Log`"" -Wait -PassThru
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
        $left = if (Test-Path $dir) { @(Get-ChildItem $dir -Recurse -File | Where-Object { $_.Name -like 'RST*' -or $t -like 'RST\*' }) } else { @() }
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
$components = Get-MsiQuery $Msi 'SELECT `Component`, `Directory_`, `Attributes`, `Condition` FROM `Component`' 4
foreach ($c in $components) {
  $d = $c[1]; $year = $null
  while ($d) { if ($roots.ContainsKey($d)) { $year = $roots[$d]; break }; $d = $parent[$d] }
  if (-not $year) { $bad += "$($c[0]) in $($c[1]) is outside every Revit year's trees"; continue }
  $want = "REVIT${year}FOUND OR REMOVE~=`"ALL`""
  if ($c[3] -ne $want) { $bad += "$($c[0]) ($year) has condition '$($c[3])', expected '$want'" }
  if (([int]$c[2] -band $Transitive) -eq 0) { $bad += "$($c[0]) ($year) is not Transitive (attributes $($c[2]))" }
  $perYear[$year]++
}
Write-Host "Components: $($components.Count) (2025=$($perYear['2025']) 2026=$($perYear['2026']) 2027=$($perYear['2027'])); problems: $($bad.Count)"
$bad | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
Assert ($components.Count -gt 0 -and $bad.Count -eq 0) "every component carries 'REVIT<year>FOUND OR REMOVE~=`"ALL`"' for its year and is Transitive"
if ($StagedRoot) {
  foreach ($y in $Years.Keys) {
    $nn = $Years[$y]
    $staged = @(Get-ChildItem (Join-Path $StagedRoot "$nn\addins"), (Join-Path $StagedRoot "$nn\app") -Recurse -File).Count
    Assert ($perYear[$y] -eq $staged) "Revit $y components ($($perYear[$y])) = staged $nn files ($staged)"
  }
}
$launch = Get-MsiQuery $Msi 'SELECT `Condition`, `Description` FROM `LaunchCondition`' 2
Assert (@($launch | Where-Object { $_[0] -eq $LaunchConditionText -and $_[1] -eq $LaunchMessage }).Count -eq 1) 'LaunchCondition table holds the "none was found" check'
$secure = Get-MsiProperty $Msi 'SecureCustomProperties'
Assert ((@('REVIT2025FOUND', 'REVIT2026FOUND', 'REVIT2027FOUND') | Where-Object { ($secure -split ';') -notcontains $_ }).Count -eq 0) 'REVIT<year>FOUND properties are Secure'

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
  $sm = Get-ChildItem (Join-Path $AppData 'RST\R27\app') -Recurse -Filter System.Management.dll |
        ForEach-Object { $_.FullName.Substring($AppData.Length + 1) }
  Write-Host "R27 System.Management.dll: $($sm -join ', ')"

  # (d) Revit 2025 installed later: Repair adds it. Then 2026 removed:
  # Repair drops it. Then 2026 back: Repair restores it.
  Set-RevitStubs @('2025', '2026', '2027')
  $logD = Join-Path $LogDir 'rst-repair-add-2025.log'
  Invoke-Msiexec '/fa' $Msi $logD | Out-Null
  Show-LogLines $logD
  Assert-OneProduct $newProductCode $newVersion '(d) repair'
  Assert-Years @('2025', '2026', '2027') '(d) repair added 2025'

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
    $logB = Join-Path $LogDir 'rst-install-samever.log'
    Invoke-Msiexec '/i' $SameVersionMsi $logB | Out-Null
    Show-LogLines $logB @($newProductCode)
    Assert-OneProduct $pc2 $v2 '(b) same-version install'
    Assert-Years @('2025', '2026', '2027') '(b) all three detected'

    Invoke-Msiexec '/i' $SameVersionMsi (Join-Path $LogDir 'rst-install-repeat.log') | Out-Null
    Assert-OneProduct $pc2 $v2 '(b) repeated install'
    $uninstallMsi = $SameVersionMsi
  }

  # (e) Revit gone, then uninstall: everything is removed.
  Set-RevitStubs @()
  Invoke-Msiexec '/x' $uninstallMsi (Join-Path $LogDir 'rst-uninstall.log') | Out-Null
  Show-Related '(e) after uninstall'
  Assert ((Get-Related $UnifiedCode).Count -eq 0) '(e) no RST product after uninstall'
  foreach ($y in $Years.Keys) {
    foreach ($t in (Get-YearTrees $y).Keys) {
      Assert (-not (Test-Path (Join-Path $AppData $t))) "(e) $t removed"
    }
  }
  Assert-Probe $probe '(e)'
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
exit 0
