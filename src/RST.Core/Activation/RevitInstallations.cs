// rst-c's own (not vendored): which Revit years are installed, and which switching mechanism each
// one supports (spec #18, In Scope 1 and 3). The detection follows decision #10 (the installer's
// AppSearch, installer/Product.wxs): Revit.exe in the InstallLocation of Revit's uninstall entry
// {7346B4A0-<yy>00-0510-0000-705C0D862004}, else in C:\Program Files\Autodesk\Revit <year>\.
//
// The mechanism: Revit's Add-Ins Manager (and so its AddInsSettings.json) arrived in Revit 2025.3
// (addins-manager-spike.md §1). A year whose install ships the manager's own manifest
// (<install>\AddIns\AddInsManager\Autodesk.AddInsManager.addin), or whose Revit.exe build is 2025.3
// or later, uses the settings file; Revit 2025.0 to 2025.2 fall back to renaming manifests.

using System.Diagnostics;

namespace RST.Core.Activation;

/// <summary>How rst-c switches add-ins in a Revit year.</summary>
public enum SwitchMechanism
{
    /// <summary>Revit's per-user AddInsSettings.json (Revit 2025.3 and later).</summary>
    SettingsFile,

    /// <summary>Renaming <c>X.addin</c> to <c>X.addin.RSTdisabled</c> (Revit 2025.0 to 2025.2).</summary>
    Rename,
}

/// <summary>One installed Revit year: its folder, its build (when readable) and its mechanism.</summary>
public sealed record RevitYear(int Year, string? InstallDir, string? Build, SwitchMechanism Mechanism)
{
    public string Label => "Revit " + Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What detection reads from the machine. Tests pass a fake.</summary>
public interface IRevitInstallProbe
{
    /// <summary>The InstallLocation of Revit <paramref name="year"/>'s uninstall entry, or null.</summary>
    string? InstallLocation(int year);

    bool FileExists(string path);

    /// <summary>The file version of <paramref name="path"/>, or null when it cannot be read.</summary>
    Version? FileVersion(string path);
}

public static class RevitInstallations
{
    /// <summary>The Revit years rst-c ships for (the MSI's years).</summary>
    public static readonly IReadOnlyList<int> SupportedYears = [2025, 2026, 2027];

    /// <summary>The first Revit 2025 update with the Add-Ins Manager.</summary>
    public static readonly Version FirstManagerBuild2025 = new(25, 3);

    /// <summary>The installed supported years, oldest first.</summary>
    public static IReadOnlyList<RevitYear> Detect(AddinRoots roots, IRevitInstallProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        probe ??= new MachineRevitInstallProbe();
        var found = new List<RevitYear>();
        foreach (var year in SupportedYears)
        {
            string? dir = null;
            var fromRegistry = SafeString(() => probe.InstallLocation(year));
            if (fromRegistry is { Length: > 0 } && probe.FileExists(Path.Combine(fromRegistry, "Revit.exe"))) dir = fromRegistry;
            else if (probe.FileExists(Path.Combine(roots.DefaultRevitInstall(year), "Revit.exe"))) dir = roots.DefaultRevitInstall(year);
            if (dir is null) continue;

            var exe = Path.Combine(dir, "Revit.exe");
            var build = SafeVersion(() => probe.FileVersion(exe));
            var manager = probe.FileExists(Path.Combine(dir, "AddIns", "AddInsManager", "Autodesk.AddInsManager.addin"));
            found.Add(new RevitYear(year, dir, build?.ToString(), MechanismFor(year, build, manager)));
        }
        return found;
    }

    /// <summary>
    /// The settings file from 2026 on, and in 2025 when the install has the Add-Ins Manager or its
    /// build is 2025.3 or later; renaming otherwise (an unknown 2025 build renames: it works on
    /// every build, the settings file only on 2025.3+).
    /// </summary>
    public static SwitchMechanism MechanismFor(int year, Version? build, bool hasAddInsManager)
    {
        if (year >= 2026 || hasAddInsManager) return SwitchMechanism.SettingsFile;
        if (year == 2025 && build is not null && build >= FirstManagerBuild2025) return SwitchMechanism.SettingsFile;
        return SwitchMechanism.Rename;
    }

    private static string? SafeString(Func<string?> read)
    {
        try { return read(); }
        catch { return null; }
    }

    private static Version? SafeVersion(Func<Version?> read)
    {
        try { return read(); }
        catch { return null; }
    }
}

/// <summary>The real probe: the 64-bit uninstall entry, the file system, Revit.exe's file version.</summary>
public sealed class MachineRevitInstallProbe : IRevitInstallProbe
{
    public string? InstallLocation(int year)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var yy = (year % 100).ToString("00", System.Globalization.CultureInfo.InvariantCulture);
        var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{{7346B4A0-{yy}00-0510-0000-705C0D862004}}";
        using var hklm = Microsoft.Win32.RegistryKey.OpenBaseKey(Microsoft.Win32.RegistryHive.LocalMachine, Microsoft.Win32.RegistryView.Registry64);
        using var entry = hklm.OpenSubKey(key);
        return entry?.GetValue("InstallLocation") as string;
    }

    public bool FileExists(string path) => File.Exists(path);

    public Version? FileVersion(string path)
    {
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        return info.FileMajorPart == 0 && info.FileMinorPart == 0
            ? null
            : new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }
}
