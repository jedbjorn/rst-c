<#
  build-helpers.ps1 - build the three installer helper exes on their own, with the same MSBuild targets an
  MSI or bundle build runs (feature #10, spec #12):
    installer\obj\helper\RST.StartTray.exe            RST.Installer.wixproj -t:RstBuildStartTray (both MSIs)
    installer\obj\machine\helper\RST.EndTrays.exe     RST.Installer.wixproj -t:RstBuildEndTrays -p:RstScope=machine
    installer-bundle\obj\helper\RST.RemoveUserRst.exe RST.Setup.wixproj -t:RstBuildRemoveUserRst (RST-Setup.exe)

  A tag release signs them before any MSI is built (_build.yml, sign-payload); the builds that follow set
  RstPrebuiltHelpers=true so they embed these files instead of compiling their own. tray-selfupdate uses the
  same path on every build, so it is exercised before a tag ever needs it.

  Writes the three full paths to the pipeline (one per line) and, with -GitHubOutput <name>, as a multi-line
  step output of that name. Windows only (the .NET Framework 4 compiler).
#>
param([string]$GitHubOutput = '')

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

$builds = @(
  @{ Proj = 'installer\RST.Installer.wixproj'; Target = 'RstBuildStartTray'; Extra = @(); Out = 'installer\obj\helper\RST.StartTray.exe' },
  @{ Proj = 'installer\RST.Installer.wixproj'; Target = 'RstBuildEndTrays'; Extra = @('-p:RstScope=machine'); Out = 'installer\obj\machine\helper\RST.EndTrays.exe' },
  @{ Proj = 'installer-bundle\RST.Setup.wixproj'; Target = 'RstBuildRemoveUserRst'; Extra = @(); Out = 'installer-bundle\obj\helper\RST.RemoveUserRst.exe' }
)
$paths = @()
foreach ($b in $builds) {
  $out = Join-Path $Root $b.Out
  # A fresh build: an earlier output (signed or not) is never reused.
  if (Test-Path $out) { Remove-Item $out -Force }
  $dotnetArgs = @('msbuild', (Join-Path $Root $b.Proj), '-restore', '-nologo', "-t:$($b.Target)", '-p:Configuration=Release', '-p:RstPrebuiltHelpers=false') + $b.Extra
  Write-Host "dotnet $($dotnetArgs -join ' ')"
  & dotnet @dotnetArgs | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild -t:$($b.Target) exited $LASTEXITCODE" }
  if (-not (Test-Path $out)) { throw "$($b.Target) did not produce $out" }
  $v = (Get-Item $out).VersionInfo
  Write-Host "built $out ($($v.CompanyName) / $($v.ProductName) / $($v.FileDescription))"
  if ($v.CompanyName -ne 'Designs/OS') { throw "$out has CompanyName '$($v.CompanyName)', expected Designs/OS (installer\helper\AssemblyInfo.cs)" }
  $paths += $out
}
if ($GitHubOutput) {
  "$GitHubOutput<<RST_HELPERS_EOF" >> $env:GITHUB_OUTPUT
  $paths >> $env:GITHUB_OUTPUT
  'RST_HELPERS_EOF' >> $env:GITHUB_OUTPUT
}
$paths
