<#
  verify-signatures.ps1 - the Authenticode check for a tag release (feature #10, spec #12; decision #131).

  A file passes when it carries a signature that:
    - Get-AuthenticodeSignature reports Valid (chain to a trusted root, file unmodified),
    - was made by the subject CN "AnJ Arch-Tech LLC" (-Subject),
    - has an RFC 3161 timestamp counter-signature (Get-AuthenticodeSignature's TimeStamperCertificate and
      `signtool verify /pa /all /v`'s "The signature is timestamped"),
    - uses a SHA-256 file digest (signtool's "Hash of file (sha256)", no SHA-1 signature),
    - and, for a PE file, names "Designs/OS" (-Description) as the program (the signature's
      SpcSpOpusInfo, what signtool /d writes and UAC shows).

  Two modes:
    -Files <path>...   check exactly these files (the sign action's check right after it signs:
                       .github/actions/codesign). Always strict.
    -StageRoot <dir> -Msi <msi>... -Bundle <exe>
                       the release check (_build.yml job "Signatures"):
                         1. every PE file in <dir>\R25, R26, R27 and Tray (the staged payload),
                         2. each MSI, and every PE inside it: the File table's files (extracted with
                            `wix msi decompile -x`) and RST's own custom-action binaries in the Binary
                            table (RstStartTrayExe; RstEndTraysExe in RST-Machine.msi). The PE files in
                            each MSI's File table must be exactly the staged ones, byte for byte, so the
                            MSI carries the signed payload and nothing else,
                         3. the bundle, and every PE embedded in it (`wix burn extract`: the
                            RemoveUserRst helper); each -Msi must be embedded byte for byte.
                       With -RequireSigned (tag releases) any file that does not pass fails the run.
                       Without it (push/PR builds, never signed) the same inventory is taken and must
                       be complete, each file's signature is only reported, and the checker itself is
                       exercised on a Microsoft-signed file (signtool.exe), so a tag run is never the
                       first run of this script.

  Third-party PE files (WiX's own custom-action DLLs in the Binary table) are listed, not checked:
  rst-c re-signs nothing it did not build (spec #12, Out of Scope).

  Windows only (PowerShell 7). Needs the Windows SDK's signtool.exe (on GitHub's Windows runners) and
  the WiX 6.0.2 CLI (-Wix, or installed as a .NET tool into $RUNNER_TEMP\wixtool).
#>
param(
  [string[]]$Files = @(),
  [string]$StageRoot = '',
  [string[]]$Msi = @(),
  [string]$Bundle = '',
  [switch]$RequireSigned,
  [string]$Subject = 'AnJ Arch-Tech LLC',
  [string]$Description = 'Designs/OS',
  [string]$Wix = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Tmp = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { $env:TEMP }

$OidSha256 = '2.16.840.1.101.3.4.2.1'
$OidOpusInfo = '1.3.6.1.4.1.311.2.1.12'   # SPC_SP_OPUS_INFO: signtool /d's program name
Add-Type -AssemblyName System.Security.Cryptography.Pkcs

# ---- the checker

function Find-SignTool {
  $kits = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
  $st = Get-ChildItem $kits -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Where-Object { $_.Directory.Name -eq 'x64' -and $_.Directory.Parent.Name -match '^\d+(\.\d+)+$' } |
    Sort-Object { [version]$_.Directory.Parent.Name } -Descending | Select-Object -First 1
  if (-not $st) { throw "signtool.exe (x64) not found under $kits" }
  return $st.FullName
}
$SignTool = Find-SignTool
Write-Host "signtool: $SignTool"

function Test-IsPe([string]$Path) {
  $fs = [IO.File]::OpenRead($Path)
  try { $b = New-Object byte[] 2; $n = $fs.Read($b, 0, 2); return ($n -eq 2 -and $b[0] -eq 0x4D -and $b[1] -eq 0x5A) }
  finally { $fs.Dispose() }
}

# The PKCS #7 signature in a PE file's certificate table (data directory 4), or $null.
function Get-PeSignedCms([string]$Path) {
  $b = [IO.File]::ReadAllBytes($Path)
  $pe = [BitConverter]::ToInt32($b, 0x3C)
  if ($b[$pe] -ne 0x50 -or $b[$pe + 1] -ne 0x45) { throw "$Path is not a PE file" }
  $opt = $pe + 24
  $magic = [BitConverter]::ToUInt16($b, $opt)
  $dirs = $opt + $(if ($magic -eq 0x20B) { 112 } else { 96 })
  $off = [BitConverter]::ToInt32($b, $dirs + 4 * 8)
  $size = [BitConverter]::ToInt32($b, $dirs + 4 * 8 + 4)
  if ($off -le 0 -or $size -le 8) { return $null }
  $len = [BitConverter]::ToInt32($b, $off)              # WIN_CERTIFICATE.dwLength
  $der = New-Object byte[] ($len - 8)
  [Array]::Copy($b, $off + 8, $der, 0, $len - 8)
  $cms = New-Object Security.Cryptography.Pkcs.SignedCms
  $cms.Decode($der)
  return $cms
}

function Test-Contains([byte[]]$Haystack, [byte[]]$Needle) {
  return ([BitConverter]::ToString($Haystack)).Contains([BitConverter]::ToString($Needle))
}

# Every way $Path falls short of the required signature; empty when it passes.
function Get-SignatureProblems([string]$Path, [string]$WantSubject, [string]$WantDescription) {
  $problems = New-Object System.Collections.Generic.List[string]
  $sig = Get-AuthenticodeSignature -LiteralPath $Path
  if ($sig.Status -ne 'Valid') { $problems.Add("status $($sig.Status)") }
  $cn = if ($sig.SignerCertificate) { $sig.SignerCertificate.GetNameInfo([Security.Cryptography.X509Certificates.X509NameType]::SimpleName, $false) } else { '' }
  if ($cn -ne $WantSubject) { $problems.Add("signer '$cn', expected '$WantSubject'") }
  if (-not $sig.TimeStamperCertificate) { $problems.Add('no timestamp counter-signature') }
  if ($sig.Status -eq 'NotSigned') { return , $problems }

  $out = (& $SignTool verify /pa /all /v $Path 2>&1 | Out-String)
  if ($LASTEXITCODE -ne 0) { $problems.Add("signtool verify /pa /all exited $LASTEXITCODE") }
  if ($out -notmatch 'Hash of file \(sha256\)') { $problems.Add('no SHA-256 file digest') }
  if ($out -match 'Hash of file \(sha1\)') { $problems.Add('carries a SHA-1 signature') }
  if ($out -notmatch 'The signature is timestamped') { $problems.Add('signtool: not timestamped') }

  if (Test-IsPe $Path) {
    $cms = Get-PeSignedCms $Path
    if (-not $cms) { $problems.Add('no certificate table') }
    else {
      $si = $cms.SignerInfos[0]
      if ($si.DigestAlgorithm.Value -ne $OidSha256) { $problems.Add("digest algorithm $($si.DigestAlgorithm.Value), expected SHA-256") }
      if ($WantDescription) {
        $opus = @($si.SignedAttributes | Where-Object { $_.Oid.Value -eq $OidOpusInfo })
        $raw = if ($opus) { $opus[0].Values[0].RawData } else { [byte[]]@() }
        $named = (Test-Contains $raw ([Text.Encoding]::BigEndianUnicode.GetBytes($WantDescription))) -or
                 (Test-Contains $raw ([Text.Encoding]::ASCII.GetBytes($WantDescription)))
        if (-not $named) { $problems.Add("description is not '$WantDescription'") }
      }
    }
  }
  return , $problems
}

$Results = New-Object System.Collections.Generic.List[object]
$Inventory = New-Object System.Collections.Generic.List[string]

function Add-Check([string]$Label, [string]$Path) {
  $p = Get-SignatureProblems $Path $Subject $Description
  $Results.Add([pscustomobject]@{ Ok = ($p.Count -eq 0); File = $Label; Detail = $(if ($p.Count) { $p -join '; ' } else { "signed by $Subject, timestamped" }) })
}
function Assert-Inventory([bool]$Cond, [string]$What) {
  if ($Cond) { Write-Host "PASS: $What" } else { Write-Host "FAIL: $What"; $Inventory.Add($What) }
}

function Get-Wix {
  if ($Wix) { return (Resolve-Path $Wix).Path }
  $dir = Join-Path $Tmp 'wixtool'
  $exe = Join-Path $dir 'wix.exe'
  if (-not (Test-Path $exe)) {
    & dotnet tool install wix --version 6.0.2 --tool-path $dir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool install wix exited $LASTEXITCODE" }
  }
  $script:Wix = $exe
  return $exe
}

# ---- MSI tables (the Windows Installer automation object, as installer/ci-smoke.ps1)

$Installer = New-Object -ComObject WindowsInstaller.Installer
function Get-ComProp($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $Obj, $ArgList)
}
function Invoke-ComMethod($Obj, [string]$Name, [object[]]$ArgList = @()) {
  return , $Obj.GetType().InvokeMember($Name, [Reflection.BindingFlags]::InvokeMethod, $null, $Obj, $ArgList)
}
function Get-MsiQuery([string]$Path, [string]$Sql, [int]$Cols) {
  $db = Invoke-ComMethod $Installer 'OpenDatabase' @($Path, 0)
  $view = Invoke-ComMethod $db 'OpenView' @($Sql)
  Invoke-ComMethod $view 'Execute' | Out-Null
  $rows = @()
  while ($rec = Invoke-ComMethod $view 'Fetch') {
    $row = @(); for ($c = 1; $c -le $Cols; $c++) { $row += Get-ComProp $rec 'StringData' @($c) }
    $rows += , $row
  }
  Invoke-ComMethod $view 'Close' | Out-Null
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($view)
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($db)
  return , $rows
}

function Get-Sha256([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

# ---- mode 1: exactly these files

if ($Files) {
  foreach ($f in $Files) {
    $full = (Resolve-Path -LiteralPath $f).Path
    Add-Check $full $full
  }
  $Results | Format-Table -AutoSize -Wrap | Out-Host
  $bad = @($Results | Where-Object { -not $_.Ok })
  if ($bad) { Write-Host "FAIL: $($bad.Count) of $($Results.Count) file(s) not signed as required"; exit 1 }
  Write-Host "PASS: all $($Results.Count) file(s) signed by $Subject, SHA-256, timestamped"
  exit 0
}

# ---- mode 2: the release

if (-not $StageRoot -or -not $Msi -or -not $Bundle) { throw 'pass -Files, or -StageRoot, -Msi and -Bundle' }
$StageRoot = (Resolve-Path $StageRoot).Path
$Msi = @($Msi | ForEach-Object { (Resolve-Path $_).Path })
$Bundle = (Resolve-Path $Bundle).Path
$wixExe = Get-Wix

# The checker itself, on a file Microsoft signed (the Windows SDK's signtool.exe: SHA-256, timestamped): a
# PR run exercises every branch a tag run takes for a good signature, and the unsigned payload shows it
# rejects. (Not dotnet.exe: its signer is CN=.NET.)
if (-not $RequireSigned) {
  $p = Get-SignatureProblems $SignTool 'Microsoft Corporation' ''
  Assert-Inventory ($p.Count -eq 0) "checker self-test: $SignTool passes as signed by Microsoft Corporation$(if ($p.Count) { " ($($p -join '; '))" })"
}

# 1. The staged payload: every PE file, each RST's own (decision #33).
$stageHashes = @{}
$expectStaged = @{
  R25  = @('addins\RST.Bootstrap.dll', 'app\RST.Core.dll', 'app\RST.Engine.dll', 'app\RST.UI.dll')
  R26  = @('addins\RST.Bootstrap.dll', 'app\RST.Core.dll', 'app\RST.Engine.dll', 'app\RST.UI.dll')
  R27  = @('addins\RST.Bootstrap.dll', 'app\RST.Core.dll', 'app\RST.Engine.dll', 'app\RST.UI.dll')
  Tray = @('RST.Core.dll', 'RST.Tray.dll', 'RST.Tray.exe', 'RST.UI.dll')
}
foreach ($tree in 'R25', 'R26', 'R27', 'Tray') {
  $root = Join-Path $StageRoot $tree
  $pes = @(Get-ChildItem $root -Recurse -File | Where-Object { $_.Extension -in '.dll', '.exe' } | Sort-Object FullName)
  $rel = @($pes | ForEach-Object { $_.FullName.Substring($root.Length + 1) })
  foreach ($want in $expectStaged[$tree]) { Assert-Inventory ($rel -contains $want) "stage $tree has $want" }
  foreach ($f in $pes) {
    Assert-Inventory ($f.Name -like 'RST.*') "stage $tree\$($f.FullName.Substring($root.Length + 1)) is RST's own"
    Add-Check "stage $tree\$($f.FullName.Substring($root.Length + 1))" $f.FullName
    $stageHashes[(Get-Sha256 $f.FullName)] = "$tree\$($f.Name)"
  }
}

# 2. Each MSI and every first-party PE inside it.
foreach ($m in $Msi) {
  $name = Split-Path $m -Leaf
  Add-Check $name $m
  $x = Join-Path $Tmp "verify-$([IO.Path]::GetFileNameWithoutExtension($m))"
  & $wixExe msi decompile $m -x $x -o "$x.wxs" | Out-Host
  if ($LASTEXITCODE -ne 0) { throw "wix msi decompile $name exited $LASTEXITCODE" }

  $msiHashes = @{}
  foreach ($row in (Get-MsiQuery $m 'SELECT `File`, `FileName` FROM `File`' 2)) {
    $long = ($row[1] -split '\|')[-1]
    if ($long -notmatch '\.(dll|exe)$') { continue }
    $path = Join-Path $x "File\$($row[0])"
    Assert-Inventory (Test-Path -LiteralPath $path) "$name`: $long ($($row[0])) extracted"
    if (-not (Test-Path -LiteralPath $path)) { continue }
    Assert-Inventory ($long -like 'RST.*') "$name`: $long is RST's own"
    Add-Check "$name > $long ($($row[0]))" $path
    $msiHashes[(Get-Sha256 $path)] = $long
  }
  $same = (@($msiHashes.Keys | Sort-Object) -join '|') -eq (@($stageHashes.Keys | Sort-Object) -join '|')
  Assert-Inventory $same "$name`: its $($msiHashes.Count) distinct PE files are exactly the $($stageHashes.Count) staged ones, byte for byte"

  $binaries = @((Get-MsiQuery $m 'SELECT `Name` FROM `Binary`' 1) | ForEach-Object { $_[0] })
  $want = @('RstStartTrayExe') + $(if ($name -like '*Machine*') { @('RstEndTraysExe') } else { @() })
  foreach ($w in $want) { Assert-Inventory ($binaries -contains $w) "$name`: Binary table has $w" }
  foreach ($b in $binaries) {
    $path = Join-Path $x "Binary\$b"
    if (-not (Test-Path -LiteralPath $path) -or -not (Test-IsPe $path)) { continue }
    if ($b -like 'Rst*') { Add-Check "$name > Binary $b" $path }
    else { Write-Host "  $name > Binary ${b}: third-party (WiX), not checked" }
  }
}

# 3. The bundle, and every PE embedded in it; the MSIs above embedded byte for byte.
$bundleName = Split-Path $Bundle -Leaf
Add-Check $bundleName $Bundle
$x = Join-Path $Tmp 'verify-bundle'
if (Test-Path $x) { Remove-Item $x -Recurse -Force }
& $wixExe burn extract $Bundle -o $x | Out-Host
if ($LASTEXITCODE -ne 0) { throw "wix burn extract $bundleName exited $LASTEXITCODE" }
$embedded = @(Get-ChildItem $x -Recurse -File)
$embeddedHashes = @($embedded | ForEach-Object { Get-Sha256 $_.FullName })
foreach ($m in $Msi) { Assert-Inventory ($embeddedHashes -contains (Get-Sha256 $m)) "$bundleName embeds $(Split-Path $m -Leaf) byte for byte" }
$embeddedPes = @($embedded | Where-Object { Test-IsPe $_.FullName })
Assert-Inventory ($embeddedPes.Count -ge 1) "$bundleName embeds a PE file (the RemoveUserRst helper)"
foreach ($f in $embeddedPes) { Add-Check "$bundleName > $($f.FullName.Substring($x.Length + 1))" $f.FullName }

# ---- the result

$Results | Format-Table -AutoSize -Wrap | Out-Host
$bad = @($Results | Where-Object { -not $_.Ok })
if ($Inventory.Count) { Write-Host "FAIL: the inventory is wrong:"; $Inventory | ForEach-Object { Write-Host "  $_" }; exit 1 }
if ($RequireSigned) {
  if ($bad) { Write-Host "FAIL: $($bad.Count) of $($Results.Count) file(s) not signed as required"; exit 1 }
  Write-Host "PASS: all $($Results.Count) files signed by $Subject (SHA-256, timestamped, '$Description')"
} else {
  Write-Host "Unsigned build (not a tag release): $($Results.Count) files inventoried, $($Results.Count - $bad.Count) signed. Nothing is required to be."
}
exit 0
