// rst-c's own (not vendored): the Revit add-in starts the RST tray when it isn't running (spec #20 §2; F17 WU2).
//
// RstApplication calls EnsureRunning once per Revit session, at ApplicationInitialized, on a thread-pool task: never
// on Revit's UI thread, and nothing waits for it. In order:
//   1. the tray's single-instance mutex Local\RST.Tray.<UserSid> exists       -> it runs; nothing to do;
//   2. Task Manager's Startup toggle for RST is off (StartupApproved\Run "RST") -> the user said no; not started;
//   3. Revit runs elevated                                                    -> never start the tray elevated
//      (F17 WU1 review N8: an elevated tray loses TaskbarCreated to UIPI); not started;
//   4. the tray at its fixed install path, per-user first (it shadows a shared copy, spec #20 §7), then
//      per-machine; neither exists                                            -> not installed; not started;
//   5. start it with --startup (hidden in the notification area) through the shell, and do not wait.
// The paths are fixed: never read from a profile, a setting or the registry. Decision #29: the mutex is the only
// thing the add-in and the tray share at run time; there is no pipe. A failure is one log line, never an error
// for Revit.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    /// <summary>Revit runs elevated; the tray is never started elevated.</summary>
    Elevated,

    /// <summary>No tray at either fixed install path.</summary>
    NotInstalled,

    /// <summary>A check or the start itself failed.</summary>
    Failed,
}

/// <summary>The outcome, the tray path when one was found, and the one log line.</summary>
internal sealed record TrayLaunchResult(TrayLaunchOutcome Outcome, string? Path, string Message);

/// <summary>What the launcher reads from Windows and the one thing it does. RuntimeTests fakes it.</summary>
internal interface ITrayLaunchEnvironment
{
    /// <summary>The current user's SID (S-1-5-21-…).</summary>
    string UserSid { get; }

    /// <summary>This process (Revit) runs with an elevated token.</summary>
    bool IsElevated { get; }

    /// <summary>The named mutex exists in this session (a tray holds it).</summary>
    bool MutexExists(string name);

    /// <summary>
    /// The raw <c>StartupApproved\Run</c> values Task Manager keeps for RST's Run value, HKCU's then HKLM's (null
    /// when absent). Task Manager writes them; RST.msi never does.
    /// </summary>
    IReadOnlyList<byte[]?> StartupApprovedValues();

    /// <summary>The fixed install paths, in the order they are tried.</summary>
    IReadOnlyList<string> CandidatePaths { get; }

    bool FileExists(string path);

    /// <summary>Starts <paramref name="path"/> through the shell and returns at once.</summary>
    void Start(string path, string arguments);
}

internal static class TrayLauncher
{
    /// <summary>The per-machine tray folder under Program Files (RST-Machine.msi, spec #20 §7; F17 WU5's layout).</summary>
    public static readonly string[] PerMachineTrayFolder = ["RST", "Tray"];

    /// <summary>Starts the tray unless it runs, the user switched it off, Revit is elevated or it is not installed. Never throws.</summary>
    public static TrayLaunchResult EnsureRunning(ITrayLaunchEnvironment env)
    {
        ArgumentNullException.ThrowIfNull(env);
        string? path = null;
        try
        {
            if (env.MutexExists(TrayPaths.MutexName(env.UserSid)))
                return new(TrayLaunchOutcome.AlreadyRunning, null, "the RST tray is already running");

            if (StartupApproved.AnyDisabled(env.StartupApprovedValues()))
                return new(TrayLaunchOutcome.StartupDisabled, null,
                    "RST is switched off in Task Manager's Startup apps, so the RST tray was not started");

            if (env.IsElevated)
                return new(TrayLaunchOutcome.Elevated, null,
                    "Revit runs as administrator; the RST tray is never started elevated, so it was not started");

            foreach (var candidate in env.CandidatePaths)
            {
                if (!env.FileExists(candidate)) continue;
                path = candidate;
                break;
            }
            if (path is null)
                return new(TrayLaunchOutcome.NotInstalled, null,
                    "the RST tray is not installed (" + string.Join(", ", env.CandidatePaths) + "), so it was not started");

            env.Start(path, TrayPaths.StartupArgument);
            return new(TrayLaunchOutcome.Started, path, "started the RST tray: " + path + " " + TrayPaths.StartupArgument);
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

    /// <summary>Disabled under either hive (HKCU for RST.msi's Run value, HKLM for RST-Machine.msi's).</summary>
    public static bool AnyDisabled(IEnumerable<byte[]?> values)
    {
        foreach (var value in values)
            if (IsDisabled(value)) return true;
        return false;
    }
}

/// <summary>The real Windows: the session's named objects, the registry, the file system and the shell.</summary>
internal sealed class MachineTrayLaunchEnvironment : ITrayLaunchEnvironment
{
    private readonly string _runValueName;

    /// <param name="candidatePaths">The fixed install paths when null (per-user, then per-machine).</param>
    /// <param name="runValueName">The Run value Task Manager's toggle is keyed by (RuntimeTests pass a scratch name).</param>
    public MachineTrayLaunchEnvironment(IReadOnlyList<string>? candidatePaths = null, string runValueName = TrayPaths.RunValueName)
    {
        CandidatePaths = candidatePaths ?? FixedPaths();
        _runValueName = runValueName;
    }

    public string UserSid
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.User?.Value ?? throw new InvalidOperationException("The current user has no SID.");
        }
    }

    /// <summary>.NET's own token check (TokenElevation on Windows).</summary>
    public bool IsElevated => Environment.IsPrivilegedProcess;

    public IReadOnlyList<string> CandidatePaths { get; }

    /// <summary>
    /// <c>%LocalAppData%\Programs\RST\Tray\RST.Tray.exe</c> (RST.msi), then <c>C:\Program Files\RST\Tray\RST.Tray.exe</c>
    /// (RST-Machine.msi). Both come from Windows' known folders, never from data RST reads.
    /// </summary>
    public static IReadOnlyList<string> FixedPaths()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        return
        [
            Path.Combine(TrayPaths.PerUserInstallDir, TrayPaths.ExeName),
            Path.Combine([programFiles, .. TrayLauncher.PerMachineTrayFolder, TrayPaths.ExeName]),
        ];
    }

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

    public IReadOnlyList<byte[]?> StartupApprovedValues() =>
        [Read(RegistryHive.CurrentUser), Read(RegistryHive.LocalMachine)];

    public bool FileExists(string path) => File.Exists(path);

    public void Start(string path, string arguments)
    {
        var info = new ProcessStartInfo(path)
        {
            Arguments = arguments,
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(path) ?? "",
        };
        using var started = Process.Start(info);
    }

    private byte[]? Read(RegistryHive hive)
    {
        try
        {
            using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var key = root.OpenSubKey(StartupApproved.RunKey);
            return key?.GetValue(_runValueName) as byte[];
        }
        catch (Exception)
        {
            // Unreadable is "not switched off": Task Manager's toggle is the user's, and only a value says no.
            return null;
        }
    }
}
