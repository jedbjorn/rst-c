// rst-c's own (not vendored): every add-in each installed Revit year would load, with its scope,
// bundle year range, ClientIds, and its current state from the user's Add-Ins Manager settings and
// the admin's lock file (spec #18, In Scope 1; AC1). One row per manifest per year: a bundle's
// component counts only in the years its PackageContents.xml names (SC-006), and a manifest found
// twice (or as both X.addin and X.addin.RSTdisabled) is one row.

using RST.Core.Scanning;

namespace RST.Core.Activation;

/// <summary>One add-in (manifest) in one Revit year.</summary>
public sealed record InventoryItem(
    int Year,
    SwitchMechanism Mechanism,
    string Name,
    string? VendorId,
    AddinScope Scope,
    string ManifestPath,
    string FileName,
    IReadOnlyList<string> AddinIds,
    bool Renamed,
    bool StaleRenamedCopy,
    string? BundlePath,
    BundleComponent? Bundle,
    IReadOnlyList<string> DisabledIds,
    AdminLock Lock,
    IReadOnlyList<AddinEntry> Entries)
{
    /// <summary>rst-c's own manifest name and ClientId (src/RST.Bootstrap/RST.addin).</summary>
    public const string RstManifestFileName = "RST.addin";

    public const string RstClientId = "4f8ef7a0-0001-4000-8000-525354000001";

    /// <summary>The suffix v3.0 (and the pyRevit-era RST) renamed disabled manifests with.</summary>
    public const string RenamedSuffix = ".RSTdisabled";

    /// <summary>Where the manifest is on disk now.</summary>
    public string CurrentPath => Renamed ? ManifestPath + RenamedSuffix : ManifestPath;

    /// <summary>RST itself: never switched off.</summary>
    public bool IsRst =>
        string.Equals(FileName, RstManifestFileName, StringComparison.OrdinalIgnoreCase) || AddinIds.Contains(RstClientId);

    /// <summary>Published by Autodesk (VendorId ADSK… or naming Autodesk): never switched off as unused.</summary>
    public bool IsAutodesk => Entries.Any(e => IsAutodeskVendor(e.VendorId)) || IsAutodeskVendor(VendorId);

    /// <summary>Every ClientId has Disabled:true in the user's AddInsSettings.json.</summary>
    public bool SwitchedOffInSettings => Mechanism == SwitchMechanism.SettingsFile && AddinIds.Count > 0 && AddinIds.All(DisabledIds.Contains);

    /// <summary>
    /// The add-in will not load at the next start: renamed (Revit cannot find it), locked off by the
    /// administrator, or switched off in the settings file, unless the administrator locks it on
    /// (IT's AdminSettings.json overrides the user's file; addins-manager-spike.md §6).
    /// </summary>
    public bool IsOff => Renamed || Lock == AdminLock.AlwaysDisabled || (SwitchedOffInSettings && Lock != AdminLock.AlwaysEnabled);

    /// <summary>"name (year)" in the plan and the report.</summary>
    public string Label => Name + " (" + Year.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")";

    public static bool IsAutodeskVendor(string? vendor) =>
        !string.IsNullOrWhiteSpace(vendor)
        && (vendor.Trim().StartsWith("ADSK", StringComparison.OrdinalIgnoreCase)
            || vendor.Contains("autodesk", StringComparison.OrdinalIgnoreCase));
}

/// <summary>A Revit year's settings and lock files as the inventory found them.</summary>
public sealed record InventoryYear(
    RevitYear Revit,
    string SettingsPath,
    SettingsFileState Settings,
    string? SettingsError,
    bool UserDisableAll,
    string AdminPath,
    bool AdminDisableAll)
{
    public int Year => Revit.Year;

    public SwitchMechanism Mechanism => Revit.Mechanism;
}

/// <summary>The add-ins of every installed Revit year.</summary>
public sealed class AddinInventory
{
    public AddinInventory(IReadOnlyList<InventoryYear> years, IReadOnlyList<InventoryItem> items)
    {
        Years = years;
        Items = items;
    }

    public static readonly AddinInventory Empty = new([], []);

    public IReadOnlyList<InventoryYear> Years { get; }

    public IReadOnlyList<InventoryItem> Items { get; }

    public IEnumerable<InventoryItem> For(int year) => Items.Where(i => i.Year == year);

    public InventoryYear? Year(int year) => Years.FirstOrDefault(y => y.Year == year);

    /// <summary>Scans the installed years under the real folders.</summary>
    public static AddinInventory ScanMachine(Action<string, Exception>? onSkip = null)
    {
        var roots = AddinRoots.FromEnvironment();
        return Scan(roots, RevitInstallations.Detect(roots), onSkip);
    }

    /// <summary>Scans <paramref name="years"/> under <paramref name="roots"/>. Unreadable files are skipped (and reported to <paramref name="onSkip"/>).</summary>
    public static AddinInventory Scan(AddinRoots roots, IReadOnlyList<RevitYear> years, Action<string, Exception>? onSkip = null)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var yearRows = new List<InventoryYear>();
        var items = new List<InventoryItem>();
        foreach (var revit in years ?? [])
        {
            var settingsPath = roots.SettingsFile(revit.Year);
            var adminPath = roots.AdminSettingsFile(revit.Year);
            var state = SettingsFileState.Missing;
            string? error = null;
            AddinsSettingsDocument? settings = null;
            var admin = AdminSettings.None;
            if (revit.Mechanism == SwitchMechanism.SettingsFile)
            {
                (state, settings, error) = ReadSettings(settingsPath);
                admin = AdminSettings.Read(adminPath);
            }
            var disabledIds = settings?.DisabledIds() ?? new HashSet<string>();
            yearRows.Add(new InventoryYear(revit, settingsPath, state, error, settings?.DisableAll ?? false, adminPath, admin.DisableAll));

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var root in roots.ManifestRoots(revit.Year, revit.InstallDir))
            {
                foreach (var found in Find(root, revit.Year, onSkip))
                {
                    if (!seen.Add(Full(found.Canonical))) continue;
                    var item = Describe(revit, root.Scope, found, disabledIds, admin, onSkip);
                    if (item is not null) items.Add(item);
                }
            }
        }
        return new AddinInventory(yearRows, items);
    }

    /// <summary>Reads a year's AddInsSettings.json: missing, ok, or unreadable (with why).</summary>
    public static (SettingsFileState State, AddinsSettingsDocument? Document, string? Error) ReadSettings(string path)
    {
        try
        {
            if (!File.Exists(path)) return (SettingsFileState.Missing, null, null);
            var doc = AddinsSettingsDocument.TryParse(File.ReadAllText(path), out var error);
            return doc is null ? (SettingsFileState.Unreadable, null, error) : (SettingsFileState.Ok, doc, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (SettingsFileState.Unreadable, null, ex.Message);
        }
    }

    private sealed record Found(string Canonical, string? Live, string? RenamedPath, string? BundlePath, BundleComponent? Component);

    private static IEnumerable<Found> Find(ManifestRoot root, int year, Action<string, Exception>? onSkip)
    {
        if (!Directory.Exists(root.Path)) yield break;
        switch (root.Kind)
        {
            case ManifestRootKind.AddinsFolder:
                foreach (var f in Group(SafeFiles(root.Path, SearchOption.TopDirectoryOnly, onSkip))) yield return f;
                break;
            case ManifestRootKind.RevitInstall:
                foreach (var f in Group(SafeFiles(root.Path, SearchOption.AllDirectories, onSkip))) yield return f;
                break;
            case ManifestRootKind.ApplicationPlugins:
                foreach (var bundle in SafeDirectories(root.Path, onSkip))
                {
                    if (!bundle.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase)) continue;
                    IReadOnlyList<BundleComponent> components;
                    try
                    {
                        // Revit loads a bundle only through its PackageContents.xml.
                        if (!File.Exists(Path.Combine(bundle, PackageContents.FileName))) continue;
                        components = PackageContents.Read(bundle);
                    }
                    catch (Exception ex)
                    {
                        onSkip?.Invoke(Path.Combine(bundle, PackageContents.FileName), ex);
                        continue;
                    }
                    foreach (var c in components)
                    {
                        if (!c.AppliesTo(year)) continue;
                        var live = File.Exists(c.ManifestPath) ? c.ManifestPath : null;
                        var renamed = File.Exists(c.ManifestPath + InventoryItem.RenamedSuffix) ? c.ManifestPath + InventoryItem.RenamedSuffix : null;
                        if (live is null && renamed is null) continue;
                        yield return new Found(c.ManifestPath, live, renamed, bundle, c);
                    }
                }
                break;
        }
    }

    private static IEnumerable<Found> Group(IEnumerable<string> files)
    {
        var byCanonical = new Dictionary<string, (string Canonical, string? Live, string? Renamed)>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            string canonical;
            bool renamed;
            if (name.EndsWith(".addin", StringComparison.OrdinalIgnoreCase)) (canonical, renamed) = (file, false);
            else if (name.EndsWith(".addin" + InventoryItem.RenamedSuffix, StringComparison.OrdinalIgnoreCase))
                (canonical, renamed) = (file[..^InventoryItem.RenamedSuffix.Length], true);
            else continue;
            if (!byCanonical.TryGetValue(canonical, out var row))
            {
                row = (canonical, null, null);
                order.Add(canonical);
            }
            byCanonical[canonical] = renamed ? row with { Renamed = file } : row with { Live = file };
        }
        foreach (var key in order)
        {
            var r = byCanonical[key];
            yield return new Found(r.Canonical, r.Live, r.Renamed, null, null);
        }
    }

    private static InventoryItem? Describe(
        RevitYear revit, AddinScope scope, Found found, IReadOnlySet<string> disabledIds, AdminSettings admin, Action<string, Exception>? onSkip)
    {
        var path = found.Live ?? found.RenamedPath!;
        AddinManifest? manifest;
        try
        {
            manifest = AddinManifestParser.ParseFile(path);
        }
        catch (Exception ex)
        {
            onSkip?.Invoke(path, ex);
            return null;
        }
        if (manifest is null) return null;

        var ids = manifest.Entries.Select(e => AddinIds.Normalize(e.AddinId)).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        var fileName = Path.GetFileName(found.Canonical);
        var name = manifest.Entries.Select(e => e.Name).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                   ?? Path.GetFileNameWithoutExtension(fileName);
        var vendor = manifest.Entries.Select(e => e.VendorId).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        var lockState = AdminLock.None;
        if (revit.Mechanism == SwitchMechanism.SettingsFile && scope != AddinScope.RevitInstall)
        {
            foreach (var id in ids)
            {
                var l = admin.LockFor(id, found.Canonical);
                if (l == AdminLock.AlwaysDisabled) { lockState = l; break; }
                if (l == AdminLock.AlwaysEnabled) lockState = l;
            }
            if (lockState == AdminLock.None && admin.DisableAll && !manifest.Entries.Any(e => InventoryItem.IsAutodeskVendor(e.VendorId)))
                lockState = AdminLock.AlwaysDisabled;
        }

        var disabled = revit.Mechanism == SwitchMechanism.SettingsFile
            ? ids.Where(disabledIds.Contains).ToList()
            : (IReadOnlyList<string>)[];

        return new InventoryItem(
            revit.Year,
            revit.Mechanism,
            name!,
            vendor,
            scope,
            found.Canonical,
            fileName,
            ids,
            Renamed: found.Live is null,
            StaleRenamedCopy: found.Live is not null && found.RenamedPath is not null,
            found.BundlePath,
            found.Component,
            disabled,
            lockState,
            manifest.Entries);
    }

    private static IEnumerable<string> SafeFiles(string dir, SearchOption option, Action<string, Exception>? onSkip)
    {
        try
        {
            return Directory.EnumerateFiles(dir, "*.addin*", option).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSkip?.Invoke(dir, ex);
            return [];
        }
    }

    private static IEnumerable<string> SafeDirectories(string dir, Action<string, Exception>? onSkip)
    {
        try
        {
            return Directory.EnumerateDirectories(dir).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            onSkip?.Invoke(dir, ex);
            return [];
        }
    }

    private static string Full(string p)
    {
        try { return Path.GetFullPath(p); }
        catch { return p; }
    }
}
