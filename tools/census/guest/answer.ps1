# answer.ps1 -Seconds N -Year Y  (tools/census; run in the console session through the Windows MCP PowerShell tool):
# answers Revit's start-up prompts: "Always Load" for the unsigned census add-in, and the 2027 "Hardware
# Acceleration" notice (Close/OK). Dialogs are found by Win32 (top-level windows of Revit's process), then the button
# is pressed with UI Automation inside that dialog only. Stops once the census add-in has logged its start.
param([int]$Seconds = 120, [string]$Year = '2026')
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
if (-not ('CW' -as [type])) {
Add-Type @'
using System; using System.Text; using System.Collections.Generic; using System.Runtime.InteropServices;
public static class CW {
  public delegate bool EP(IntPtr h, IntPtr l);
  [DllImport("user32.dll")] static extern bool EnumWindows(EP f, IntPtr l);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  public static List<string> Wins(int pid) { var o = new List<string>(); EnumWindows((h, l) => { uint p; GetWindowThreadProcessId(h, out p); if (p == pid && IsWindowVisible(h)) { var s = new StringBuilder(512); GetWindowText(h, s, 512); o.Add(h.ToInt64() + "|" + s); } return true; }, IntPtr.Zero); return o; }
}
'@
}
$sw = [Diagnostics.Stopwatch]::StartNew(); $pressed = 0; $quiet = 0
$btnCond = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::ControlTypeProperty, [Windows.Automation.ControlType]::Button)
while ($sw.Elapsed.TotalSeconds -lt $Seconds) {
  $revit = Get-Process Revit -ea 0
  if (-not $revit) { Start-Sleep 2; continue }
  $acted = $false
  foreach ($e in [CW]::Wins($revit.Id)) {
    $h, $title = $e -split '\|', 2
    if ($title -notmatch 'Unsigned|Security|Hardware|Graphics|Acceleration') { continue }
    try { $w = [Windows.Automation.AutomationElement]::FromHandle([IntPtr][long]$h) } catch { continue }
    $want = $null
    foreach ($b in $w.FindAll([Windows.Automation.TreeScope]::Descendants, [Windows.Automation.Condition]::TrueCondition)) {
      if ($b.Current.Name -eq 'Always Load') { $want = $b }
      elseif (-not $want -and $title -match 'Hardware|Graphics|Acceleration' -and $b.Current.Name -match '^(Close|OK|Continue)$') { $want = $b }
    }
    if ($want) {
      $how = ''
      try { $want.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke(); $how = 'Invoke' } catch {
        $bh = $want.Current.NativeWindowHandle
        if ($bh -ne 0) { [CW]::PostMessage([IntPtr]$bh, 0x00F5, [IntPtr]::Zero, [IntPtr]::Zero) | Out-Null; $how = 'BM_CLICK' } }
      if ($how) { "pressed '$($want.Current.Name)' ($how) in '$title' at $(Get-Date -Format HH:mm:ss)"; $pressed++; $acted = $true } else { "could not press '$($want.Current.Name)' in '$title'" }
    } else { "no known button in '$title'" }
  }
  if (-not $acted) { $quiet++ } else { $quiet = 0 }
  $al = Get-Item "C:\RstC\census\$Year\actions.log" -ea 0
  if ($quiet -ge 2 -and $al -and $al.LastWriteTime -gt $revit.StartTime) { break }
  Start-Sleep 2
}
"answer.ps1 done after $([int]$sw.Elapsed.TotalSeconds)s, pressed $pressed; windows: $(([CW]::Wins((Get-Process Revit -ea 0).Id) | % { ($_ -split '\|',2)[1] }) -join ' / ')"
