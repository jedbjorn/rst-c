<#
  ci-smoke.ps1 — Windows Installer checks for RST.msi on a clean CI runner
  (feature #11). Called by .github/workflows/_build.yml; no Revit needed.

  -Mode Install
      Permanent install/uninstall smoke (D6, AC3). Seeds a user-data probe,
      installs RST.msi silently, asserts the six per-major trees and their
      key DLLs, uninstalls, then asserts the trees are gone and the probe
      survived.

  -Mode Migrate -From Preview|Unified -OldMsi <v2.2.0 asset>
      Upgrade from the last two-MSI release.
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
  [string]$StagedR27 = 'build/R27'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$UnifiedCode = '{AF0331FC-5280-492A-94BD-95EDF108BE74}'
$PreviewCode = '{02BE4399-A563-4B15-BD12-ECF5D8486FB3}'   # retired R27 preview
$AppData = $env:APPDATA
$LogDir = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
$Msi = (Resolve-Path $Msi).Path

# The six install trees and the files each must carry.
$Trees = [ordered]@{
  'Autodesk\Revit\Addins\2025' = @('RST.addin', 'RST.Bootstrap.dll')
  'Autodesk\Revit\Addins\2026' = @('RST.addin', 'RST.Bootstrap.dll')
  'Autodesk\Revit\Addins\2027' = @('RST.addin', 'RST.Bootstrap.dll')
  'RST\R25\app'                = @('RST.Engine.dll', 'RST.UI.dll', 'RST.Core.dll', 'RST.Engine.deps.json')
  'RST\R26\app'                = @('RST.Engine.dll', 'RST.UI.dll', 'RST.Core.dll', 'RST.Engine.deps.json')
  'RST\R27\app'                = @('RST.Engine.dll', 'RST.UI.dll', 'RST.Core.dll', 'RST.Engine.deps.json')
}

$Installer = New-Object -ComObject WindowsInstaller.Installer
function Get-ComProp($Obj, [string]$Name, [object[]]$ArgList = @()) {
  $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $Obj, $ArgList)
}
function Invoke-ComMethod($Obj, [string]$Name, [object[]]$ArgList = @()) {
  $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Obj, $ArgList)
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

function Invoke-Msiexec([string]$Verb, [string]$Path, [string]$Log) {
  Write-Host "msiexec $Verb $Path (log: $Log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Path`" /qn /l*v `"$Log`"" -Wait -PassThru
  if ($p.ExitCode -notin 0, 3010) {
    if (Test-Path $Log) { Get-Content $Log -Tail 60 | Write-Host }
    throw "msiexec $Verb exited $($p.ExitCode)"
  }
  Write-Host "  exit $($p.ExitCode)"
}

function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { throw "FAIL: $What" }
  Write-Host "PASS: $What"
}

function Assert-TreesPresent {
  foreach ($t in $Trees.Keys) {
    $dir = Join-Path $AppData $t
    foreach ($f in $Trees[$t]) {
      Assert (Test-Path (Join-Path $dir $f) -PathType Leaf) "$t\$f present"
    }
  }
}

function Show-LogLines([string]$Log, [string[]]$Extra = @()) {
  $pat = @('FindRelatedProducts', 'RemoveExistingProducts', 'R27PREVIEWFOUND', 'WIX_UPGRADE_DETECTED') + $Extra
  Write-Host "---- $([IO.Path]::GetFileName($Log)): upgrade lines"
  Select-String -Path $Log -Pattern $pat -SimpleMatch | ForEach-Object { Write-Host $_.Line }
  Write-Host '----'
}

function Seed-Probe {
  $probe = Join-Path $AppData 'RST\profiles\probe.txt'
  New-Item -ItemType Directory -Force (Split-Path $probe) | Out-Null
  Set-Content -Path $probe -Value 'rst-c ci probe' -NoNewline
  return $probe
}

$newVersion = Get-MsiProperty $Msi 'ProductVersion'
$newProductCode = Get-MsiProperty $Msi 'ProductCode'
Write-Host "New RST.msi: ProductVersion=$newVersion ProductCode=$newProductCode"
Show-Related 'start'
Assert ((Get-Related $UnifiedCode).Count -eq 0 -and (Get-Related $PreviewCode).Count -eq 0) 'runner starts with no RST product'

if ($Mode -eq 'Install') {
  $probe = Seed-Probe

  Invoke-Msiexec '/i' $Msi (Join-Path $LogDir 'rst-install.log')
  Show-Related 'after install'
  $r = Get-Related $UnifiedCode
  Assert ($r.Count -eq 1 -and $r[0].Version -eq $newVersion) "one RST product at $newVersion"
  Assert-TreesPresent
  $sm = Get-ChildItem (Join-Path $AppData 'RST\R27\app') -Recurse -Filter System.Management.dll |
        ForEach-Object { $_.FullName.Substring($AppData.Length + 1) }
  Write-Host "R27 System.Management.dll: $($sm -join ', ')"

  Invoke-Msiexec '/x' $Msi (Join-Path $LogDir 'rst-uninstall.log')
  Show-Related 'after uninstall'
  Assert ((Get-Related $UnifiedCode).Count -eq 0) 'no RST product after uninstall'
  foreach ($t in $Trees.Keys) {
    Assert (-not (Test-Path (Join-Path $AppData $t))) "$t removed"
  }
  Assert ((Test-Path $probe) -and (Get-Content $probe -Raw) -eq 'rst-c ci probe') 'user data probe survives'
  exit 0
}

# ---- Migrate
if (-not $From -or -not $OldMsi) { throw '-Mode Migrate needs -From and -OldMsi' }
$OldMsi = (Resolve-Path $OldMsi).Path
$oldCode = Get-MsiProperty $OldMsi 'UpgradeCode'
$oldPC = Get-MsiProperty $OldMsi 'ProductCode'
$oldVersion = Get-MsiProperty $OldMsi 'ProductVersion'
Write-Host "Old $([IO.Path]::GetFileName($OldMsi)): UpgradeCode=$oldCode ProductCode=$oldPC ProductVersion=$oldVersion"
$expectCode = if ($From -eq 'Preview') { $PreviewCode } else { $UnifiedCode }
Assert ($oldCode -eq $expectCode) "old MSI carries UpgradeCode $expectCode"

$probe = Seed-Probe
Invoke-Msiexec '/i' $OldMsi (Join-Path $LogDir 'old-install.log')
Show-Related 'before'
$before = Get-Related $oldCode
Assert ($before.Count -eq 1 -and $before[0].ProductCode -eq $oldPC) "old product $oldPC installed"

$engine27 = Join-Path $AppData 'RST\R27\app\RST.Engine.dll'
$stagedHash = (Get-FileHash (Join-Path $StagedR27 'app\RST.Engine.dll') -Algorithm SHA256).Hash
if ($From -eq 'Preview') {
  $oldHash = (Get-FileHash $engine27 -Algorithm SHA256).Hash
  Write-Host "R27 RST.Engine.dll before: $oldHash (preview)"
  Write-Host "R27 RST.Engine.dll staged: $stagedHash (new build)"

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
Invoke-Msiexec '/i' $Msi $log
Show-Related 'after'
Show-LogLines $log @($oldPC)

$after = Get-Related $UnifiedCode
Assert ($after.Count -eq 1 -and $after[0].ProductCode -eq $newProductCode -and $after[0].Version -eq $newVersion) "exactly one RST product, $newProductCode at $newVersion"
Assert ((Get-Related $PreviewCode).Count -eq 0) 'no product left on the retired R27 preview UpgradeCode'
Assert ((Select-String -Path $log -Pattern 'RemoveExistingProducts' -SimpleMatch -Quiet)) 'log shows RemoveExistingProducts'
Assert ((Select-String -Path $log -Pattern $oldPC -SimpleMatch -Quiet)) "log names the removed product $oldPC"
Assert-TreesPresent
$newHash = (Get-FileHash $engine27 -Algorithm SHA256).Hash
Write-Host "R27 RST.Engine.dll after:  $newHash"
Assert ($newHash -eq $stagedHash) 'installed R27 engine equals the newly staged engine'
Assert ((Test-Path $probe) -and (Get-Content $probe -Raw) -eq 'rst-c ci probe') 'user data probe survives'
exit 0
