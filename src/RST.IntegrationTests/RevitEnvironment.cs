using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace RST.IntegrationTests;

/// <summary>
/// Helpers for detecting the Revit installations present on the test host.
/// Uses %PROGRAMFILES%\Autodesk\Revit {year} as the authoritative signal —
/// the same root AddinDirectoryScanner uses for the RevitInstall search path.
/// </summary>
internal static class RevitEnvironment
{
    private static readonly string[] KnownYears = { "2024", "2025", "2026", "2027" };

    /// <summary>
    /// Returns the Revit version strings (e.g. "2025", "2026") whose install
    /// directory is present on this machine. Empty if Revit is not installed.
    /// </summary>
    public static IReadOnlyList<string> InstalledVersions()
    {
        var pf = Environment.GetEnvironmentVariable("PROGRAMFILES")
                 ?? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        if (string.IsNullOrEmpty(pf))
            return Array.Empty<string>();

        return KnownYears
            .Where(v => Directory.Exists(Path.Combine(pf, "Autodesk", $"Revit {v}")))
            .ToArray();
    }
}
