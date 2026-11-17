# probe.ps1  (tools/census): what the guest holds (re-probe after every reset): Revit years and builds, templates,
# add-in manifests, WebView2 and browser versions.
foreach ($y in 2025, 2026, 2027) {
  $exe = "C:\Program Files\Autodesk\Revit $y\Revit.exe"
  if (Test-Path $exe) { "revit $y exe $((Get-Item $exe).VersionInfo.FileVersion)" } else { "revit $y not installed" }
  $t = "C:\ProgramData\Autodesk\RVT $y"
  if (Test-Path $t) {
    "  project templates: " + ((Get-ChildItem "$t\Templates" -Recurse -Filter *.rte -ea 0 | % { $_.FullName.Replace("$t\Templates\", '') }) -join '; ')
    foreach ($l in 'English-Imperial', 'English') { "  family templates $l : " + (Get-ChildItem "$t\Family Templates\$l" -Recurse -Filter *.rft -ea 0).Count }
  }
  Get-ChildItem "C:\ProgramData\Autodesk\Revit\Addins\$y\*.addin" -ea 0 | % { "  machine addin: $($_.Name)" }
  Get-ChildItem "$env:APPDATA\Autodesk\Revit\Addins\$y\*" -ea 0 | % { "  user addin: $($_.Name)" }
}
Get-ChildItem 'C:\ProgramData\Autodesk\ApplicationPlugins' -Directory -ea 0 | % { "bundle: $($_.Name)" }
$wv = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ea 0
"webview2 runtime: $($wv.pv)"
$b = Get-Item "$env:LOCALAPPDATA\BraveSoftware\Brave-Browser\Application\brave.exe", 'C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe' -ea 0 | select -First 1
"brave: $($b.VersionInfo.FileVersion)"
"screen: " + (Get-CimInstance Win32_VideoController | % { "$($_.CurrentHorizontalResolution)x$($_.CurrentVerticalResolution)" })
