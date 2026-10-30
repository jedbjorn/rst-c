<#
  rst-c's own: AC5's fixture half (feature #17, spec #20 §6; F17 WU3). The tray's companion detection
  (RST.Tray's MsiProducts over msi.dll, then RST.Core's ProductDetection with the real manifest scan) against
  spec #15's fixture companion MSI, the test-only RST-CompanionFixture-<v>.msi the bundle job already builds
  (installer-bundle/fixture, artifact bundle-test). No Revit and no real companion needed.

  For each scenario this script installs or removes the fixture with msiexec, then writes what Windows
  Installer itself reports into an expectation file:
    - ProductCode and ProductVersion from the MSI's own Property table (what msiexec installed);
    - the products Windows Installer lists for the fixture's UpgradeCode (its COM automation interface,
      RelatedProducts), each with its VersionString and its context (ProductInfo AssignmentType: 0 this user,
      1 the machine), which must agree with how msiexec was run (per-user, or ALLUSERS=1);
  and runs RST.RuntimeTests' MsiFixtureDetectionTests, which must detect exactly that (RST_MSI_FIXTURE_EXPECT).
  The test is skipped without the variable, so this script also requires the result file it writes.

    none        nothing installed                              NotInstalled
    user-1.0.0  per-user install of 1.0.0                      Installer, 1.0.0, this user
    user-2.0.0  per-user 2.0.0 over it (MajorUpgrade)          Installer, 2.0.0, this user, one product
    machine     removed, then 1.1.0 with ALLUSERS=1            Installer, 1.1.0, the machine
    manual      removed; a hand-copied .addin with its ClientId Manual ("installed manually")
    both        that .addin, and 1.1.0 per-user again          Installer (Windows Installer wins)

  Build first: dotnet build src/RST.RuntimeTests/RST.RuntimeTests.csproj -c "Release R25".
  Product queries go through the WindowsInstaller.Installer COM object via InvokeMember, as in
  installer-bundle/ci-bundle-smoke.ps1 (it has no type library PowerShell can bind to).
#>
param(
  [Parameter(Mandatory)][string]$FixtureDir,
  [string]$Configuration = 'Release R25',
  [string]$OutDir = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'tray-detect' } else { Join-Path $env:TEMP 'tray-detect' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$FixtureCode = '{A0E85F7A-BF17-4052-AB6A-010781D8CF6D}'   # installer-bundle/fixture/Product.wxs
$FixtureClientId = '7e57f1c7-a5e5-4c3d-8f0e-000000000ae5'  # test-only, for the hand-copied manifest
$ManualAddin = Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2026\RSTCompanionFixture.addin'
New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$Installer = New-Object -ComObject WindowsInstaller.Installer
function Get-ComProp($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $Obj, $ArgList)
}
function Invoke-ComMethod($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Obj, $ArgList)
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
# What Windows Installer lists for the fixture's UpgradeCode: ProductCode, VersionString, context (2 user, 4 machine).
function Get-Installed {
  $list = Get-ComProp $Installer 'RelatedProducts' @($FixtureCode)
  $n = Get-ComProp $list 'Count'
  $out = @()
  for ($i = 0; $i -lt $n; $i++) {
    $pc = Get-ComProp $list 'Item' @($i)
    $assignment = Get-ComProp $Installer 'ProductInfo' @($pc, 'AssignmentType')
    $out += [pscustomobject]@{
      productCode = $pc
      version     = Get-ComProp $Installer 'ProductInfo' @($pc, 'VersionString')
      context     = if ("$assignment" -eq '1') { 4 } else { 2 }
    }
  }
  return , $out
}

function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { throw "FAIL: $What" }
  Write-Host "PASS: $What"
}
function Invoke-Msiexec([string]$Verb, [string]$Target, [string]$Name, [string]$Extra = '') {
  $log = Join-Path $OutDir "$Name.log"
  Write-Host "msiexec $Verb $Target $Extra /qn (log: $log)"
  $p = Start-Process msiexec.exe -ArgumentList "$Verb `"$Target`" $Extra /qn /norestart /l*v `"$log`"" -Wait -PassThru
  Write-Host "  exit $($p.ExitCode)"
  if ($p.ExitCode -ne 0) {
    if (Test-Path $log) { Get-Content $log -Tail 60 | Write-Host }
    throw "msiexec $Verb $Target exited $($p.ExitCode)"
  }
}
function Get-FixtureMsi([string]$Version) {
  $p = Join-Path $FixtureDir "RST-CompanionFixture-$Version.msi"
  if (-not (Test-Path $p)) { throw "fixture MSI missing: $p" }
  return (Resolve-Path $p).Path
}
function Remove-Fixture([string]$Label) {
  $installed = Get-Installed
  foreach ($p in $installed) { Invoke-Msiexec '/x' $p.productCode "remove-$Label" }
  Assert ((Get-Installed).Count -eq 0) "[$Label] no fixture product left"
}
function Set-ManualAddin([bool]$Present) {
  if ($Present) {
    New-Item -ItemType Directory -Force (Split-Path $ManualAddin) | Out-Null
    Set-Content -Path $ManualAddin -Encoding UTF8 -Value @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RST Companion Fixture</Name>
    <Assembly>RSTCompanionFixture.dll</Assembly>
    <ClientId>$FixtureClientId</ClientId>
    <FullClassName>Fixture.App</FullClassName>
    <VendorId>DSOS</VendorId>
  </AddIn>
</RevitAddIns>
"@
  } elseif (Test-Path $ManualAddin) {
    Remove-Item $ManualAddin -Force
  }
}

# One scenario: the expectation from Windows Installer, then the tray's detection must match it.
$Results = @()
function Test-Scenario([string]$Scenario, [string]$Kind, [object[]]$Expected) {
  $installed = Get-Installed
  Write-Host "[$Scenario] Windows Installer lists: $(($installed | ForEach-Object { "$($_.productCode) $($_.version) ctx $($_.context)" }) -join '; ')"
  Assert ($installed.Count -eq $Expected.Count) "[$Scenario] Windows Installer lists $($Expected.Count) fixture product(s)"
  foreach ($e in $Expected) {
    $hit = @($installed | Where-Object { $_.productCode -eq $e.productCode })
    Assert ($hit.Count -eq 1) "[$Scenario] $($e.productCode) (the MSI's ProductCode) is the one installed"
    Assert ($hit[0].version -eq $e.version) "[$Scenario] its VersionString $($hit[0].version) is the MSI's ProductVersion $($e.version)"
    Assert ($hit[0].context -eq $e.context) "[$Scenario] its context ($($hit[0].context)) is how msiexec ran ($($e.context))"
  }
  $expectFile = Join-Path $OutDir "$Scenario.expect.json"
  $resultFile = Join-Path $OutDir "$Scenario.result.json"
  Remove-Item $resultFile -ErrorAction SilentlyContinue
  [ordered]@{
    scenario = $Scenario; upgradeCode = $FixtureCode; clientId = $FixtureClientId; kind = $Kind
    products = @($installed)
  } | ConvertTo-Json -Depth 4 | Set-Content -Path $expectFile -Encoding UTF8

  $env:RST_MSI_FIXTURE_EXPECT = $expectFile
  $env:RST_MSI_FIXTURE_RESULT = $resultFile
  & dotnet test src/RST.RuntimeTests/RST.RuntimeTests.csproj -c $Configuration --no-build --nologo `
      --filter 'FullyQualifiedName~RST.RuntimeTests.Tray.MsiFixtureDetectionTests' --logger 'console;verbosity=normal'
  $code = $LASTEXITCODE
  Remove-Item Env:RST_MSI_FIXTURE_EXPECT, Env:RST_MSI_FIXTURE_RESULT
  Assert ($code -eq 0) "[$Scenario] MsiFixtureDetectionTests passed"
  Assert (Test-Path $resultFile) "[$Scenario] the test ran (not skipped) and wrote its result"
  $result = Get-Content $resultFile -Raw | ConvertFrom-Json
  Assert ($result.Kind -eq $Kind) "[$Scenario] the tray detects $Kind"
  $script:Results += [pscustomobject]@{
    Scenario = $Scenario; Expected = $Kind; Detected = $result.Kind
    Installer = (($installed | ForEach-Object { "$($_.version) ctx $($_.context)" }) -join '; ')
    Tray = (($result.Products | ForEach-Object { "$($_.VersionText) $($_.Context)" }) -join '; ')
  }
}

$f100 = Get-FixtureMsi '1.0.0'; $f110 = Get-FixtureMsi '1.1.0'; $f200 = Get-FixtureMsi '2.0.0'
$pc = @{}
foreach ($pair in @(@('1.0.0', $f100), @('1.1.0', $f110), @('2.0.0', $f200))) {
  $pc[$pair[0]] = Get-MsiProperty $pair[1] 'ProductCode'
  Assert ((Get-MsiProperty $pair[1] 'ProductVersion') -eq $pair[0]) "fixture $($pair[0]): the MSI's ProductVersion is $($pair[0])"
  Assert ((Get-MsiProperty $pair[1] 'UpgradeCode') -eq $FixtureCode) "fixture $($pair[0]): the MSI's UpgradeCode is $FixtureCode"
}

Set-ManualAddin $false
Remove-Fixture 'start'
Test-Scenario 'none' 'NotInstalled' @()

Invoke-Msiexec '/i' $f100 'install-user-1.0.0'
Test-Scenario 'user-1.0.0' 'Installer' @([pscustomobject]@{ productCode = $pc['1.0.0']; version = '1.0.0'; context = 2 })

Invoke-Msiexec '/i' $f200 'install-user-2.0.0'
Test-Scenario 'user-2.0.0' 'Installer' @([pscustomobject]@{ productCode = $pc['2.0.0']; version = '2.0.0'; context = 2 })

Remove-Fixture 'before-machine'
Invoke-Msiexec '/i' $f110 'install-machine-1.1.0' 'ALLUSERS=1'
Test-Scenario 'machine' 'Installer' @([pscustomobject]@{ productCode = $pc['1.1.0']; version = '1.1.0'; context = 4 })

Remove-Fixture 'before-manual'
Set-ManualAddin $true
Test-Scenario 'manual' 'Manual' @()

Invoke-Msiexec '/i' $f110 'install-user-1.1.0'
Test-Scenario 'both' 'Installer' @([pscustomobject]@{ productCode = $pc['1.1.0']; version = '1.1.0'; context = 2 })

Remove-Fixture 'end'
Set-ManualAddin $false

Write-Host ''
Write-Host 'AC5 (fixture): Windows Installer vs the tray'
$Results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
$Results | ConvertTo-Json -Depth 3 | Set-Content -Path (Join-Path $OutDir 'summary.json') -Encoding UTF8
Write-Host "PASS: tray detection matches Windows Installer in all $($Results.Count) scenarios"
