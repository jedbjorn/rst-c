// rst-c's own (not vendored): what the Profile Builder view-model reads and writes (spec #16,
// WU4), in place of the Builder's LoaderBridge calls: get_catalog, get_profiles,
// get_active_profile, get_revit_version, get_addin_lookup, get_all_tabs, save_profile,
// export_profile, load_profile (the auto-reload), and the machine-wide branding
// (load_default_branding, save_default_branding_url, pick_logo_file, clear_default_logo).
// The real backend composes the same RST.Core services the bridge and the WPF Loader call.

using System.IO;
using RST.Core.Builder;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Scanning;
using RST.UI.Loader;
using Serilog;

namespace RST.UI.Builder;

/// <summary>The machine-wide branding: whether a logo is stored (and its file name) and the URL.</summary>
public sealed record BrandingState(bool HasLogo, string? FileName, string? Url)
{
    public static readonly BrandingState None = new(false, null, null);

    /// <summary>The header button's dot is filled when a logo or a URL is set.</summary>
    public bool IsSet => HasLogo || !string.IsNullOrEmpty(Url);
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

    OpResult Export(Profile profile, string path);

    BrandingState LoadBranding();

    /// <summary>Stores the branding URL (already normalized; empty clears it).</summary>
    OpResult SaveBrandingUrl(string url);

    /// <summary>Stores the picked image as the machine-wide logo.</summary>
    LogoOutcome StoreLogo(string sourcePath);

    OpResult ClearLogo();

    /// <summary>The auto-reload after saving the active profile. The add-in renames run off the calling (UI) thread.</summary>
    Task<LoadResult> LoadAsync(LoadRequest request);
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
    /// <param name="passes">The add-in file operations; null is the running Revit's search paths.</param>
    /// <param name="lookupPath">addin_lookup.json; null is the shipped Assets\lookup copy.</param>
    public ProfileBuilderBackend(
        string revitVersion,
        IReadOnlyList<ScannedCommand>? catalog,
        IProfileSwitchScheduler? switchScheduler,
        IReadOnlyList<string>? allTabs,
        LoaderPaths? paths = null,
        IAddinPasses? passes = null,
        string? lookupPath = null)
    {
        RevitVersion = revitVersion ?? "";
        AllTabs = allTabs ?? [];
        Catalog = CatalogCommand.FromScanned(catalog, out var promoted);
        var p = paths ?? LoaderPaths.Default;
        var log = SerilogLoaderLog.Instance;
        _library = new ProfileLibraryService(p, log);
        _load = new ProfileLoadService(p, AllTabs, switchScheduler, passes ?? new FileSystemAddinPasses(RevitVersion), log);
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

    public async Task<LoadResult> LoadAsync(LoadRequest request)
    {
        // As the Loader (spec #16, Planner default 8): files on the UI thread, the .addin renames
        // on the thread pool, the Revit scheduler back on the UI thread.
        var start = _load.Begin(request);
        if (start.Failure is not null) return start.Failure;
        var passes = await Task.Run(() => _load.RunAddinPasses(start));
        return _load.Finish(start, passes);
    }
}
