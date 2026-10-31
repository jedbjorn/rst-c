<#
  ci-tray-start.ps1 - checks for the tray the installers start after a
  successful install, repair or upgrade (SC-044, decision #45; Tray.wxs
  RstStartTray, helper\StartTray.cs). Dot-sourced by installer/ci-smoke.ps1,
  installer/ci-machine-smoke.ps1, installer/ci-tray-selfupdate.ps1 and
  installer-bundle/ci-bundle-smoke.ps1 after they define Assert.

    Get-TokenFacts <pid>      the process's user SID, elevation (TokenElevation,
                              TokenElevationType) and integrity level
    Get-UserShell             this user's Explorer in this session, with its facts
    Show-RunnerTokens         OBSERVE lines: this process's and the shell's tokens
    Wait-SettledTray <label>  exactly one RST.Tray process once every launch has
                              decided (it logged "starting (pid N," or exited, as
                              the self-update smoke counts; SC-022); returns it
    Assert-StartedTray        that tray: the expected exe, --startup, this user,
                              this session (not 0), no more rights than this
                              user's Explorer (unelevated under UAC), no window
                              (so no first-run notice, decision #65)
    Assert-NoTray <label>     no RST.Tray process within a grace period
    Get-StartTrayLog <since>  the RST-StartTray-<time>-<pid>.log files written
                              since then (the installer's TEMP; as SYSTEM
                              C:\Windows\Temp or C:\Windows\SystemTemp), printed
#>

if (-not ('RstCiToken' -as [type])) {
  Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class RstCiToken
{
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);

    private static IntPtr Info(IntPtr token, int infoClass)
    {
        int length;
        GetTokenInformation(token, infoClass, IntPtr.Zero, 0, out length);
        if (length <= 0) return IntPtr.Zero;
        IntPtr buffer = Marshal.AllocHGlobal(length);
        if (GetTokenInformation(token, infoClass, buffer, length, out length)) return buffer;
        Marshal.FreeHGlobal(buffer);
        return IntPtr.Zero;
    }

    private static string Sid(IntPtr token, int infoClass)
    {
        IntPtr b = Info(token, infoClass);
        if (b == IntPtr.Zero) return "";
        try
        {
            IntPtr text;
            if (!ConvertSidToStringSidW(Marshal.ReadIntPtr(b), out text)) return "";
            try { return Marshal.PtrToStringUni(text); } finally { LocalFree(text); }
        }
        finally { Marshal.FreeHGlobal(b); }
    }

    private static int Int(IntPtr token, int infoClass)
    {
        IntPtr b = Info(token, infoClass);
        if (b == IntPtr.Zero) return -1;
        try { return Marshal.ReadInt32(b); } finally { Marshal.FreeHGlobal(b); }
    }

    /// <summary>"sid|elevated|elevationType|integritySid", or null when the process cannot be read.</summary>
    public static string Facts(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            IntPtr t;
            if (!OpenProcessToken(h, 0x8 /* TOKEN_QUERY */, out t)) return null;
            try { return Sid(t, 1) + "|" + Int(t, 20) + "|" + Int(t, 18) + "|" + Sid(t, 25); }
            finally { CloseHandle(t); }
        }
        finally { CloseHandle(h); }
    }
}
'@
}

$RstCiSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
$RstCiSession = (Get-Process -Id $PID).SessionId
$RstCiTrayLogs = Join-Path $env:APPDATA 'RST\logs'

function Get-TokenFacts([int]$ProcessId) {
  $raw = [RstCiToken]::Facts($ProcessId)
  if (-not $raw) { return $null }
  $p = $raw -split '\|'
  $type = switch ([int]$p[2]) { 1 { 'default' } 2 { 'full' } 3 { 'limited' } default { "unknown ($($p[2]))" } }
  return [pscustomobject]@{
    Sid           = $p[0]
    Elevated      = ([int]$p[1] -eq 1)
    ElevationType = $type
    Integrity     = [int]($p[3] -replace '^.*-', '')   # 8192 medium, 12288 high, 16384 system
  }
}
function Format-Facts($f) {
  if (-not $f) { return '(token not readable)' }
  return "user $($f.Sid), elevated $(if ($f.Elevated) { 'yes' } else { 'no' }), token $($f.ElevationType), integrity $($f.Integrity)"
}

# This user's Explorer in this session (the shell the tray is started through), or $null.
function Get-UserShell {
  foreach ($e in @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe'")) {
    if ($e.SessionId -ne $RstCiSession) { continue }
    $f = Get-TokenFacts $e.ProcessId
    if ($f -and $f.Sid -eq $RstCiSid) { return [pscustomobject]@{ ProcessId = $e.ProcessId; Facts = $f } }
  }
  return $null
}

function Show-RunnerTokens {
  Write-Host "OBSERVE: this script (pid $PID, session $RstCiSession): $(Format-Facts (Get-TokenFacts $PID))"
  $shell = Get-UserShell
  if ($shell) { Write-Host "OBSERVE: this user's Explorer: pid $($shell.ProcessId), $(Format-Facts $shell.Facts)" }
  else { Write-Host "OBSERVE: no Explorer of this user in session $RstCiSession" }
}

function Show-TrayProcesses([string]$Label) {
  foreach ($w in @(Get-CimInstance Win32_Process -Filter "Name='RST.Tray.exe'")) {
    $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($w.ParentProcessId)" -ErrorAction SilentlyContinue
    Write-Host "  [$Label] RST.Tray pid $($w.ProcessId) session $($w.SessionId) parent $($w.ParentProcessId) $(if ($parent) { $parent.Name }) '$($w.CommandLine)'; $(Format-Facts (Get-TokenFacts $w.ProcessId))"
  }
}

# True once tray pid $Id has logged its start ("RST tray <version> starting (pid N, ...)").
function Test-TrayStarted([int]$Id) {
  foreach ($f in @(Get-ChildItem $RstCiTrayLogs -Filter 'tray_*.log' -ErrorAction SilentlyContinue)) {
    if (@(Get-Content $f.FullName -ErrorAction SilentlyContinue | Where-Object { $_.Contains("starting (pid $Id,") }).Count -gt 0) { return $true }
  }
  return $false
}

# Exactly one RST.Tray process once every launch has decided (SC-022), still the same one 5 s later.
function Wait-SettledTray([string]$Label, [int]$Seconds = 90) {
  $deadline = (Get-Date).AddSeconds($Seconds)
  $id = 0
  while ($true) {
    $t = @(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue)
    if ($t.Count -eq 1 -and (Test-TrayStarted $t[0].Id)) { $id = $t[0].Id; break }
    if ((Get-Date) -gt $deadline) {
      Show-TrayProcesses $Label
      Assert $false "[$Label] exactly one RST.Tray runs (got $($t.Count) after $Seconds s; none settled)"
    }
    Start-Sleep -Milliseconds 500
  }
  Start-Sleep -Seconds 5   # any second launch has decided by now
  $t = @(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue)
  Show-TrayProcesses $Label
  Assert ($t.Count -eq 1 -and $t[0].Id -eq $id) "[$Label] exactly one RST.Tray process (pid $id; got $($t.Count))"
  $p = $t[0]
  try { $null = $p.Handle } catch { }   # a handle, so its exit code stays readable
  return $p
}

# The tray the installer started: the expected exe, hidden, this user, this session, no more rights than the shell.
# "Not a child of msiexec" holds with UAC off too: it is the parent, not the token, that tells RstStartTray's start
# (through Explorer) from a start by msiexec itself (Restart Manager's restart, which SC-044 review B1 disabled).
function Assert-StartedTray($Proc, [string]$Exe, [string]$Label) {
  $w = Get-CimInstance Win32_Process -Filter "ProcessId=$($Proc.Id)"
  Assert ($null -ne $w) "[$Label] the tray (pid $($Proc.Id)) is running"
  $f = Get-TokenFacts $Proc.Id
  $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($w.ParentProcessId)" -ErrorAction SilentlyContinue
  Write-Host "OBSERVE: [$Label] tray pid $($Proc.Id), parent $($w.ParentProcessId) $(if ($parent) { $parent.Name } else { '(gone)' }), '$($w.CommandLine)'; $(Format-Facts $f)"
  Assert ($w.ExecutablePath -eq $Exe) "[$Label] it runs $Exe (got $($w.ExecutablePath))"
  Assert ($w.CommandLine -match '(^|\s)--startup(\s|$)') "[$Label] it was started hidden (--startup)"
  Assert ($null -ne $f -and $f.Sid -eq $RstCiSid) "[$Label] it runs as this user ($RstCiSid)"
  Assert ($w.SessionId -eq $RstCiSession -and $w.SessionId -ne 0) "[$Label] it runs in this user's session $RstCiSession, not session 0"
  $shell = Get-UserShell
  $checks = @()
  if ($shell -and -not $shell.Facts.Elevated) {
    $checks += , @((-not $f.Elevated -and $f.ElevationType -ne 'full' -and $f.Integrity -le 8192), "[$Label] it is not elevated (token $($f.ElevationType), integrity $($f.Integrity)), like this user's Explorer")
  } elseif ($shell) {
    # No split token for this user (UAC off, or the built-in Administrator): Explorer itself has the full token, and the
    # tray at sign-in gets that same token; there is no UIPI to lose to.
    Write-Host "OBSERVE: [$Label] this user's Explorer itself has a full, unsplit token (no UAC split token for this account)"
    $checks += , @(($f.Integrity -le $shell.Facts.Integrity), "[$Label] it has no more rights than this user's Explorer (integrity $($f.Integrity) <= $($shell.Facts.Integrity))")
  } else {
    $checks += , @((-not $f.Elevated), "[$Label] it is not elevated (no Explorer of this user to compare with)")
  }
  $checks += , @(($null -eq $parent -or $parent.Name -ne 'msiexec.exe'), "[$Label] it is not a child of msiexec")
  foreach ($c in $checks) { Assert $c[0] $c[1] }
  Start-Sleep -Seconds 2
  $Proc.Refresh()
  # Decision #65: no window also means no first-run notice (the callers seed no "updateNoticeShownUtc").
  Assert ($Proc.MainWindowHandle -eq [IntPtr]::Zero) "[$Label] it shows no window (no first-run update-check notice either, decision #65)"
}

function Assert-NoTray([string]$Label, [int]$GraceSeconds = 10) {
  $deadline = (Get-Date).AddSeconds($GraceSeconds)
  do {
    $t = @(Get-Process -Name 'RST.Tray' -ErrorAction SilentlyContinue)
    if ($t.Count -gt 0) { break }
    Start-Sleep -Milliseconds 500
  } while ((Get-Date) -lt $deadline)
  Show-TrayProcesses $Label
  Assert ($t.Count -eq 0) "[$Label] no RST.Tray process runs (within $GraceSeconds s)"
}

# The tray start helper's logs written since $Since, oldest first; each is printed. Returns the file paths.
function Get-StartTrayLog([datetime]$Since, [string]$Label) {
  $dirs = @($env:TEMP, [IO.Path]::GetTempPath(), (Join-Path $env:SystemRoot 'Temp'), (Join-Path $env:SystemRoot 'SystemTemp')) |
    Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { (Resolve-Path $_).Path.TrimEnd('\') } | Sort-Object -Unique
  $logs = @($dirs | ForEach-Object { Get-ChildItem $_ -Filter 'RST-StartTray-*-*.log' -ErrorAction SilentlyContinue } |
    Where-Object { $_.LastWriteTimeUtc -ge $Since.ToUniversalTime().AddSeconds(-1) } |
    Group-Object Name | ForEach-Object { $_.Group[0] } | Sort-Object LastWriteTimeUtc)   # one file may be seen through two TEMP spellings
  foreach ($l in $logs) {
    Write-Host "---- [$Label] $($l.FullName)"
    Get-Content $l.FullName | ForEach-Object { Write-Host "  $_" }
    if ($env:RUNNER_TEMP) { Copy-Item $l.FullName (Join-Path $env:RUNNER_TEMP "$($Label -replace '[^\w-]', '_')-$($l.Name)") -Force -ErrorAction SilentlyContinue }
  }
  if (-not $logs) { Write-Host "  [$Label] no RST-StartTray log since $Since (in $($dirs -join ', '))" }
  return , @($logs | ForEach-Object { $_.FullName })
}
# The lines of every start-helper log since $Since.
function Get-StartTrayLines([datetime]$Since, [string]$Label) {
  return , @(Get-StartTrayLog $Since $Label | ForEach-Object { Get-Content $_ })
}
