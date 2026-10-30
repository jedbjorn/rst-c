<#
  ci-bundle-build.ps1 - the CI bundle job's builds and package checks
  (feature #13 Phase 2, spec #15, AC3). Called by .github/workflows/_build.yml
  after fetch-companions.ps1 has downloaded and verified the pinned
  companion MSIs into installer-bundle\payload\.

  Builds three bundles and checks what each one chains and embeds (see
  Assert-Embeds):
    1. RST-Setup.exe          from companions/companions.json: RST.msi plus
                              exactly the shippable companions' pinned assets,
                              one theme checkbox per shippable companion.
    2. RST-Setup-none.exe     from a copy of the registry with nothing
                              shippable: still builds, RST.msi only, no
                              checkboxes.
    3. RST-Setup-Fixture.exe  the smoke's test bundle, from a test registry
                              holding the fixture companion (shippable) and an
                              in_development entry: RST.msi plus the 1.1.0
                              fixture only.
  Also builds the fixture companion at 1.0.0, 1.1.0 and 2.0.0 for the smoke.

  Every bundle also chains RST-Machine.msi and the RemoveUserRst helper
  (feature #17, spec #20 §7: the "Everyone on this PC" path), so each
  expected chain holds RST, RemoveUserRst and RSTMachine besides the
  companions.

  Every build gets its own IntermediateOutputPath: the WiX build is
  incremental on its sources, so variants sharing obj\Release\ could reuse
  each other's compiled output.

  Output (all under installer-bundle\bin\):
    Release\RST-Setup.exe                  the shipped bundle
    ci\test\RST-Setup-Fixture.exe, ci\test\RST-CompanionFixture-<v>.msi
    ci\generated\<bundle>\{Companions.g.wxs,thm.xml}   for review
#>
param(
  [string]$Version = '',
  [string]$RstMsi = 'installer/bin/Release/RST.msi',
  [string]$RstMachineMsi = 'installer/bin/Release/RST-Machine.msi'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$Here = $PSScriptRoot
$Root = (Resolve-Path (Join-Path $Here '..')).Path
$RstMsi = (Resolve-Path $RstMsi).Path
$RstMachineMsi = (Resolve-Path $RstMachineMsi).Path
$Helper = Join-Path $Here 'obj\helper\RST.RemoveUserRst.exe'
$Proj = Join-Path $Here 'RST.Setup.wixproj'
$Gen = Join-Path $Here 'obj\gen'
$Ci = Join-Path $Here 'bin\ci'
$Tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }
foreach ($d in "$Ci\test", "$Ci\generated") { New-Item -ItemType Directory -Force $d | Out-Null }

function Assert([bool]$Cond, [string]$What) {
  if (-not $Cond) { throw "FAIL: $What" }
  Write-Host "PASS: $What"
}
function Invoke-Dotnet([string[]]$DotnetArgs) {
  Write-Host "dotnet $($DotnetArgs -join ' ')"
  & dotnet @DotnetArgs | Out-Host   # never into the caller's return value
  if ($LASTEXITCODE -ne 0) { throw "dotnet $($DotnetArgs -join ' ') exited $LASTEXITCODE" }
}
function Build-Bundle([string]$Name, [string[]]$Extra) {
  Invoke-Dotnet (@('build', $Proj, '-c', 'Release', '--nologo', "-p:RstMsi=$RstMsi", "-p:RstMachineMsi=$RstMachineMsi", "-p:RstBundleVersion=$Version", "-p:OutputName=$Name", "-p:IntermediateOutputPath=obj\$Name\") + $Extra)
  $exe = Join-Path $Here "bin\Release\$Name.exe"
  Assert (Test-Path $exe) "$Name.exe built ($([math]::Round((Get-Item $exe).Length / 1MB, 2)) MB)"
  $keep = Join-Path $Ci "generated\$Name"
  New-Item -ItemType Directory -Force $keep | Out-Null
  Copy-Item (Join-Path $Gen 'Companions.g.wxs'), (Join-Path $Gen 'thm.xml') $keep
  return $exe
}
# What the bundle carries, two ways:
#  - its chain: RST-Setup.exe /layout <dir> /quiet plans every package, and
#    the log names each one ("Planned package: <Id>,"). Embedded payloads
#    stay inside the exe on a layout, so the folder itself proves nothing.
#  - its bytes: `wix burn extract` (the WiX 6.0.2 CLI) unpacks the attached
#    container; the extracted files' sha256 must be exactly the expected
#    packages' (RST.msi as built, each companion's pin): embedded unmodified.
$WixTool = Join-Path $Tmp 'wixtool'
Invoke-Dotnet @('tool', 'install', 'wix', '--version', '6.0.2', '--tool-path', $WixTool)
function Assert-Embeds([string]$Exe, [hashtable]$Expect, [string[]]$Checkboxes) {
  $name = [IO.Path]::GetFileNameWithoutExtension($Exe)
  $dir = Join-Path $Tmp "layout-$name"
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
  $log = Join-Path $Tmp "layout-$name.log"
  $p = Start-Process $Exe -ArgumentList @('/layout', "`"$dir`"", '/quiet', '/log', "`"$log`"") -Wait -PassThru
  if ($p.ExitCode -ne 0) { Get-Content $log -Tail 40 | Write-Host; throw "$name /layout exited $($p.ExitCode)" }
  $planned = @(Select-String -Path $log -Pattern 'Planned package: ([^,]+),' | ForEach-Object { $_.Matches[0].Groups[1].Value } | Sort-Object)
  $want = @($Expect.Keys | Sort-Object)
  Write-Host "$name chain: $($planned -join ', ')"
  Assert (($planned -join '|') -eq ($want -join '|')) "$name chains exactly: $($want -join ', ')"

  $x = Join-Path $Tmp "extract-$name"
  if (Test-Path $x) { Remove-Item $x -Recurse -Force }
  & (Join-Path $WixTool 'wix.exe') burn extract $Exe -o $x | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "wix burn extract $name exited $LASTEXITCODE" }
  $files = @(Get-ChildItem $x -Recurse -File)
  foreach ($f in $files) { Write-Host "  extracted $($f.FullName.Substring($x.Length)) ($($f.Length) bytes)" }
  $got = @($files | ForEach-Object { (Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() } | Sort-Object)
  $wantHash = @($Expect.Values | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object)
  Assert (($got -join '|') -eq ($wantHash -join '|')) "$name embeds exactly these bytes: $(($Expect.GetEnumerator() | Sort-Object Key | ForEach-Object { "$($_.Key)=$($_.Value.Substring(0, 12).ToLowerInvariant())" }) -join ', ')"

  [xml]$thm = Get-Content -Raw -Encoding UTF8 (Join-Path $Ci "generated\$name\thm.xml")
  $boxes = @($thm.SelectNodes("//*[local-name()='Checkbox']") | ForEach-Object { $_.GetAttribute('Name') } | Where-Object { $_ -ne 'EulaAcceptCheckbox' } | Sort-Object)
  Write-Host "$name theme checkboxes: $(if ($boxes) { $boxes -join ', ' } else { '(none)' })"
  Assert (($boxes -join '|') -eq ((@($Checkboxes) | Sort-Object) -join '|')) "$name theme has exactly the checkboxes: $(if ($Checkboxes) { $Checkboxes -join ', ' } else { '(none)' })"
}
function PascalId([string]$id) { -join ($id -split '-' | ForEach-Object { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }) }

# 1. The shipped bundle.
$registry = Join-Path $Root 'companions\companions.json'
$reg = Get-Content -Raw -Encoding UTF8 $registry | ConvertFrom-Json
$ship = @($reg.companions | Where-Object { $_.status -eq 'shippable' })
$exe = Build-Bundle 'RST-Setup' @()
$rstHash = (Get-FileHash $RstMsi -Algorithm SHA256).Hash
# The scope packages every bundle chains (feature #17): RST-Machine.msi as built, and the helper the first build compiled.
Assert (Test-Path $Helper) "the RemoveUserRst helper was built ($Helper)"
$scope = @{ RSTMachine = (Get-FileHash $RstMachineMsi -Algorithm SHA256).Hash; RemoveUserRst = (Get-FileHash $Helper -Algorithm SHA256).Hash }
function With-Scope([hashtable]$Packages) { $all = $scope.Clone(); foreach ($k in $Packages.Keys) { $all[$k] = $Packages[$k] }; return $all }
$expect = With-Scope @{ RST = $rstHash }
foreach ($c in $ship) { $expect[(PascalId $c.id)] = $c.release.sha256 }
Assert-Embeds $exe $expect @($ship | ForEach-Object { "Install$(PascalId $_.id)" })

# 2. Zero shippable companions: still builds, no checkboxes.
$none = Join-Path $Tmp 'companions-none.json'
foreach ($c in $reg.companions) { $c.status = 'in_development'; $c.release = $null }
$reg | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $none
$exe = Build-Bundle 'RST-Setup-none' @("-p:CompanionRegistry=$none")
Assert-Embeds $exe (With-Scope @{ RST = $rstHash }) @()

# 3. Fixture companion builds and the smoke's test bundle.
foreach ($v in '1.0.0', '1.1.0', '2.0.0') {
  Invoke-Dotnet @('build', (Join-Path $Here 'fixture\RST.CompanionFixture.wixproj'), '-c', 'Release', '--nologo', "-p:FixtureVersion=$v", "-p:IntermediateOutputPath=obj\v$v\")
  Copy-Item (Join-Path $Here "fixture\bin\Release\RST-CompanionFixture-$v.msi") "$Ci\test\"
}
$fixtureMsi = Join-Path $Ci 'test\RST-CompanionFixture-1.1.0.msi'
$payload = Join-Path $Tmp 'fixture-payload'
New-Item -ItemType Directory -Force $payload | Out-Null
Copy-Item $fixtureMsi $payload
$testRegistry = [ordered]@{
  schemaVersion = 1
  companions    = @(
    [ordered]@{
      id = 'companion-fixture'; name = 'RST Companion Fixture'; description = 'Test-only fixture companion, never shipped'
      repo = 'jedbjorn/rst-c'; status = 'shippable'; revitYears = @(2025, 2026, 2027)
      addinClientId = '00000000-0000-4000-8000-00000000f1f1'; vendorId = 'DSOS'; defaultSelected = $false
      release = [ordered]@{
        tag = 'ci-fixture'; asset = 'RST-CompanionFixture-1.1.0.msi'
        sha256 = (Get-FileHash $fixtureMsi -Algorithm SHA256).Hash.ToLowerInvariant()
        upgradeCode = 'A0E85F7A-BF17-4052-AB6A-010781D8CF6D'; version = '1.1.0'
      }
    },
    [ordered]@{
      id = 'not-shipped'; name = 'Not Shipped'; description = 'In development: must not be embedded'
      repo = 'jedbjorn/rst-c'; status = 'in_development'; revitYears = @(2027)
      addinClientId = $null; vendorId = 'DSOS'; defaultSelected = $false; release = $null
    }
  )
}
$testRegistryPath = Join-Path $Ci 'test\companions.fixture.json'
$testRegistry | ConvertTo-Json -Depth 8 | Set-Content -Encoding UTF8 $testRegistryPath
$exe = Build-Bundle 'RST-Setup-Fixture' @("-p:CompanionRegistry=$testRegistryPath", "-p:CompanionPayloadDir=$payload")
Assert-Embeds $exe (With-Scope @{ RST = $rstHash; CompanionFixture = (Get-FileHash $fixtureMsi -Algorithm SHA256).Hash }) @('InstallCompanionFixture')
Copy-Item $exe "$Ci\test\"
Write-Host 'ci-bundle-build: done'
