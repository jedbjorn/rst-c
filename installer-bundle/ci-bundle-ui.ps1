<#
  ci-bundle-ui.ps1 - drive RST-Setup.exe's own UI once on a CI runner and
  capture it (feature #13 Phase 2, spec #15). Called by _build.yml
  (bundle-smoke, mode UI). Evidence for what a user sees: one unticked
  checkbox per shippable companion on the Install page, and the finish
  page's "companions stay installed" note after install and after
  uninstall.

  Steps (stub Revit.exe files, as in ci-bundle-smoke.ps1):
    1. RST-Setup.exe            Install page -> screenshot; assert every
                                companion checkbox exists and is unticked
    2. tick the license and every companion checkbox, Install -> Success
       page -> screenshot; assert the companions were installed
    3. RST-Setup.exe /uninstall -> Success page -> screenshot; assert RST
       is gone and the companions remain; then remove them (msiexec /x)
  Found through UI Automation, driven by BM_CLICK, captured
  with Graphics.CopyFromScreen of the setup window.
#>
param(
  [Parameter(Mandatory)][string]$Bundle,
  [string]$Registry = 'companions/companions.json',
  [string]$OutDir = $(Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }) 'bundle-ui')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing

$Bundle = (Resolve-Path $Bundle).Path
New-Item -ItemType Directory -Force $OutDir | Out-Null
$LogDir = Split-Path $OutDir
$ProgramFiles64 = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$reg = Get-Content -Raw -Encoding UTF8 $Registry | ConvertFrom-Json
$companions = @($reg.companions | Where-Object { $_.status -eq 'shippable' })
$Installer = New-Object -ComObject WindowsInstaller.Installer

function Assert([bool]$Cond, [string]$What) { if (-not $Cond) { throw "FAIL: $What" }; Write-Host "PASS: $What" }
function Get-RelatedCount([string]$Code) {
  $l = $Installer.GetType().InvokeMember('RelatedProducts', 'GetProperty', $null, $Installer, @($Code))
  return $l.GetType().InvokeMember('Count', 'GetProperty', $null, $l, $null)
}

foreach ($y in '2025', '2026', '2027') {
  $d = Join-Path $ProgramFiles64 "Autodesk\Revit $y"; New-Item -ItemType Directory -Force $d | Out-Null
  Set-Content -Path (Join-Path $d 'Revit.exe') -Value 'rst-c ci stub' -NoNewline
}

$Root = [Windows.Automation.AutomationElement]::RootElement
# The window belongs to the BA, which Burn v5+ runs as its own child
# process, so find it by its caption ("RST Setup"), not by process id.
function Find-Window([string]$Caption = 'RST Setup', [int]$TimeoutSec = 60) {
  $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::NameProperty, $Caption)
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $w = $Root.FindFirst([Windows.Automation.TreeScope]::Children, $cond)
    if ($w) { return $w }
    Start-Sleep -Milliseconds 500
  }
  $top = $Root.FindAll([Windows.Automation.TreeScope]::Children, [Windows.Automation.Condition]::TrueCondition) | ForEach-Object { "'$($_.Current.Name)'" }
  throw "no '$Caption' window after $TimeoutSec s; top-level windows: $($top -join ', ')"
}
# thmutil's controls are plain Win32 BUTTON windows that UI Automation
# reports as Panes (no Toggle/Invoke pattern), so find them by name and
# drive them by window message: BM_GETCHECK reads a checkbox, BM_CLICK
# clicks it (the BA's own click handler then sets the Burn variable).
Add-Type -Namespace RstCi -Name User32 -MemberDefinition @'
[DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
'@
$BM_GETCHECK = 0x00F0; $BM_CLICK = 0x00F5
function Find-Control($Window, [string]$NameLike) {
  $all = $Window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)
  # NativeWindowHandle != 0: the theme's own controls, not the title bar's
  # Close/Minimize buttons, which UI Automation also exposes.
  foreach ($e in $all) { if ($e.Current.Name -like $NameLike -and -not $e.Current.IsOffscreen -and $e.Current.NativeWindowHandle -ne 0) { return $e } }
  return $null
}
function Wait-Control($Window, [string]$NameLike, [int]$TimeoutSec = 300) {
  $deadline = (Get-Date).AddSeconds($TimeoutSec)
  while ((Get-Date) -lt $deadline) {
    $e = Find-Control $Window $NameLike
    if ($e) { return $e }
    Start-Sleep -Seconds 1
  }
  Write-Host "---- all descendants of '$($Window.Current.Name)' (type | name | offscreen | enabled)"
  foreach ($e in $Window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
    Write-Host "  $($e.Current.ControlType.ProgrammaticName) | $($e.Current.Name) | $($e.Current.IsOffscreen) | $($e.Current.IsEnabled)"
  }
  try { Save-Shot $Window 'failure' } catch { Write-Host "screenshot failed: $_" }
  throw "no control '$NameLike' after $TimeoutSec s"
}
function Send([object]$e, [int]$Msg) { [RstCi.User32]::SendMessage([IntPtr]$e.Current.NativeWindowHandle, [uint32]$Msg, [IntPtr]::Zero, [IntPtr]::Zero).ToInt64() }
function Test-Ticked($e) { (Send $e $BM_GETCHECK) -eq 1 }
function Set-Ticked($e) {
  if (-not (Test-Ticked $e)) { Send $e $BM_CLICK | Out-Null }
  Start-Sleep -Milliseconds 300
  Assert (Test-Ticked $e) "ticked '$($e.Current.Name)'"
}
function Invoke-Button($e) { Send $e $BM_CLICK | Out-Null }
function Save-Shot($Window, [string]$Name) {
  Start-Sleep -Milliseconds 800
  try { $Window.SetFocus() } catch { }
  $r = $Window.Current.BoundingRectangle
  $bmp = New-Object Drawing.Bitmap ([int]$r.Width), ([int]$r.Height)
  $g = [Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen([int]$r.X, [int]$r.Y, 0, 0, $bmp.Size)
  $path = Join-Path $OutDir "$Name.png"
  $bmp.Save($path, [Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host "screenshot: $path ($([int]$r.Width)x$([int]$r.Height))"
}
function Show-Texts($Window, [string]$Label) {
  $texts = $Window.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) |
    Where-Object { -not $_.Current.IsOffscreen -and $_.Current.Name } | ForEach-Object { "[$($_.Current.ControlType.ProgrammaticName -replace 'ControlType.', '')] $($_.Current.Name)" }
  Write-Host "---- visible controls [$Label]"; $texts | ForEach-Object { Write-Host "  $_" }; Write-Host '----'
  return , @($texts)
}

# 1. Install page.
$p = Start-Process $Bundle -ArgumentList @('/log', "`"$LogDir\bundle-ui-install.log`"") -PassThru
$w = Find-Window
$eula = Wait-Control $w '*license terms*' 60
Show-Texts $w 'install page' | Out-Null
$boxes = @{}
foreach ($c in $companions) {
  $b = Find-Control $w "$($c.name) $($c.release.version)*"
  Assert ($null -ne $b) "Install page has a '$($c.name) $($c.release.version)' checkbox"
  Assert (-not (Test-Ticked $b)) "'$($b.Current.Name)' starts unticked (defaultSelected false)"
  $boxes[$c.id] = $b
}
Save-Shot $w '1-install-page'

# 2. Tick everything, install, Success page.
Set-Ticked $eula
foreach ($c in $companions) { Set-Ticked $boxes[$c.id] }
Save-Shot $w '2-install-page-ticked'
Invoke-Button (Wait-Control $w 'Install' 10)
$close = Wait-Control $w 'Close' 600
$t = Show-Texts $w 'success page (install)'
Assert (@($t | Where-Object { $_ -like '*stay installed if you uninstall RST*' }).Count -ge 1) 'install finish page says companions stay installed if RST is uninstalled'
Save-Shot $w '3-success-install'
Invoke-Button $close
$p.WaitForExit(30000) | Out-Null
Assert ((Get-RelatedCount '{AF0331FC-5280-492A-94BD-95EDF108BE74}') -eq 1) 'RST installed through the UI'
foreach ($c in $companions) { Assert ((Get-RelatedCount "{$($c.release.upgradeCode)}") -eq 1) "$($c.name) installed through its checkbox" }

# 3. Uninstall through the UI, Success page.
$p = Start-Process $Bundle -ArgumentList @('/uninstall', '/log', "`"$LogDir\bundle-ui-uninstall.log`"") -PassThru
$w = Find-Window
# /uninstall with full UI may first show the Modify page (Repair / Uninstall).
$deadline = (Get-Date).AddSeconds(600); $close = $null
while (-not $close -and (Get-Date) -lt $deadline) {
  $close = Find-Control $w 'Close'
  $un = Find-Control $w 'Uninstall'
  if (-not $close -and $un) { Show-Texts $w 'modify page' | Out-Null; Invoke-Button $un }
  Start-Sleep -Seconds 1
}
Assert ($null -ne $close) 'uninstall reached the finish page'
$t = Show-Texts $w 'success page (uninstall)'
Assert (@($t | Where-Object { $_ -like '*separate products and stay installed*' }).Count -ge 1) 'uninstall finish page says the companions stay installed'
Save-Shot $w '4-success-uninstall'
Invoke-Button $close
$p.WaitForExit(30000) | Out-Null
Assert ((Get-RelatedCount '{AF0331FC-5280-492A-94BD-95EDF108BE74}') -eq 0) 'RST removed'
foreach ($c in $companions) {
  Assert ((Get-RelatedCount "{$($c.release.upgradeCode)}") -eq 1) "$($c.name) still installed"
  $list = $Installer.GetType().InvokeMember('RelatedProducts', 'GetProperty', $null, $Installer, @("{$($c.release.upgradeCode)}"))
  $pc = $list.GetType().InvokeMember('Item', 'GetProperty', $null, $list, @(0))
  $x = Start-Process msiexec.exe -ArgumentList "/x $pc /qn" -Wait -PassThru
  Assert ($x.ExitCode -eq 0) "$($c.name) removed (msiexec /x exit $($x.ExitCode))"
}
exit 0
