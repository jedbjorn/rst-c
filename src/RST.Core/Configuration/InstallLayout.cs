// rst-c's own (not vendored): where each install scope puts RST, and whether a user's own copy shadows the one
// installed for everyone (feature #17, spec #20 §7; F17 WU5). Pure path math over injected roots and a file-exists
// seam, so RST.Tests checks it on Linux. The engine logs a shadow once at startup; the tray tells the user once.
//
//   RST.msi (per user)              %AppData%\Autodesk\Revit\Addins\<year>\RST.addin
//                                   %AppData%\RST\R<NN>\app\                       the engine
//   RST-Machine.msi (per machine)   %ProgramData%\Autodesk\Revit\Addins\2025|2026\RST.addin
//                                   C:\Program Files\Autodesk\Revit\Addins\2027\RST.addin
//                                   C:\Program Files\RST\R<NN>\app\                 the engine
//                                   C:\Program Files\RST\Tray\                      the tray
//
// Revit loads a per-user manifest in preference to an all-users manifest of the same name, so a user who still has
// RST.msi installed runs their own copy. That is information, never enforcement: nothing here removes anything.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace RST.Core.Configuration;

/// <summary>Which installer put RST where it is.</summary>
public enum RstInstallScope
{
    /// <summary>RST.msi, for one Windows user.</summary>
    User,

    /// <summary>RST-Machine.msi, for everyone on the PC.</summary>
    Machine,
}

/// <summary>A Revit year where the user's own RST hides the one installed for everyone.</summary>
/// <param name="Year">The Revit year.</param>
/// <param name="UserManifest">The user's own manifest, the one Revit loads.</param>
/// <param name="MachineManifest">The all-users manifest Revit skips for this user.</param>
public sealed record RstShadow(int Year, string UserManifest, string MachineManifest);

public static class InstallLayout
{
    /// <summary>RST's manifest file name, the same in both scopes.</summary>
    public const string ManifestName = "RST.addin";

    /// <summary>The Revit years the installers ship (decision #21: 2025 and later). Add a year when the MSIs do.</summary>
    public static readonly IReadOnlyList<int> ShippedYears = new[] { 2025, 2026, 2027 };

    /// <summary>The first Revit year that reads all-users manifests from Program Files instead of %ProgramData%.</summary>
    public const int FirstProgramFilesManifestYear = 2027;

    /// <summary>RST.msi's manifest: <c>%AppData%\Autodesk\Revit\Addins\&lt;year&gt;\RST.addin</c>.</summary>
    public static string UserManifest(string appData, int year) =>
        Path.Combine(appData, "Autodesk", "Revit", "Addins", Year(year), ManifestName);

    /// <summary>
    /// RST-Machine.msi's manifest: <c>%ProgramData%\Autodesk\Revit\Addins\&lt;year&gt;\RST.addin</c> for 2025 and 2026,
    /// <c>C:\Program Files\Autodesk\Revit\Addins\&lt;year&gt;\RST.addin</c> from 2027.
    /// </summary>
    public static string MachineManifest(string programData, string programFiles, int year) =>
        year >= FirstProgramFilesManifestYear
            ? Path.Combine(programFiles, "Autodesk", "Revit", "Addins", Year(year), ManifestName)
            : Path.Combine(programData, "Autodesk", "Revit", "Addins", Year(year), ManifestName);

    /// <summary>RST-Machine.msi's root, <c>C:\Program Files\RST\</c>: the bootstraps, the engines and the tray.</summary>
    public static string MachineRoot(string programFiles) => Path.Combine(programFiles, "RST");

    /// <summary>The per-machine tray folder, <c>C:\Program Files\RST\Tray\</c>.</summary>
    public static string MachineTrayDir(string programFiles) => Path.Combine(MachineRoot(programFiles), "Tray");

    /// <summary>The scope a file belongs to: <see cref="RstInstallScope.Machine"/> under <see cref="MachineRoot"/>, otherwise User.</summary>
    public static RstInstallScope ScopeOf(string? path, string programFiles)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(programFiles)) return RstInstallScope.User;
        var root = MachineRoot(programFiles).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var full = path.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var rootNormal = root.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        return full.StartsWith(rootNormal, StringComparison.OrdinalIgnoreCase) ? RstInstallScope.Machine : RstInstallScope.User;
    }

    /// <summary>
    /// Every year in <paramref name="years"/> where both manifests exist: the user's own RST is loaded there and the
    /// shared one is not. Unreadable paths count as absent. Never throws.
    /// </summary>
    public static IReadOnlyList<RstShadow> FindShadows(
        string? appData, string? programData, string? programFiles, IEnumerable<int> years, Func<string, bool> fileExists)
    {
        ArgumentNullException.ThrowIfNull(years);
        ArgumentNullException.ThrowIfNull(fileExists);
        var found = new List<RstShadow>();
        if (string.IsNullOrEmpty(appData) || string.IsNullOrEmpty(programData) || string.IsNullOrEmpty(programFiles)) return found;
        foreach (var year in years)
        {
            var user = UserManifest(appData, year);
            var machine = MachineManifest(programData, programFiles, year);
            if (Exists(fileExists, user) && Exists(fileExists, machine)) found.Add(new RstShadow(year, user, machine));
        }
        return found;
    }

    /// <summary><see cref="FindShadows(string?, string?, string?, IEnumerable{int}, Func{string, bool})"/> for this user and PC.</summary>
    public static IReadOnlyList<RstShadow> FindShadows(IEnumerable<int>? years = null) => FindShadows(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        years ?? ShippedYears,
        File.Exists);

    /// <summary>The engine's one startup line for a shadow (English, like the rest of the engine log).</summary>
    public static string Describe(RstShadow shadow)
    {
        ArgumentNullException.ThrowIfNull(shadow);
        return string.Format(CultureInfo.InvariantCulture,
            "RST is installed both for you and for everyone on this PC (Revit {0}). Revit loads your own copy ({1}), not the shared one ({2}). "
            + "To use the shared one, remove \"RST\" in Settings > Apps.",
            shadow.Year, shadow.UserManifest, shadow.MachineManifest);
    }

    private static string Year(int year) => year.ToString(CultureInfo.InvariantCulture);

    private static bool Exists(Func<string, bool> fileExists, string path)
    {
        try { return fileExists(path); }
        catch { return false; }
    }
}
