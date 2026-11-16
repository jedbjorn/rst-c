# vendors.ps1 -Year  (tools/census): every Revit add-in manifest the census reads, with VendorId, Name and AddInId
param([string]$Year = '2026')
$dirs = "C:\Program Files\Autodesk\Revit $Year\AddIns", "C:\ProgramData\Autodesk\Revit\Addins\$Year", "$env:APPDATA\Autodesk\Revit\Addins\$Year", 'C:\ProgramData\Autodesk\ApplicationPlugins', "$env:APPDATA\Autodesk\ApplicationPlugins"
foreach ($d in $dirs) {
  Get-ChildItem $d -Recurse -Filter *.addin -ea 0 | % {
    try { [xml]$x = Get-Content $_.FullName -Raw } catch { "BAD|$($_.FullName)"; return }
    foreach ($a in $x.RevitAddIns.AddIn) { '{0}|{1}|{2}{3}|{4}' -f $a.VendorId, $a.Name, $a.AddInId, $a.ClientId, $_.FullName }
  }
}
