# status.ps1 -Year  (tools/census watchdog): one compact report: Revit alive/responding, crash reporter, the
# progress file's age and tail, the tool being run, and the newest action lines.
param([string]$Year = '2026', [int]$N = 4)
$yd = "C:\RstC\census\$Year"
$p = Get-Process Revit -ea 0
if ($p) { "REVIT running pid=$($p.Id) resp=$($p.Responding) ws=$([int]($p.WorkingSet64/1MB))MB title='$($p.MainWindowTitle)'" } else { 'REVIT none' }
if (Get-Process cer_dialog, senddmp -ea 0) { 'CRASHREPORTER present' }
$pf = "$yd\progress.txt"
if (Test-Path $pf) { "PROGRESS_AGE $([int]((Get-Date) - (Get-Item $pf).LastWriteTime).TotalSeconds)"; Get-Content $pf -Tail $N | % { "P $_" } } else { 'PROGRESS_AGE -1' }
if (Test-Path C:\RstC\census\current.txt) { "CURRENT " + (Get-Content C:\RstC\census\current.txt) }
if (Test-Path "$yd\actions.log") { Get-Content "$yd\actions.log" -Tail 3 | % { "A $_" } }
if (Test-Path C:\RstC\census\go) { 'GO pending' }
