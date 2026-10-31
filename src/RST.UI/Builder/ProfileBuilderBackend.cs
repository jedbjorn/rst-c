// rst-c's own (not vendored): what the Profile Builder view-model reads and writes (spec #16,
// WU4), replacing the retired LoaderBridge's Builder calls: get_catalog, get_profiles,
// get_active_profile, get_revit_version, get_addin_lookup, get_all_tabs, save_profile,
// export_profile, load_profile (the auto-reload), list_iconpack, and the machine-wide branding
// (load_default_branding, save_default_branding_url, pick_logo_file, clear_default_logo).
// The real backend composes the same RST.Core services the WPF Loader calls.

using System.IO;
using RST.Core.Builder;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Ribbon;
using RST.Core.Scanning;
using RST.UI.Loader;
using RST.Core.Logging;

namespace RST.UI.Builder;

/// <summary>The machine-wide branding: whether a logo is stored (and its file name) and the URL.</summary>
public sealed record BrandingState(bool HasLogo, string? FileName, string? Url)
{
    public static readonly BrandingState None = new(false, null, null);
}

/// <summary>Everything the Profile Builder reads and writes. Tests pass a fake.</summary>
public interface IProfileBuilderBackend
{
    /// <summary>The running Revit version ("2026"), or empty outside Revit (passed back to the auto-reload).</summary>
    string RevitVersion { get; }

    /// <summary>The ribbon's tab titles, captured before the window opened.</summary>
    IReadOnlyList<string> AllTabs { get; }

    /// <summary>The command catalogue, captured before the window opened, in get_catalog's shape.</summary>
    IReadOnlyList<CatalogCommand> Catalog { get; }

    IReadOnlyList<ProfileEntry> GetProfiles();

    ActiveProfile GetActiveProfile();

    IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup();

    SaveOutcome Save(Profile profile);

    /// <summary>After the first save of a profile read from a file with no id: removes that file (WU1 review F6).</summary>
    OpResult RemoveSupersededFile(string fileName);

    OpResult Export(Profile profile, string path);

    BrandingState LoadBranding();

    /// <summary>Stores the branding URL (already normalized; empty clears it).</summary>
    OpResult SaveBrandingUrl(string url);

    /// <summary>Stores the picked image as the machine-wide logo.</summary>
    LogoOutcome StoreLogo(string sourcePath);

    OpResult ClearLogo();

    /// <summary>The auto-reload after saving the active profile: the ribbon only, never an add-in change (spec #18).</summary>
    Task<LoadResult> LoadAsync(LoadRequest request);

    /// <summary>The icon pack catalogue (one entry per design with a complete color set); empty when the pack is missing.</summary>
    IReadOnlyList<IconPackEntry> GetIconPack();
}

/// <summary>The real backend: %AppData%\RST, the shipped lookup, and the running Revit's add-in folders.</summary>
public sealed class ProfileBuilderBackend : IProfileBuilderBackend
{
    private readonly ProfileLibraryService _library;
    private readonly ProfileLoadService _load;
    private readonly ProfileSaveService _save;
    private readonly AddinLookupService _lookup;

    /// <param name="revitVersion">Revit's version number.</param>
    /// <param name="catalog">The scanned command catalogue.</param>
    /// <param name="switchScheduler">The live ribbon switch; null means "restart Revit".</param>
    /// <param name="allTabs">The ribbon's tab titles.</param>
    /// <param name="paths">The per-user files; null is %AppData%\RST.</param>
    /// <param name="lookupPath">addin_lookup.json; null is the shipped Assets\lookup copy.</param>
    public ProfileBuilderBackend(
        string revitVersion,
        IReadOnlyList<ScannedCommand>? catalog,
        IProfileSwitchScheduler? switchScheduler,
        IReadOnlyList<string>? allTabs,
        LoaderPaths? paths = null,
        string? lookupPath = null)
    {
        RevitVersion = revitVersion ?? "";
        AllTabs = allTabs ?? [];
        Catalog = CatalogCommand.FromScanned(catalog, out var promoted);
        var p = paths ?? LoaderPaths.Default;
        var log = SerilogLoaderLog.Instance;
        _library = new ProfileLibraryService(p, log);
        // No add-in switching: the Builder's auto-reload never changes add-ins (spec #18, decision #16.4).
        _load = new ProfileLoadService(p, switchScheduler, switching: null, log);
        _save = new ProfileSaveService(p.ProfilesDir, log);
        _lookup = new AddinLookupService(lookupPath ?? LoaderAssets.LookupPath, log);
        Log.Information("Builder catalog: {Count} commands ({Promoted} host-tab promoted)", Catalog.Count, promoted);
    }

    public string RevitVersion { get; }

    public IReadOnlyList<string> AllTabs { get; }

    public IReadOnlyList<CatalogCommand> Catalog { get; }

    public IReadOnlyList<ProfileEntry> GetProfiles() => _library.List();

    public ActiveProfile GetActiveProfile() => _library.GetActive();

    public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup() => _lookup.Read();

    public SaveOutcome Save(Profile profile) => _save.Save(profile);

    public OpResult RemoveSupersededFile(string fileName) => _save.RemoveSupersededFile(fileName);

    public OpResult Export(Profile profile, string path) => _save.Export(profile, path);

    public BrandingState LoadBranding()
    {
        var defaults = BrandingDefaults.Load();
        var hasLogo = BrandingDefaults.HasLogo;
        return new BrandingState(hasLogo, hasLogo ? BrandingDefaults.LogoFileName : null, defaults.Url);
    }

    public OpResult SaveBrandingUrl(string url)
    {
        try
        {
            var defaults = BrandingDefaults.Load();
            defaults.Url = string.IsNullOrWhiteSpace(url) ? null : url.Trim();
            defaults.Save();
            Log.Information("Builder: default branding url saved (urlSet={UrlSet})", !string.IsNullOrEmpty(defaults.Url));
            return OpResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Builder: save_default_branding_url write failed");
            return OpResult.Fail("Write failed: " + ex.Message);
        }
    }

    public LogoOutcome StoreLogo(string sourcePath) => BrandingLogo.Store(sourcePath);

    public OpResult ClearLogo()
    {
        try
        {
            if (File.Exists(BrandingDefaults.LogoPath))
            {
                File.Delete(BrandingDefaults.LogoPath);
                Log.Information("Builder: default logo deleted {Path}", BrandingDefaults.LogoPath);
            }
            return OpResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Builder: clear_default_logo delete failed");
            return OpResult.Fail("Delete failed: " + ex.Message);
        }
    }

    /// <summary>
    /// list_iconpack (LB:465-505): the shipped pack's catalogue. A missing folder gives an empty
    /// list (logged); a design without a complete color set is left out (logged).
    /// </summary>
    public IReadOnlyList<IconPackEntry> GetIconPack() => IconPackSource.Read(LoaderAssets.IconPackDirectory);

    /// <summary>
    /// The ribbon-only auto-reload (spec #18, In Scope 6; decision #16.4): the active profile's
    /// files and the live ribbon switch, and no add-in change. v3.0 re-ran the disable pass here
    /// with no confirm and ignored its result (D8); add-ins change only through Load Profile.
    /// </summary>
    public Task<LoadResult> LoadAsync(LoadRequest request) => Task.FromResult(_load.Reload(request));
}

/// <summary>The icon pack catalogue as the picker reads it (LoaderBridge.ListIconpackCore without the JSON).</summary>
public static class IconPackSource
{
    public static IReadOnlyList<IconPackEntry> Read(string dir)
    {
        try
        {
            var catalogue = IconPack.BuildCatalogue(dir);
            if (!catalogue.DirectoryExists)
            {
                Log.Warning("Builder.list_iconpack: missing pack dir {Dir}", dir);
                return [];
            }
            foreach (var name in catalogue.IncompleteNames)
                Log.Warning("Builder.list_iconpack: {Name} omitted — incomplete color-variant set in {Dir}", name, dir);
            Log.Information("Builder.list_iconpack OK: {Count} icons from {Dir}", catalogue.Entries.Count, dir);
            return catalogue.Entries;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Builder.list_iconpack threw");
            return [];
        }
    }
}
