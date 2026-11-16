# setup.ps1 -Year 2026|2027  (tools/census, spec #34): park RST's own manifest for the session, install the census
# add-in for that year, and record the guest's state. Runs over `./sc vm exec` (non-interactive session).
param([Parameter(Mandatory)][string]$Year)
$ErrorActionPreference = 'Continue'
"whoami=$(whoami) at $(Get-Date -Format o)"
$ad = "$env:APPDATA\Autodesk\Revit\Addins\$Year"
New-Item -ItemType Directory -Force $ad, "C:\RstC\census\addin\$Year", "C:\RstC\census\$Year" | Out-Null
Get-ChildItem "$ad\*" -ea 0 | % { "user addin before: $($_.Name)" }
Get-ChildItem "C:\ProgramData\Autodesk\Revit\Addins\$Year\*.addin" -ea 0 | % { "machine addin: $($_.Name)" }
if (Test-Path "$ad\RST.addin") { Rename-Item "$ad\RST.addin" 'RST.addin.census-off' -Force; "parked RST.addin -> RST.addin.census-off" }
Copy-Item "C:\RstC\stage\$Year\RST.Census.dll" "C:\RstC\census\addin\$Year\" -Force
$m = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>RST.Census</Name>
    <Assembly>C:\RstC\census\addin\$Year\RST.Census.dll</Assembly>
    <AddInId>3c8e2f6a-1b7d-4e22-9a34-220000000034</AddInId>
    <FullClassName>RST.Census.CensusApp</FullClassName>
    <VendorId>RSTC</VendorId>
    <VendorDescription>rst-c Ribbon Tool Census (test seat only, spec #34)</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content "$ad\RST.Census.addin" $m -Encoding UTF8
if (Test-Path C:\RstC\stage\skip.tsv) { Copy-Item C:\RstC\stage\skip.tsv C:\RstC\census\skip.tsv -Force }
"census dll sha256=" + (Get-FileHash "C:\RstC\census\addin\$Year\RST.Census.dll").Hash
$exe = "C:\Program Files\Autodesk\Revit $Year\Revit.exe"
if (Test-Path $exe) { "revit exe $Year file version " + (Get-Item $exe).VersionInfo.FileVersion } else { "revit $Year NOT INSTALLED" }
Get-ChildItem "$ad\*" | % { "user addin now: $($_.Name)" }
