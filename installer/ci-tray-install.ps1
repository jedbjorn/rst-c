<#
  rst-c's own: AC6 on Windows CI (feature #17, spec #20 §5; F17 WU4), plus AC7's CI analogue. Runs RST.RuntimeTests'
  TrayInstallFixtureTests: the tray's real install path (InstallCoordinator, VerifiedDownload, msi.dll through
  MsiDatabase and MsiProducts, System32's msiexec, the advisory-only Revit policy) against a fake GitHub that serves
  spec #15's fixture companion MSI (installer-bundle/fixture, built at 1.0.0, 1.1.0 and 2.0.0 by the job):

    refused, each with its logged reason, msiexec never run:
      wrong hash, hash missing from SHA256SUMS.txt, digest mismatch, wrong UpgradeCode, wrong scope (ALLUSERS=1),
      off-allowlist redirect, bad asset name
    installed:   the good fixture 1.0.0 (msiexec exit 0, per user)
    installed:   the upgrade to 1.1.0 proceeds while a Revit.exe stand-in stays alive
    refused:     a downgrade to 1.0.0
    failed:      an upgrade to 2.0.0 that fails on purpose after the old version was removed: nothing installed,
                 "Reinstall previous version (1.1.0)" offered
    reinstalled: 1.1.0 from the cache, with no download

  The test is skipped without RST_TRAY_INSTALL_FIXTURES, so this script also requires the result file it writes.
  Build first: dotnet build src/RST.RuntimeTests/RST.RuntimeTests.csproj -c "Release R25".
#>
param(
  [Parameter(Mandatory)][string]$FixtureDir,
  [string]$Configuration = 'Release R25',
  [string]$OutDir = $(if ($env:RUNNER_TEMP) { Join-Path $env:RUNNER_TEMP 'tray-install' } else { Join-Path $env:TEMP 'tray-install' })
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

New-Item -ItemType Directory -Force $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path
$result = Join-Path $OutDir 'result.json'
Remove-Item $result -ErrorAction SilentlyContinue
foreach ($v in '1.0.0', '1.1.0', '2.0.0') {
  $msi = Join-Path $FixtureDir "RST-CompanionFixture-$v.msi"
  if (-not (Test-Path $msi)) { throw "fixture MSI missing: $msi" }
}
if (@(Get-Process -Name 'Revit' -ErrorAction SilentlyContinue).Count -ne 0) { throw 'a Revit.exe is running on the runner' }

$env:RST_TRAY_INSTALL_FIXTURES = (Resolve-Path $FixtureDir).Path
$env:RST_TRAY_INSTALL_RESULT = $result
& dotnet test src/RST.RuntimeTests/RST.RuntimeTests.csproj -c $Configuration --no-build --nologo `
    --filter 'FullyQualifiedName~RST.RuntimeTests.Tray.TrayInstallFixtureTests' --logger 'console;verbosity=normal'
$code = $LASTEXITCODE
Remove-Item Env:RST_TRAY_INSTALL_FIXTURES, Env:RST_TRAY_INSTALL_RESULT
if ($code -ne 0) { throw "FAIL: TrayInstallFixtureTests exited $code" }
if (-not (Test-Path $result)) { throw 'FAIL: the test was skipped (no result file)' }

$r = Get-Content $result -Raw | ConvertFrom-Json
Write-Host ''
Write-Host 'AC6: the tray''s installs against the fixture companion MSI'
$r.steps | Format-Table step, phase, problem, exitCode, msiexecRuns, previous, @{ n = 'installed'; e = { $_.installed -join ', ' } } -AutoSize |
  Out-String -Width 220 | Write-Host
Write-Host 'Logged reasons:'
$r.steps | Where-Object { $_.reason } | ForEach-Object { Write-Host "  [$($_.step)] $($_.reason)" }
$refusals = @($r.steps | Where-Object { $_.phase -eq 'Refused' })
if ($refusals.Count -ne 8) { throw "FAIL: expected 8 refusals (7 before install + the downgrade), got $($refusals.Count)" }
if (@($refusals | Where-Object { $_.msiexecRuns -ne 0 }).Count -ne 0) { throw 'FAIL: msiexec ran for a refused install' }
Write-Host "PASS: every unverified MSI refused with a logged reason; installs with a background Revit stand-in, failed upgrade and reinstall as expected"
