<#
  rst-probe.ps1 — guest helper for feature #16's VM checks (spec #18, AC5). rst-c's own (not
  vendored); Windows PowerShell 5.1. Run from the unpacked RST-Probe artifact folder, as the
  standard user (never elevated) unless a step says otherwise.

    .\rst-probe.ps1 whoami                         user, elevation, and who may write each admin file
    .\rst-probe.ps1 install-user   -Years 2026,2027  per-user RST-Probe (tab "RSTProbe")
    .\rst-probe.ps1 install-bundle                   %AppData% bundle (tab "RSTProbeBundle")
    .\rst-probe.ps1 uninstall                        removes both variants (and any .RSTdisabled copy)
    .\rst-probe.ps1 v30-rename     -Year 2026        renames RstProbe.addin as RST 3.0 did (.RSTdisabled)
    .\rst-probe.ps1 user-off       -Year 2026 [-Guid <id>]   switches an add-in off in the user's
                                                     AddInsSettings.json, as Autodesk's manager would
    .\rst-probe.ps1 admin-lock     -Year 2026 -Option 2 [-Guid <id>] [-Manifest <path>]
                                                     writes AdminSettings.json (1 always on, 2 always off)
    .\rst-probe.ps1 admin-unlock   -Year 2026
    .\rst-probe.ps1 show           -Year 2026        settings, admin file, rst-c's record, manifests
    .\rst-probe.ps1 journal        -Year 2026        RST-Probe / DiRoots lines of the newest journal

  Paths are the ones the Add-Ins Manager spike proved (shared/f15/addins-manager-spike.md).
#>
param(
  [Parameter(Mandatory = $true, Position = 0)]
  [ValidateSet('whoami', 'install-user', 'install-bundle', 'uninstall', 'v30-rename', 'user-off', 'admin-lock', 'admin-unlock', 'show', 'journal')]
  [string]$Action,
  [int[]]$Years = @(2026, 2027),
  [int]$Year = 2026,
  [string]$Guid = 'd4c6e3e0-0000-4000-8000-525354500001',
  [string]$Manifest,
  [int]$Option = 2,
  [string]$Pattern = 'RST-Probe|RstProbe|DiRoots|DisabledUserAddIn'
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$utf8 = New-Object Text.UTF8Encoding($false)

function UserAddins([int]$y) { Join-Path $env:APPDATA "Autodesk\Revit\Addins\$y" }
function SettingsPath([int]$y) { Join-Path $env:APPDATA "Autodesk\Revit\Autodesk Revit $y\AddinsData\AddInsSettings.json" }
function AdminPath([int]$y) {
  if ($y -ge 2027) { "C:\Program Files\Autodesk\Revit\Admin\$y\AdminSettings.json" }
  else { Join-Path $env:ProgramData "Autodesk\RVT $y\Admin\AdminSettings.json" }
}
function CanCreateIn([string]$dir) {
  $probe = Join-Path $dir ('.rst-probe-' + [guid]::NewGuid().ToString('N'))
  try {
    New-Item -ItemType Directory -Force $dir -ErrorAction Stop | Out-Null
    [IO.File]::WriteAllText($probe, 'x'); Remove-Item $probe -Force; return $true
  } catch { return $false }
}
function FileLine([string]$p) {
  if (Test-Path $p) { $i = Get-Item $p; '{0}  len={1} mtime={2}' -f $p, $i.Length, $i.LastWriteTime.ToString('HH:mm:ss') } else { "$p  MISSING" }
}

switch ($Action) {
  'whoami' {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    $admin = (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    "user=$($id.Name) elevated=$admin"
    foreach ($y in 2026, 2027) {
      $dir = Split-Path (AdminPath $y)
      "admin dir $y : $dir  canCreateFileAsThisUser=$(CanCreateIn $dir)"
      if (Test-Path $dir) { icacls $dir }
    }
  }
  'install-user' {
    foreach ($y in $Years) {
      $dst = UserAddins $y
      New-Item -ItemType Directory -Force (Join-Path $dst 'RstProbe') | Out-Null
      Copy-Item (Join-Path $here "user\$y\RstProbe.addin") $dst -Force
      Copy-Item (Join-Path $here "user\$y\RstProbe\RstProbe.dll") (Join-Path $dst 'RstProbe') -Force
      "installed RST-Probe for $y -> $dst"
    }
  }
  'install-bundle' {
    $dst = Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins'
    New-Item -ItemType Directory -Force $dst | Out-Null
    Copy-Item (Join-Path $here 'bundle\RstProbe.bundle') $dst -Recurse -Force
    "installed RstProbe.bundle -> $dst"
  }
  'uninstall' {
    foreach ($y in 2025, 2026, 2027) {
      $d = UserAddins $y
      Remove-Item (Join-Path $d 'RstProbe.addin'), (Join-Path $d 'RstProbe.addin.RSTdisabled') -Force -ErrorAction SilentlyContinue
      Remove-Item (Join-Path $d 'RstProbe') -Recurse -Force -ErrorAction SilentlyContinue
    }
    Remove-Item (Join-Path $env:APPDATA 'Autodesk\ApplicationPlugins\RstProbe.bundle') -Recurse -Force -ErrorAction SilentlyContinue
    'removed both RST-Probe variants'
  }
  'v30-rename' {
    $p = Join-Path (UserAddins $Year) 'RstProbe.addin'
    Rename-Item $p 'RstProbe.addin.RSTdisabled'
    "renamed $p -> RstProbe.addin.RSTdisabled (as RST 3.0 did)"
  }
  'user-off' {
    $p = SettingsPath $Year
    $g = $Guid.ToLowerInvariant()
    if (Test-Path $p) { $t = [IO.File]::ReadAllText($p) } else { $t = '{"DisableAllAddIns":false,"AddInItemSettings":[]}'; New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null }
    $re = '\{"Disabled":(true|false),"GUID":"' + [regex]::Escape($g) + '"'
    if ($t -match $re) { $t = [regex]::Replace($t, $re, '{"Disabled":true,"GUID":"' + $g + '"') }
    else { $t = $t -replace '"AddInItemSettings":\[\]', ('"AddInItemSettings":[{"Disabled":true,"GUID":"' + $g + '"}]'); if ($t -notmatch [regex]::Escape($g)) { $t = $t -replace '"AddInItemSettings":\[', ('"AddInItemSettings":[{"Disabled":true,"GUID":"' + $g + '"},') } }
    [IO.File]::WriteAllText($p, $t, $utf8)
    "switched $g off in $p (the user's own choice; rst-c must leave it off)"
  }
  'admin-lock' {
    $p = AdminPath $Year
    if (-not $Manifest) { $Manifest = Join-Path (UserAddins $Year) 'RstProbe.addin' }
    $json = '{"DisableAllAddIns":false,"AddInManifestAdminSettings":[{"Path":' + (ConvertTo-Json $Manifest) + ',"AddInItemAdminSettings":[{"AddInId":"' + $Guid.ToLowerInvariant() + '","LoadOption":' + $Option + '}]}]}'
    try {
      New-Item -ItemType Directory -Force (Split-Path $p) | Out-Null
      [IO.File]::WriteAllText($p, $json, $utf8)
      "wrote $p"; [IO.File]::ReadAllText($p)
    } catch {
      "COULD NOT WRITE $p as $([Security.Principal.WindowsIdentity]::GetCurrent().Name): $($_.Exception.Message)"
      'Run this step from an elevated session (see vm-steps.md, step L).'
      exit 5
    }
  }
  'admin-unlock' {
    $p = AdminPath $Year
    Remove-Item $p -Force -ErrorAction SilentlyContinue
    "removed $p : exists=$(Test-Path $p)"
  }
  'show' {
    FileLine (SettingsPath $Year)
    if (Test-Path (SettingsPath $Year)) { [IO.File]::ReadAllText((SettingsPath $Year)) }
    FileLine (AdminPath $Year)
    if (Test-Path (AdminPath $Year)) { [IO.File]::ReadAllText((AdminPath $Year)) }
    $ledger = Join-Path $env:APPDATA 'RST\addin_switches.json'
    FileLine $ledger
    if (Test-Path $ledger) { [IO.File]::ReadAllText($ledger) }
    Get-ChildItem (UserAddins $Year) -Filter 'RstProbe.addin*' -ErrorAction SilentlyContinue | ForEach-Object { 'manifest: ' + $_.FullName }
  }
  'journal' {
    $j = Get-ChildItem "$env:LOCALAPPDATA\Autodesk\Revit\Autodesk Revit $Year\Journals\journal.*.txt" | Sort-Object LastWriteTime | Select-Object -Last 1
    "journal: $($j.FullName) mtime=$($j.LastWriteTime.ToString('HH:mm:ss'))"
    Select-String -Path $j.FullName -Pattern $Pattern | ForEach-Object { '{0}: {1}' -f $_.LineNumber, $_.Line.Trim() }
  }
}
