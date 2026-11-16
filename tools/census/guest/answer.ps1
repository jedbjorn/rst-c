# answer.ps1 -Seconds N  (tools/census; run in the console session through the Windows MCP PowerShell tool):
# answers Revit's start-up prompts with UI Automation: "Always Load" for the unsigned census add-in, and the
# 2027 "Hardware Acceleration disabled" notice (Close/OK). Prints what it pressed; stops once Revit's main window is up
# and nothing is left to answer.
param([int]$Seconds = 120, [string]$Year = '2026')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$root = [Windows.Automation.AutomationElement]::RootElement
$sw = [Diagnostics.Stopwatch]::StartNew(); $pressed = 0; $quiet = 0
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  $revit = Get-Process Revit -ea 0
  if (-not $revit) { Start-Sleep 2; continue }
  $acted = $false
  $cond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ProcessIdProperty, $revit.Id)
  foreach ($w in $root.FindAll([Windows.Automation.TreeScope]::Children, $cond)) {
    $title = $w.Current.Name
    $btnCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
    $buttons = $w.FindAll([Windows.Automation.TreeScope]::Descendants, $btnCond)
    $want = $null
    foreach ($b in $buttons) { if ($b.Current.Name -eq 'Always Load') { $want = $b } }
    if (-not $want -and ($title -match 'Hardware|Graphics' -or ($w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition) | ? { $_.Current.Name -match 'Hardware Acceleration' }))) {
      foreach ($b in $buttons) { if ($b.Current.Name -match '^(Close|OK|Continue)$') { $want = $b } }
    }
    if ($want) {
      try { $want.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); "pressed '$($want.Current.Name)' in '$title' at $(Get-Date -Format HH:mm:ss)"; $pressed++; $acted = $true } catch { "invoke failed in '$title': $_" }
    }
  }
  if (-not $acted) { $quiet++ } else { $quiet = 0 }
  $al = Get-Item "C:\RstC\census\$Year\actions.log" -ea 0
  if ($quiet -ge 2 -and $al -and $al.LastWriteTime -gt $revit.StartTime) { break }
  Start-Sleep 2
}
"answer.ps1 done after $([int]$sw.Elapsed.TotalSeconds)s, pressed $pressed; revit title '$((Get-Process Revit -ea 0).MainWindowTitle)'"
