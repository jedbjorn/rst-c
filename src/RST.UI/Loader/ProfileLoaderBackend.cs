// rst-c's own (not vendored): the Profile Loader view-model's port to the RST.Core Loader
// services (spec #16, WU3). The real backend only composes those RST.Core services (the ones the
// Builder's backend also calls), so the WPF Loader and the WPF Builder share one copy of the logic.

using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.UI.Loader;

/// <summary>Everything the Profile Loader reads and writes. Tests pass a fake.</summary>
public interface IProfileLoaderBackend
{
    /// <summary>The running Revit version ("2026"), or empty outside Revit.</summary>
    string RevitVersion { get; }

    /// <summary>The ribbon's tab titles, captured before the window opened.</summary>
    IReadOnlyList<string> AllTabs { get; }

    IReadOnlyList<ProfileEntry> GetProfiles();

    ActiveProfile GetActiveProfile();

    /// <summary>The prefs, after the one-time active-profile migration.</summary>
    UserProfilePrefs GetPrefs();

    IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup();

    IReadOnlyList<LoadedAddin> GetLoadedAddins();

    UserAddinConfig GetUserConfig();

    OpResult RecordPrefs(string profileId, IReadOnlyList<string> hiddenTabs, bool disableUnused, bool presetsAdopted);

    OpResult RecordOpacityOverride(string profileId, int? opacity);

    ImportOutcome Import(string zipPath);

    DisablePreviewOutcome GetDisablePreview(string name, string? id);

    /// <summary>Load Profile. The add-in renames run off the calling (UI) thread.</summary>
    Task<LoadResult> LoadAsync(LoadRequest request);

    OpResult Unload();

    DeleteOutcome Delete(string name, string? id);

    RestoreOutcome RestoreAll();
}

/// <summary>The real backend: the RST.Core Loader services over %AppData%\RST and the running Revit's add-in folders.</summary>
public sealed class ProfileLoaderBackend : IProfileLoaderBackend
{
    private readonly ProfileLibraryService _library;
    private readonly ProfileLoadService _load;
    private readonly LoaderPrefsService _prefs;
    private readonly AddinMaintenanceService _addins;
    private readonly AddinLookupService _lookup;

    /// <param name="revitVersion">Revit's version number.</param>
    /// <param name="switchScheduler">The live ribbon switch; null means "restart Revit".</param>
    /// <param name="allTabs">The ribbon's tab titles.</param>
    /// <param name="paths">The per-user files; null is %AppData%\RST.</param>
    /// <param name="passes">The add-in file operations; null is the running Revit's search paths.</param>
    /// <param name="lookupPath">addin_lookup.json; null is the shipped Assets\lookup copy.</param>
    /// <param name="clock">active_profile.json's loaded_at; null is now.</param>
    public ProfileLoaderBackend(
        string revitVersion,
        IProfileSwitchScheduler? switchScheduler,
        IReadOnlyList<string>? allTabs,
        LoaderPaths? paths = null,
        IAddinPasses? passes = null,
        string? lookupPath = null,
        Func<DateTime>? clock = null)
    {
        RevitVersion = revitVersion ?? "";
        AllTabs = allTabs ?? [];
        var p = paths ?? LoaderPaths.Default;
        var log = SerilogLoaderLog.Instance;
        var addinPasses = passes ?? new FileSystemAddinPasses(RevitVersion);
        _library = new ProfileLibraryService(p, log);
        _load = new ProfileLoadService(p, AllTabs, switchScheduler, addinPasses, log, clock);
        _prefs = new LoaderPrefsService(p, log);
        _addins = new AddinMaintenanceService(p, addinPasses, log);
        _lookup = new AddinLookupService(lookupPath ?? LoaderAssets.LookupPath, log);
    }

    public string RevitVersion { get; }

    public IReadOnlyList<string> AllTabs { get; }

    public IReadOnlyList<ProfileEntry> GetProfiles() => _library.List();

    public ActiveProfile GetActiveProfile() => _library.GetActive();

    public UserProfilePrefs GetPrefs() => _prefs.Read();

    public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup() => _lookup.Read();

    public IReadOnlyList<LoadedAddin> GetLoadedAddins() => _addins.ScanLoaded();

    public UserAddinConfig GetUserConfig() => _addins.GetUserConfig();

    public OpResult RecordPrefs(string profileId, IReadOnlyList<string> hiddenTabs, bool disableUnused, bool presetsAdopted) =>
        _prefs.Record(profileId, hiddenTabs, disableUnused, presetsAdopted);

    public OpResult RecordOpacityOverride(string profileId, int? opacity) => _prefs.RecordOpacityOverride(profileId, opacity);

    public ImportOutcome Import(string zipPath) => _library.Import(zipPath);

    public DisablePreviewOutcome GetDisablePreview(string name, string? id) => _addins.Preview(name, id);

    public async Task<LoadResult> LoadAsync(LoadRequest request)
    {
        // Spec #16, Planner default 8: the files are written here (UI thread), the .addin
        // renames run on the thread pool, and the Revit scheduler is called back on the UI thread.
        var start = _load.Begin(request);
        if (start.Failure is not null) return start.Failure;
        var passes = await Task.Run(() => _load.RunAddinPasses(start));
        return _load.Finish(start, passes);
    }

    public OpResult Unload() => _load.Unload();

    public DeleteOutcome Delete(string name, string? id) => _library.Delete(name, id);

    public RestoreOutcome RestoreAll() => _addins.RestoreAll();
}
