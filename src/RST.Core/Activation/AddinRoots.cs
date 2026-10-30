// rst-c's own (not vendored): where Revit looks for add-ins and keeps its Add-Ins Manager
// settings, per Revit year (spec #18, feature #16). The paths are the ones the Add-Ins Manager
// spike proved on the VM (shared/f15/addins-manager-spike.md §2, §5, §6):
//   - 2027 and later load machine-wide add-ins from C:\Program Files\Autodesk\Revit\Addins\<yr>
//     and C:\Program Files\Autodesk\ApplicationPlugins, and ignore both %ProgramData% roots;
//   - the per-user settings are %AppData%\Autodesk\Revit\Autodesk Revit <yr>\AddinsData\AddInsSettings.json;
//   - the admin lock is C:\ProgramData\Autodesk\RVT <yr>\Admin\AdminSettings.json up to 2026 and
//     C:\Program Files\Autodesk\Revit\Admin\<yr>\AdminSettings.json from 2027.
// No WPF and no Revit types: the v3.1 tray service (feature #17) hosts the same code.

namespace RST.Core.Activation;

/// <summary>Who an add-in is installed for, which decides what switching it can touch.</summary>
public enum AddinScope
{
    /// <summary>Under the user's %AppData%.</summary>
    User,

    /// <summary>Installed for every user (%ProgramData%, or Program Files from 2027).</summary>
    Machine,

    /// <summary>Shipped inside Revit's own install folder. Never switched.</summary>
    RevitInstall,
}

/// <summary>How a folder of manifests is laid out.</summary>
public enum ManifestRootKind
{
    /// <summary>An <c>Addins\&lt;year&gt;</c> folder: Revit reads the <c>.addin</c> files at its top level.</summary>
    AddinsFolder,

    /// <summary>An <c>ApplicationPlugins</c> folder: <c>*.bundle</c> packages, each with a PackageContents.xml.</summary>
    ApplicationPlugins,

    /// <summary>Revit's own <c>AddIns</c> folder, searched recursively.</summary>
    RevitInstall,
}

/// <summary>A folder Revit reads manifests from for one year.</summary>
public sealed record ManifestRoot(string Path, AddinScope Scope, ManifestRootKind Kind);

/// <summary>
/// The three base folders every add-in path derives from. <see cref="FromEnvironment"/> reads the
/// real ones; tests point them at a fake tree.
/// </summary>
public sealed record AddinRoots(string AppData, string ProgramData, string ProgramFiles)
{
    /// <summary>The first Revit year that reads machine-wide add-ins from Program Files only.</summary>
    public const int ProgramFilesMachineYear = 2027;

    /// <summary>The current user's %AppData%, %ProgramData% and %ProgramFiles%.</summary>
    public static AddinRoots FromEnvironment() => new(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetEnvironmentVariable("PROGRAMDATA") is { Length: > 0 } pd
            ? pd
            : Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        Environment.GetEnvironmentVariable("PROGRAMFILES") is { Length: > 0 } pf
            ? pf
            : Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles));

    /// <summary><c>%AppData%\Autodesk\Revit\Autodesk Revit &lt;year&gt;\AddinsData\AddInsSettings.json</c>.</summary>
    public string SettingsFile(int year) =>
        System.IO.Path.Combine(AppData, "Autodesk", "Revit", $"Autodesk Revit {year}", "AddinsData", "AddInsSettings.json");

    /// <summary>The IT admin's lock file for <paramref name="year"/> (read only; rst-c never writes it).</summary>
    public string AdminSettingsFile(int year) => year >= ProgramFilesMachineYear
        ? System.IO.Path.Combine(ProgramFiles, "Autodesk", "Revit", "Admin", year.ToString(System.Globalization.CultureInfo.InvariantCulture), "AdminSettings.json")
        : System.IO.Path.Combine(ProgramData, "Autodesk", $"RVT {year}", "Admin", "AdminSettings.json");

    /// <summary>Revit's install folder for <paramref name="year"/> at its default location.</summary>
    public string DefaultRevitInstall(int year) => System.IO.Path.Combine(ProgramFiles, "Autodesk", $"Revit {year}");

    /// <summary>
    /// Every folder Revit <paramref name="year"/> reads manifests from, in Revit's order: the
    /// user's Addins folder, the machine Addins folder, the user's then the machine bundles, and
    /// Revit's own add-ins (<paramref name="revitInstall"/>, else the default folder).
    /// </summary>
    public IReadOnlyList<ManifestRoot> ManifestRoots(int year, string? revitInstall = null)
    {
        var y = year.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var roots = new List<ManifestRoot>
        {
            new(System.IO.Path.Combine(AppData, "Autodesk", "Revit", "Addins", y), AddinScope.User, ManifestRootKind.AddinsFolder),
        };
        if (year >= ProgramFilesMachineYear)
        {
            roots.Add(new(System.IO.Path.Combine(ProgramFiles, "Autodesk", "Revit", "Addins", y), AddinScope.Machine, ManifestRootKind.AddinsFolder));
            roots.Add(new(System.IO.Path.Combine(AppData, "Autodesk", "ApplicationPlugins"), AddinScope.User, ManifestRootKind.ApplicationPlugins));
            roots.Add(new(System.IO.Path.Combine(ProgramFiles, "Autodesk", "ApplicationPlugins"), AddinScope.Machine, ManifestRootKind.ApplicationPlugins));
        }
        else
        {
            roots.Add(new(System.IO.Path.Combine(ProgramData, "Autodesk", "Revit", "Addins", y), AddinScope.Machine, ManifestRootKind.AddinsFolder));
            roots.Add(new(System.IO.Path.Combine(AppData, "Autodesk", "ApplicationPlugins"), AddinScope.User, ManifestRootKind.ApplicationPlugins));
            roots.Add(new(System.IO.Path.Combine(ProgramData, "Autodesk", "ApplicationPlugins"), AddinScope.Machine, ManifestRootKind.ApplicationPlugins));
        }
        roots.Add(new(System.IO.Path.Combine(revitInstall ?? DefaultRevitInstall(year), "AddIns"), AddinScope.RevitInstall, ManifestRootKind.RevitInstall));
        return roots;
    }
}
