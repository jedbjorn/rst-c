# collect.ps1 -Year  (tools/census): zip the year's census folder and this session's journals for `./sc vm pull`.
param([string]$Year = '2026', [int]$Hours = 12)
$c = "C:\RstC\collect-$Year"; Remove-Item $c -Recurse -Force -ea 0
New-Item -ItemType Directory -Force "$c\journals" | Out-Null
$jd = "$env:LOCALAPPDATA\Autodesk\Revit\Autodesk Revit $Year\Journals"
Get-ChildItem "$jd\journal.*.txt" -ea 0 | ? { $_.LastWriteTime -gt (Get-Date).AddHours(-$Hours) } | % { Copy-Item $_.FullName "$c\journals\" }
if (Test-Path "C:\RstC\census\$Year") { Copy-Item "C:\RstC\census\$Year" "$c\census" -Recurse }
Copy-Item C:\RstC\census\skip.tsv "$c\" -ea 0
Remove-Item "C:\RstC\collect-$Year.zip" -ea 0
Compress-Archive -Path "$c\*" -DestinationPath "C:\RstC\collect-$Year.zip" -Force
"zip " + (Get-Item "C:\RstC\collect-$Year.zip").Length
