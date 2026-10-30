// rst-c's own (not vendored): which supported Revit years are installed (spec #18, In Scope 1).
// The detection follows decision #10 (the installer's AppSearch, installer/Product.wxs): Revit.exe
// in the InstallLocation of Revit's uninstall entry {7346B4A0-<yy>00-0510-0000-705C0D862004}, else
// in C:\Program Files\Autodesk\Revit <year>\.
//
// Decision #21 (FnB): RST supports Revit 2025 and later only, and every supported year switches
// add-ins through Revit's AddInsSettings.json. Revit 2025 is "partially supported": it is treated
// exactly like 2026 (Autodesk's Add-Ins Manager arrived in 2025.3; on an older 2025 build the file
// is written but has no effect, which the docs say). There is no per-build detection and no rename
// fallback; years before 2025 are ignored.

namespace RST.Core.Activation;

/// <summary>One installed Revit year and its folder.</summary>
public sealed record RevitYear(int Year, string? InstallDir)
{
    public string Label => "Revit " + Year.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>What detection reads from the machine. Tests pass a fake.</summary>
public interface IRevitInstallProbe
{
    /// <summary>The InstallLocation of Revit <paramref name="year"/>'s uninstall entry, or null.</summary>
    string? InstallLocation(int year);

    bool FileExists(string path);
}

public static class RevitInstallations
{
    /// <summary>The first Revit year RST supports (decision #21); earlier years are ignored.</summary>
    public const int MinimumYear = 2025;

    /// <summary>The Revit years RST ships for (the MSI's years).</summary>
    public static readonly IReadOnlyList<int> SupportedYears = [2025, 2026, 2027];

    /// <summary>The installed supported years, oldest first.</summary>
    public static IReadOnlyList<RevitYear> Detect(AddinRoots roots, IRevitInstallProbe? probe = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        probe ??= new MachineRevitInstallProbe();
        var found = new List<RevitYear>();
        foreach (var year in SupportedYears)
        {
            string? fromRegistry;
            try { fromRegistry = probe.InstallLocation(year); }
            catch { fromRegistry = null; }
            if (fromRegistry is { Length: > 0 } && probe.FileExists(Path.Combine(fromRegistry, "Revit.exe")))
                found.Add(new RevitYear(year, fromRegistry));
            else if (probe.FileExists(Path.Combine(roots.DefaultRevitInstall(year), "Revit.exe")))
                found.Add(new RevitYear(year, roots.DefaultRevitInstall(year)));
        }
        return found;
    }
}

/// <summary>The real probe: the 64-bit uninstall entry and the file system.</summary>
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
}
