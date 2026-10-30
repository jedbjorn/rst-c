<#
  fetch-companions.ps1 - download each shippable companion's pinned MSI and
  verify it against the registry (feature #13 Phase 2, spec #15, AC3).

  For every companion whose status is "shippable":
    gh release download <tag> -R <repo> -p <asset> -D <OutDir>
  then:
    - the asset must exist on that release (missing -> fail);
    - its sha256 must equal the pin (mismatch -> fail; never "fix" a pin to
      make this pass, see companions/README.md);
    - on Windows, the MSI's own UpgradeCode and ProductVersion must equal
      the pin's upgradeCode and version (read through the Windows Installer
      COM object).
  Any failure exits non-zero, which fails the CI bundle job.

  Needs the GitHub CLI (gh) with a token in GH_TOKEN (CI: github.token;
  the companion repos are public). Runs under Windows PowerShell 5.1 and
  PowerShell 7.
#>
param(
  [string]$Registry = (Join-Path $PSScriptRoot '..\companions\companions.json'),
  [string]$OutDir = (Join-Path $PSScriptRoot 'payload')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$reg = Get-Content -Raw -Encoding UTF8 -Path $Registry | ConvertFrom-Json
$shippable = @($reg.companions | Where-Object { $_.status -eq 'shippable' })
Write-Host "fetch-companions: $($shippable.Count) shippable companion(s) in $Registry"
if (-not (Test-Path $OutDir)) { New-Item -ItemType Directory -Force $OutDir | Out-Null }

$isWindows_ = [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
$installer = if ($isWindows_) { New-Object -ComObject WindowsInstaller.Installer } else { $null }
function Get-MsiProperty([string]$Path, [string]$Name) {
  $db = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($Path, 0))
  $view = $db.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $db, @("SELECT ``Value`` FROM ``Property`` WHERE ``Property`` = '$Name'"))
  [void]$view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null)
  $rec = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)
  $value = if ($rec) { $rec.GetType().InvokeMember('StringData', 'GetProperty', $null, $rec, @(1)) } else { $null }
  [void]$view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  return $value
}

$failures = @()
foreach ($c in $shippable) {
  $r = $c.release
  if ($null -eq $r) { $failures += "$($c.id): shippable but has no release"; continue }
  $dest = Join-Path $OutDir $r.asset
  if (Test-Path $dest) { Remove-Item $dest -Force }
  Write-Host "fetch-companions: $($c.id): gh release download $($r.tag) -R $($c.repo) -p $($r.asset)"
  & gh release download $r.tag -R $c.repo -p $r.asset -D $OutDir --clobber
  if ($LASTEXITCODE -ne 0 -or -not (Test-Path $dest -PathType Leaf)) {
    $failures += "$($c.id): asset $($r.asset) not found on release $($r.tag) of $($c.repo) (gh exit $LASTEXITCODE)"
    continue
  }
  $actual = (Get-FileHash -Path $dest -Algorithm SHA256).Hash.ToLowerInvariant()
  if ($actual -ne $r.sha256.ToLowerInvariant()) {
    $failures += "$($c.id): sha256 mismatch for $($r.asset): pinned $($r.sha256), downloaded $actual"
    continue
  }
  Write-Host "fetch-companions: $($c.id): sha256 $actual matches the pin"
  if ($installer) {
    $code = ([string](Get-MsiProperty $dest 'UpgradeCode')).Trim('{', '}')
    $version = [string](Get-MsiProperty $dest 'ProductVersion')
    if ($code -ne $r.upgradeCode) { $failures += "$($c.id): MSI UpgradeCode {$code} != pinned $($r.upgradeCode)" }
    if ($version -ne $r.version) { $failures += "$($c.id): MSI ProductVersion $version != pinned $($r.version)" }
    Write-Host "fetch-companions: $($c.id): MSI UpgradeCode {$code}, ProductVersion $version"
  }
}

if ($failures) {
  $failures | ForEach-Object { Write-Host "::error::fetch-companions: $_" }
  throw "fetch-companions: $($failures.Count) companion pin(s) failed verification"
}
Write-Host "fetch-companions: all $($shippable.Count) pin(s) verified into $OutDir"
