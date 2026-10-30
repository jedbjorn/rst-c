// rst-c's own (not vendored): the Revit add-in starts the RST tray when it isn't running (spec #20 §2; F17 WU2).
//
// RstApplication calls EnsureRunning once per Revit session, at ApplicationInitialized, on a thread-pool task: never
// on Revit's UI thread, and nothing waits for it. In order:
//   1. the tray's single-instance mutex Local\RST.Tray.<UserSid> exists       -> it runs; nothing to do;
//   2. the tray at its fixed install path, per-user first (it shadows a shared copy, spec #20 §7), then
//      per-machine; neither exists                                            -> not installed; not started;
//   3. Task Manager's Startup toggle for that install's Run value is off (StartupApproved\Run "RST", HKCU for the
//      per-user tray, HKLM for the per-machine one; review N3)              -> the user said no; not started;
//   4. Revit runs with a full elevated token (UAC on, run as administrator) -> never start the tray elevated
//      (F17 WU1 review N8: an elevated tray loses TaskbarCreated to UIPI); not started. With UAC off every process
//      has a full token and there is no UIPI to lose to, so the tray starts (review N2);
//   5. start it with --startup (hidden in the notification area), outside Revit's job when Revit runs in one
//      (TrayProcessStart; review N1), and do not wait.
// The paths are fixed: never read from a profile, a setting or the registry. Decision #29: the mutex is the only
// thing the add-in and the tray share at run time; there is no pipe. A failure is one log line, never an error
// for Revit.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Threading;
using Microsoft.Win32;
using RST.Core.Tray;

namespace RST.Engine.Tray;

/// <summary>What <see cref="TrayLauncher.EnsureRunning"/> did.</summary>
internal enum TrayLaunchOutcome
{
    /// <summary>The tray was started (not waited for).</summary>
    Started,

    /// <summary>The tray's mutex exists: it already runs for this user and session.</summary>
    AlreadyRunning,

    /// <summary>RST is switched off in Task Manager's Startup apps.</summary>
    StartupDisabled,

    /// <summary>Revit runs elevated (a full token under UAC); the tray is never started elevated.</summary>
    Elevated,

    /// <summary>No tray at either fixed install path.</summary>
    NotInstalled,

    /// <summary>A check or the start itself failed.</summary>
    Failed,
}

/// <summary>The outcome, the tray path when one was found, and the one log line.</summary>
internal sealed record TrayLaunchResult(TrayLaunchOutcome Outcome, string? Path, string Message);

/// <summary>A fixed tray install path, and whether it is the per-machine install (HKLM Run) or the per-user one (HKCU Run).</summary>
internal sealed record TrayCandidate(string Path, bool PerMachine);

/// <summary>What the launcher reads from Windows and the one thing it does. RuntimeTests fakes it.</summary>
internal interface ITrayLaunchEnvironment
{
    /// <summary>The current user's SID (S-1-5-21-…).</summary>
    string UserSid { get; }

    /// <summary>This process (Revit) runs with a full elevated token under UAC (TokenElevationTypeFull).</summary>
    bool IsElevated { get; }

    /// <summary>The named mutex exists in this session (a tray holds it).</summary>
    bool MutexExists(string name);

    /// <summary>
    /// Task Manager's raw <c>StartupApproved\Run</c> value for RST's Run value, under HKLM when
    /// <paramref name="perMachine"/>, else HKCU (null when absent). Task Manager writes it; RST's MSIs never do.
    /// </summary>
    byte[]? StartupApprovedValue(bool perMachine);

    /// <summary>The fixed install paths, in the order they are tried.</summary>
    IReadOnlyList<TrayCandidate> Candidates { get; }

    bool FileExists(string path);

    /// <summary>Starts <paramref name="path"/> and returns at once, saying how it was started.</summary>
    string Start(string path, string arguments);
}

internal static class TrayLauncher
{
    /// <summary>
    /// The per-machine tray folder, <c>C:\Program Files\RST\Tray\</c> (RST-Machine.msi, spec #20 §7): F17 WU5's
    /// <see cref="TrayPaths.PerMachineInstallDir"/>, the one place it is named.
    /// </summary>
    public static string PerMachineInstallDir => TrayPaths.PerMachineInstallDir;

    /// <summary>Starts the tray unless it runs, it is not installed, the user switched it off or Revit is elevated. Never throws.</summary>
    public static TrayLaunchResult EnsureRunning(ITrayLaunchEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? path = null;
        try
        {
            if (env.MutexExists(TrayPaths.MutexName(env.UserSid)))
                return new(TrayLaunchOutcome.AlreadyRunning, null, "the RST tray is already running");

            TrayCandidate? found = null;
            foreach (var candidate in env.Candidates)
            {
                if (!env.FileExists(candidate.Path)) continue;
                found = candidate;
                break;
            }
            if (found is null)
            {
                return new(TrayLaunchOutcome.NotInstalled, null,
                    "the RST tray is not installed (" + string.Join(", ", env.Candidates.Select(c => c.Path)) + "), so it was not started");
            }
            path = found.Path;

            if (StartupApproved.IsDisabled(env.StartupApprovedValue(found.PerMachine)))
                return new(TrayLaunchOutcome.StartupDisabled, path,
                    "RST is switched off in Task Manager's Startup apps, so the RST tray was not started");

            if (env.IsElevated)
                return new(TrayLaunchOutcome.Elevated, path,
                    "Revit runs as administrator; the RST tray is never started elevated, so it was not started");

            var route = env.Start(path, TrayPaths.StartupArgument);
            return new(TrayLaunchOutcome.Started, path, "started the RST tray: " + path + " " + TrayPaths.StartupArgument + " (" + route + ")");
        }
        catch (Exception ex)
        {
            return new(TrayLaunchOutcome.Failed, path,
                "could not start the RST tray" + (path is null ? "" : " (" + path + ")") + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }
}

/// <summary>Task Manager's Startup toggle, as Explorer stores it.</summary>
internal static class StartupApproved
{
    /// <summary><c>Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run</c>, under HKCU and HKLM.</summary>
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// A REG_BINARY of 12 bytes: the first byte is even (02, 06) when the entry is enabled and odd (03, 07) when the
    /// user disabled it; the rest is when. Missing, empty or not binary means enabled (Task Manager never touched it).
    /// </summary>
    public static bool IsDisabled(byte[]? value) => value is { Length: > 0 } && (value[0] & 1) == 1;
}

/// <summary>The real Windows: the session's named objects, the registry, the file system and the process start.</summary>
internal sealed class MachineTrayLaunchEnvironment : ITrayLaunchEnvironment
{
    private readonly string _runValueName;

    /// <param name="candidates">The fixed install paths when null (per-user, then per-machine).</param>
    /// <param name="runValueName">The Run value Task Manager's toggle is keyed by (RuntimeTests pass a scratch name).</param>
    public MachineTrayLaunchEnvironment(IReadOnlyList<TrayCandidate>? candidates = null, string runValueName = TrayPaths.RunValueName)
    {
        Candidates = candidates ?? FixedPaths();
        _runValueName = runValueName;
    }

    public string UserSid
    {
        get
        {
            using var identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        }
    }

    /// <summary>
    /// TokenElevationType is Full: Revit was run as administrator with UAC on. With UAC off (or the built-in
    /// Administrator) the type is Default: every process has a full token, Explorer too, so there is no UIPI gap.
    /// </summary>
    public bool IsElevated => ElevationType() == TokenElevationTypeFull;

    public IReadOnlyList<TrayCandidate> Candidates { get; }

    /// <summary>
    /// <c>%LocalAppData%\Programs\RST\Tray\RST.Tray.exe</c> (RST.msi, HKCU Run), then
    /// <c>C:\Program Files\RST\Tray\RST.Tray.exe</c> (RST-Machine.msi, HKLM Run). Both come from Windows' known
    /// folders, never from data RST reads.
    /// </summary>
    public static IReadOnlyList<TrayCandidate> FixedPaths() =>
    [
        new(Path.Combine(TrayPaths.PerUserInstallDir, TrayPaths.ExeName), PerMachine: false),
        new(Path.Combine(TrayLauncher.PerMachineInstallDir, TrayPaths.ExeName), PerMachine: true),
    ];

    public bool MutexExists(string name)
    {
        try
        {
            if (!Mutex.TryOpenExisting(name, out var mutex)) return false;
            mutex.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // It exists but this token may not open it (a tray started by another token of this user): it runs.
            return true;
        }
    }

    public byte[]? StartupApprovedValue(bool perMachine)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(perMachine ? RegistryHive.LocalMachine : RegistryHive.CurrentUser, RegistryView.Registry64);
            using var key = root.OpenSubKey(StartupApproved.RunKey);
            return key?.GetValue(_runValueName) as byte[];
        }
        catch (Exception)
        {
            // Unreadable is "not switched off": Task Manager's toggle is the user's, and only a value says no.
            return null;
        }
    }

    public bool FileExists(string path) => File.Exists(path);

    public string Start(string path, string arguments) => TrayProcessStart.Start(path, arguments).Route;

    private const int TokenElevationTypeClass = 18;
    private const int TokenElevationTypeFull = 2;

    /// <summary>TOKEN_ELEVATION_TYPE of this process's token: 1 Default, 2 Full, 3 Limited (0 when unreadable).</summary>
    internal static int ElevationType()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return GetTokenInformation(identity.Token, TokenElevationTypeClass, out var type, sizeof(int), out _) ? type : 0;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);
}
