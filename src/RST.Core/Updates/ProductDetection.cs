// rst-c's own (not vendored): what is installed, and what the Apps and Updates pages say about it (spec #20 §6;
// research Q6; F17 WU3).
//
//   1. Windows Installer, by UpgradeCode (RST.Tray's MsiProducts: MsiEnumRelatedProducts, then
//      MsiGetProductInfoEx for the version and the context). This is the real signal.
//   2. Secondary: the companion's ClientId in any add-in an installed Revit year (2025+) would load, found by
//      feature #16's inventory (RST.Core.Activation.AddinInventory over RevitInstallations and AddinRoots), the one
//      source of truth for add-in locations: from 2027, Program Files' Addins\<year> and ApplicationPlugins, and
//      not %ProgramData%, which 2027 ignores; a bundle counts only in the years its PackageContents.xml names
//      (F16 review delta N1). A ClientId with no Windows Installer product means a copy put there by hand:
//      "installed manually: updates not managed by RST".
//
// A per-user install by another Windows user is invisible to this user, which is correct: the pages say "for you".
// The state rules (ProductStatus.Compute) are pure, so RST.Tests covers them on Linux.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using RST.Core.Activation;

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
    /// <summary>
    /// Every installed instance for <paramref name="upgradeCode"/> this user can see. Throws when Windows Installer
    /// cannot be asked (msi.dll missing, an unexpected error code): "none" must never stand in for "couldn't tell".
    /// </summary>
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

    /// <summary>Windows Installer could not be read: nothing is claimed (never "not installed" or "manual").</summary>
    Unknown,
}

/// <param name="Kind">How it is present.</param>
/// <param name="Installed">The Windows Installer instance shown (the highest version when there are several).</param>
/// <param name="ManifestPath">For <see cref="InstallKind.Manual"/>: the .addin that holds its ClientId.</param>
public sealed record ProductInstallation(InstallKind Kind, InstalledProduct? Installed = null, string? ManifestPath = null)
{
    public static ProductInstallation None { get; } = new(InstallKind.NotInstalled);

    public static ProductInstallation CouldNotRead { get; } = new(InstallKind.Unknown);

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

    /// <summary>Windows Installer could not be read ("Unknown (couldn't read installed apps)").</summary>
    Unknown,
}

/// <param name="Product">The product.</param>
/// <param name="Installation">What is installed.</param>
/// <param name="Latest">The latest full release, when the last check found one.</param>
/// <param name="LatestFailed">The last check for this product failed ("couldn't check"); <paramref name="Latest"/> is then the last known one.</param>
/// <param name="NoRelease">The last good check found no full release for the repo ("No release yet").</param>
public sealed record ProductStatus(
    ProductInfo Product, ProductInstallation Installation, ReleaseVersion? Latest, bool LatestFailed, bool NoRelease = false)
{
    public ProductState State => Compute(Installation, Latest, LatestFailed);

    /// <summary>
    /// The state rule. Unknown (Windows Installer unreadable) and Manual win over everything; an Installer version
    /// that is not a.b.c counts as unknown. After a FAILED check "up to date" is never claimed (it would be stale):
    /// the state is Installed. A known newer version stays "update available", which no failure can make untrue.
    /// </summary>
    public static ProductState Compute(ProductInstallation installation, ReleaseVersion? latest, bool latestFailed = false)
    {
        ArgumentNullException.ThrowIfNull(installation);
        switch (installation.Kind)
        {
            case InstallKind.Unknown:
                return ProductState.Unknown;
            case InstallKind.Manual:
                return ProductState.InstalledManually;
            case InstallKind.NotInstalled:
                return ProductState.NotInstalled;
        }
        if (latest is not { } newest || installation.Version is not { } current) return ProductState.Installed;
        if (newest > current) return ProductState.UpdateAvailable;
        return latestFailed ? ProductState.Installed : ProductState.UpToDate;
    }
}

public static class ProductDetection
{
    /// <summary>
    /// Decides how <paramref name="product"/> is present from what Windows Installer reports
    /// (<paramref name="installed"/>, for its UpgradeCodes; null when it could not be read) and the ClientIds
    /// found in manifests. Unreadable is <see cref="InstallKind.Unknown"/>, whatever the manifests say: a product
    /// Windows Installer manages must never be shown as "installed manually" because msi.dll failed.
    /// </summary>
    public static ProductInstallation Decide(
        ProductInfo product,
        IReadOnlyList<InstalledProduct>? installed,
        IReadOnlyDictionary<string, string> manifestClientIds)
    {
        ArgumentNullException.ThrowIfNull(product);
        ArgumentNullException.ThrowIfNull(manifestClientIds);
        if (installed is null) return ProductInstallation.CouldNotRead;
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

    /// <summary>
    /// Every instance for every UpgradeCode of <paramref name="product"/>, or null when Windows Installer could not
    /// be read for any of them (logged). Never throws.
    /// </summary>
    public static IReadOnlyList<InstalledProduct>? Query(IInstalledProducts source, ProductInfo product, Action<string>? log = null)
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
                Log(log, $"detection: {product.Name} ({code}) not readable, shown as unknown: {ex.Message}");
                return null;
            }
        }
        return found;
    }

    /// <summary>
    /// ClientId (lower-case GUID) to the manifest declaring it, for every add-in an installed Revit year (2025+) would
    /// load, from feature #16's inventory (<see cref="AddinInventory"/>): the user's and the machine's Addins folders and
    /// ApplicationPlugins bundles as Revit reads them per year. Revit's own install folder is left out (a companion is
    /// never put there). A manifest switched off by RST 3.0's rename (<c>.addin.RSTdisabled</c>) counts, with its
    /// on-disk path: the add-in is still there. Unreadable files are skipped. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, string> InstalledClientIds(AddinRoots roots, IReadOnlyList<RevitYear> years, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentNullException.ThrowIfNull(years);
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var inventory = AddinInventory.Scan(roots, years, (path, ex) => Log(log, $"detection: {path} not readable: {ex.Message}"));
            foreach (var item in inventory.Items)
            {
                if (item.Scope == AddinScope.RevitInstall) continue;
                foreach (var addinId in item.AddinIds)
                    if (ProductCatalog.NormalizeGuid(addinId)?.ToLowerInvariant() is { } id && !ids.ContainsKey(id))
                        ids[id] = item.CurrentPath;
            }
        }
        catch (Exception ex)
        {
            Log(log, $"detection: add-in scan failed: {ex.Message}");
        }
        return ids;
    }

    /// <summary>
    /// <see cref="InstalledClientIds(AddinRoots, IReadOnlyList{RevitYear}, Action{string})"/> for this user and the Revit
    /// years installed here (decision #10 detection).
    /// </summary>
    public static IReadOnlyDictionary<string, string> InstalledClientIds(Action<string>? log = null)
    {
        try
        {
            var roots = AddinRoots.FromEnvironment();
            return InstalledClientIds(roots, RevitInstallations.Detect(roots), log);
        }
        catch (Exception ex)
        {
            Log(log, $"detection: Revit years not readable: {ex.Message}");
            return new Dictionary<string, string>();
        }
    }

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }
}
