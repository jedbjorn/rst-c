<#
  ci-machine-layout.ps1 - RST-Machine.msi's installed layout, for the CI
  smokes that check it (feature #17, spec #20 §7, AC9; F17 WU5). Dot-sourced
  by installer/ci-machine-smoke.ps1 and installer-bundle/ci-bundle-smoke.ps1
  after they define Assert.

    C:\Program Files\RST\R<NN>\RST.Bootstrap.dll        the bootstrap
    C:\Program Files\RST\R<NN>\app\RST.Engine.dll ...  the engine
    %ProgramData%\Autodesk\Revit\Addins\2025|2026\RST.addin
    C:\Program Files\Autodesk\Revit\Addins\2027\RST.addin
        <Assembly> = the absolute path of that year's bootstrap
    C:\Program Files\RST\Tray\RST.Tray.exe              the tray
    HKLM\Software\Microsoft\Windows\CurrentVersion\Run  RST = "<tray>" --startup
#>

$MachineUpgradeCode = '{79C25959-C28A-4ED8-8984-85ED525476C3}'
$MachineProgramFiles = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
$MachineProgramData = $env:ProgramData
$MachineRoot = Join-Path $MachineProgramFiles 'RST'
$MachineTrayExe = Join-Path $MachineRoot 'Tray\RST.Tray.exe'
$MachineRunKey = 'HKLM:\Software\Microsoft\Windows\CurrentVersion\Run'
$MachineYears = [ordered]@{ '2025' = 'R25'; '2026' = 'R26'; '2027' = 'R27' }

function Get-MachineManifest([string]$Year) {
  if ([int]$Year -ge 2027) { return Join-Path $MachineProgramFiles "Autodesk\Revit\Addins\$Year\RST.addin" }
  return Join-Path $MachineProgramData "Autodesk\Revit\Addins\$Year\RST.addin"
}
function Get-MachineBootstrap([string]$Year) { return Join-Path $MachineRoot "$($MachineYears[$Year])\RST.Bootstrap.dll" }
function Get-MachineEngineDir([string]$Year) { return Join-Path $MachineRoot "$($MachineYears[$Year])\app" }
function Get-MachineRunValue {
  $k = Get-ItemProperty -Path $MachineRunKey -Name 'RST' -ErrorAction SilentlyContinue
  if ($k) { return $k.RST } else { return $null }
}

# Exactly the given years are installed per machine: manifest (with the absolute <Assembly> of its own year's
# bootstrap), bootstrap and engine present; the other years have none of them.
function Assert-MachineYears([string[]]$Present, [string]$Label) {
  foreach ($y in $MachineYears.Keys) {
    $manifest = Get-MachineManifest $y
    $boot = Get-MachineBootstrap $y
    $engine = Get-MachineEngineDir $y
    if ($Present -contains $y) {
      Assert (Test-Path $manifest -PathType Leaf) "[$Label] $manifest present"
      [xml]$x = Get-Content -Raw $manifest
      $assembly = [string]$x.RevitAddIns.AddIn.Assembly
      Assert ($assembly -eq $boot) "[$Label] Revit $y manifest's <Assembly> is the absolute bootstrap path $boot (got '$assembly')"
      Assert (Test-Path $boot -PathType Leaf) "[$Label] $boot present"
      foreach ($f in 'RST.Engine.dll', 'RST.UI.dll', 'RST.Core.dll', 'RST.Engine.deps.json') {
        Assert (Test-Path (Join-Path $engine $f) -PathType Leaf) "[$Label] $engine\$f present"
      }
      Assert (-not (Test-Path (Join-Path (Split-Path $boot) 'RST.Engine.dll'))) "[$Label] the engine is in app\ beside the bootstrap, not with it"
    } else {
      Assert (-not (Test-Path $manifest)) "[$Label] no $manifest (Revit $y not detected)"
      Assert (-not (Test-Path $boot)) "[$Label] no $boot"
      Assert (-not (Test-Path $engine)) "[$Label] no $engine"
    }
  }
  if ($Present.Count -gt 0) { Assert-MachineNoForeignDlls $Label }
}

# Decision #33: Program Files\RST (each year's bootstrap and app\, and the tray) holds only RST's own DLLs.
function Assert-MachineNoForeignDlls([string]$Label) {
  $dlls = @(Get-ChildItem $MachineRoot -Recurse -Filter *.dll -ErrorAction SilentlyContinue)
  $foreign = @($dlls | Where-Object { $_.Name -notmatch '^RST(\..+)?\.dll$' } | ForEach-Object { $_.FullName.Substring($MachineRoot.Length + 1) })
  Write-Host "[$Label] $MachineRoot DLLs: $(($dlls | ForEach-Object { $_.FullName.Substring($MachineRoot.Length + 1) }) -join ', ')"
  Assert ($dlls.Count -gt 0 -and $foreign.Count -eq 0) "[$Label] $MachineRoot ships no third-party DLL (decision #33)$(if ($foreign) { ': ' + ($foreign -join ', ') })"
}

function Assert-MachineTray([bool]$Present, [string]$Label) {
  $run = Get-MachineRunValue
  if ($Present) {
    Assert (Test-Path $MachineTrayExe -PathType Leaf) "[$Label] $MachineTrayExe present"
    Assert ($run -eq "`"$MachineTrayExe`" --startup") "[$Label] HKLM Run value RST = `"$MachineTrayExe`" --startup (got '$run')"
  } else {
    Assert (-not (Test-Path $MachineTrayExe)) "[$Label] $MachineTrayExe removed"
    Assert ($null -eq $run) "[$Label] HKLM Run value RST removed"
  }
}

# Nothing of RST-Machine.msi is left: files, Program Files\RST, manifests, Run value.
function Assert-MachineGone([string]$Label) {
  Assert-MachineYears @() $Label
  Assert-MachineTray $false $Label
  Assert (-not (Test-Path $MachineRoot)) "[$Label] $MachineRoot removed"
}
