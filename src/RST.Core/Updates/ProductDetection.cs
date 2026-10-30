// rst-c's own (not vendored): what is installed, and what the Apps and Updates pages say about it (spec #20 §6;
// research Q6; F17 WU3).
//
//   1. Windows Installer, by UpgradeCode (RST.Tray's MsiProducts: MsiEnumRelatedProducts, then
//      MsiGetProductInfoEx for the version and the context). This is the real signal.
//   2. Secondary: the companion's ClientId in any .addin RST.Core's own scanner finds (AddinDirectoryScanner's
//      search paths and AddinManifestParser, as the Loader uses them, for every Revit year 2025+ present).
//      A ClientId with no Windows Installer product means a copy put there by hand: "installed manually:
//      updates not managed by RST".
//
// A per-user install by another Windows user is invisible to this user, which is correct: the pages say "for you".
// The state rules (ProductStatus.Compute) are pure, so RST.Tests covers them on Linux.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RST.Core.AddIns;
using RST.Core.Scanning;

namespace RST.Core.Updates;

/// <summary>Where Windows Installer has a product installed.</summary>
public enum InstallContext
{
    /// <summary>MSIINSTALLCONTEXT_USERUNMANAGED: for this user (RST.msi, Gantt-It, Splash-It).</summary>
    User,

    /// <summary>MSIINSTALLCONTEXT_USERMANAGED: for this user, by policy.</summary>
    UserManaged,

    /// <summary>MSIINSTALLCONTEXT_MACHINE: for everyone on this PC.</summary>
    Machine,
}

/// <summary>One installed product instance Windows Installer reports for an UpgradeCode.</summary>
/// <param name="ProductCode">Its ProductCode, upper-case with braces as Windows Installer writes it.</param>
/// <param name="VersionText">Its VersionString as reported.</param>
/// <param name="Context">Where it is installed.</param>
public sealed record InstalledProduct(string ProductCode, string VersionText, InstallContext Context)
{
    public ReleaseVersion? Version => ReleaseVersion.TryParseInstalled(VersionText);
}

/// <summary>Finds installed products by UpgradeCode. RST.Tray's MsiProducts is the real one; tests fake it.</summary>
public interface IInstalledProducts
{
    /// <summary>Every installed instance for <paramref name="upgradeCode"/> this user can see. Never throws.</summary>
    IReadOnlyList<InstalledProduct> Related(string upgradeCode);
}

/// <summary>How a product is present.</summary>
public enum InstallKind
{
    NotInstalled,

    /// <summary>Windows Installer has it (the version and context are known).</summary>
    Installer,

    /// <summary>Only its .addin was found: copied by hand, not managed by Windows Installer or RST.</summary>
    Manual,
}

/// <param name="Kind">How it is present.</param>
/// <param name="Installed">The Windows Installer instance shown (the highest version when there are several).</param>
/// <param name="ManifestPath">For <see cref="InstallKind.Manual"/>: the .addin that holds its ClientId.</param>
public sealed record ProductInstallation(InstallKind Kind, InstalledProduct? Installed = null, string? ManifestPath = null)
{
    public static ProductInstallation None { get; } = new(InstallKind.NotInstalled);

    public ReleaseVersion? Version => Installed?.Version;
}

/// <summary>What the Apps and Updates pages show for one product.</summary>
public enum ProductState
{
    NotInstalled,

    /// <summary>Installed, latest not known (not checked yet, or the check failed).</summary>
    Installed,

    UpToDate,

    UpdateAvailable,

    /// <summary>Installed by hand: updates not managed by RST.</summary>
    InstalledManually,
}

/// <param name="Product">The product.</param>
/// <param name="Installation">What is installed.</param>
/// <param name="Latest">The latest full release, when the last check found one.</param>
/// <param name="LatestFailed">The last check for this product failed ("couldn't check").</param>
public sealed record ProductStatus(ProductInfo Product, ProductInstallation Installation, ReleaseVersion? Latest, bool LatestFailed)
{
    public ProductState State => Compute(Installation, Latest);

    /// <summary>The state rule. Manual wins over everything; an Installer version that is not a.b.c counts as unknown.</summary>
    public static ProductState Compute(ProductInstallation installation, ReleaseVersion? latest)
    {
        ArgumentNullException.ThrowIfNull(installation);
        switch (installation.Kind)
        {
            case InstallKind.Manual:
                return ProductState.InstalledManually;
            case InstallKind.NotInstalled:
                return ProductState.NotInstalled;
        }
        if (latest is not { } newest || installation.Version is not { } current) return ProductState.Installed;
        return newest > current ? ProductState.UpdateAvailable : ProductState.UpToDate;
    }
}

public static class ProductDetection
{
    /// <summary>The first Revit year rst-c supports (decision #21). Later years are found by their folders.</summary>
    public const int FirstRevitYear = 2025;

    /// <summary>
    /// Decides how <paramref name="product"/> is present from what Windows Installer reports
    /// (<paramref name="installed"/>, for its UpgradeCodes) and the ClientIds found in manifests.
    /// </summary>
    public static ProductInstallation Decide(
        ProductInfo product,
        IReadOnlyList<InstalledProduct> installed,
        IReadOnlyDictionary<string, string> manifestClientIds)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(manifestClientIds);
        if (installed.Count > 0)
        {
            var shown = installed
                .OrderByDescending(p => p.Version ?? new ReleaseVersion(-1, 0, 0))
                .ThenBy(p => p.Context)
                .First();
            return new ProductInstallation(InstallKind.Installer, shown);
        }
        if (product.AddinClientId is { } clientId && manifestClientIds.TryGetValue(clientId, out var manifest))
            return new ProductInstallation(InstallKind.Manual, ManifestPath: manifest);
        return ProductInstallation.None;
    }

    /// <summary>Every instance for every UpgradeCode of <paramref name="product"/>. Never throws.</summary>
    public static IReadOnlyList<InstalledProduct> Query(IInstalledProducts source, ProductInfo product, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(product);
        var found = new List<InstalledProduct>();
        foreach (var code in product.UpgradeCodes)
        {
            try
            {
                found.AddRange(source.Related(code));
            }
            catch (Exception ex)
            {
                Log(log, $"detection: {product.Name} ({code}) not readable: {ex.Message}");
            }
        }
        return found;
    }

    /// <summary>
    /// The .addin roots to scan for ClientIds: RST.Core's scanner's own search paths
    /// (<see cref="AddinDirectoryScanner"/>: the user and machine Addins folders and both ApplicationPlugins
    /// folders) for every Revit year from <see cref="FirstRevitYear"/> that has an Addins folder. Two differences
    /// from a Loader scan: the Revit install folder is left out (a companion is never put there, and it is large),
    /// and there is no write probe (the tray only reads). Duplicates (ApplicationPlugins, once per year) are dropped.
    /// </summary>
    public static IReadOnlyList<string> ManifestRoots(string? appData, string? programData)
    {
        var roots = new List<string>();
        foreach (var year in RevitYears(appData, programData))
        {
            var paths = AddinDirectoryScanner.BuildSearchPaths(
                appData, programData, programFiles: null, year.ToString(CultureInfo.InvariantCulture), canWrite: _ => true);
            foreach (var path in paths)
                if (path.Kind != AddinPathKind.RevitInstall && !roots.Contains(path.Path, StringComparer.OrdinalIgnoreCase))
                    roots.Add(path.Path);
        }
        return roots;
    }

    /// <summary>The Revit years (2025+) with a folder under either Addins root, ascending.</summary>
    internal static IReadOnlyList<int> RevitYears(string? appData, string? programData)
    {
        var years = new SortedSet<int>();
        foreach (var baseDir in new[] { appData, programData })
        {
            if (string.IsNullOrEmpty(baseDir)) continue;
            var addins = Path.Combine(baseDir, "Autodesk", "Revit", "Addins");
            try
            {
                if (!Directory.Exists(addins)) continue;
                foreach (var dir in Directory.EnumerateDirectories(addins))
                {
                    var name = Path.GetFileName(dir);
                    if (name.Length == 4 && int.TryParse(name, NumberStyles.None, CultureInfo.InvariantCulture, out var year)
                        && year >= FirstRevitYear)
                        years.Add(year);
                }
            }
            catch (Exception)
            {
                // An unreadable folder is skipped, as the scanner skips it.
            }
        }
        return years.ToList();
    }

    /// <summary>The ManifestRoots for this user's %AppData% and %ProgramData%.</summary>
    public static IReadOnlyList<string> ManifestRoots() => ManifestRoots(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));

    /// <summary>
    /// ClientId (lower-case GUID) to the first manifest declaring it, across <paramref name="roots"/>, read with
    /// RST.Core's own parser. Disabled manifests (<c>.addin.RSTdisabled</c>) count: the add-in is still there.
    /// Unreadable files are skipped. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ManifestClientIds(IEnumerable<string> roots, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            try
            {
                foreach (var manifest in AddinManifestParser.ParseDirectory(root, onSkip: null))
                    foreach (var entry in manifest.Entries)
                        if (ProductCatalog.NormalizeGuid(entry.AddinId)?.ToLowerInvariant() is { } id && !ids.ContainsKey(id))
                            ids[id] = manifest.FilePath;
            }
            catch (Exception ex)
            {
                Log(log, $"detection: manifests under {root} not readable: {ex.Message}");
            }
        }
        return ids;
    }

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }
}
