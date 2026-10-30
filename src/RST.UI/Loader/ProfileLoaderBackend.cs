// rst-c's own (not vendored): the Profile Loader view-model's port to the RST.Core Loader
// services (spec #16, WU3). The real backend only composes those RST.Core services (the ones the
// Builder's backend also calls), so the WPF Loader and the WPF Builder share one copy of the logic.
// Spec #18 (feature #16): the add-in calls go through RST.Core.Activation (inventory, plan,
// executor), the same code the v3.1 tray service will host.

using RST.Core.Activation;
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

    /// <summary>The add-ins of every installed Revit year (the status pills read the running year's).</summary>
    AddinInventory GetAddinInventory();

    OpResult RecordPrefs(string profileId, IReadOnlyList<string> hiddenTabs, bool disableUnused, bool presetsAdopted);

    OpResult RecordOpacityOverride(string profileId, int? opacity);

    ImportOutcome Import(string zipPath);

    /// <summary>What Load Profile would change for the profile (Confirm Add-in Changes shows it; the load runs it).</summary>
    LoadPlanOutcome PlanLoad(string name, string? id, bool disableUnused);

    /// <summary>Load Profile: runs <see cref="LoadRequest.Plan"/>. The add-in changes run off the calling (UI) thread.</summary>
    Task<LoadResult> LoadAsync(LoadRequest request);

    /// <summary>Unload, which switches back on what rst-c switched off.</summary>
    UnloadOutcome Unload();

    DeleteOutcome Delete(string name, string? id);

    RestoreOutcome RestoreAll();

    /// <summary>The one-time move of RST 3.0's renamed manifests; null when there was nothing to move.</summary>
    ActivationReport? MigrateLegacy();
}

/// <summary>The real backend: the RST.Core Loader services over %AppData%\RST and every installed Revit year's add-ins.</summary>
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
    /// <param name="switching">The add-in switching; null is every installed Revit year on this machine.</param>
    /// <param name="lookupPath">addin_lookup.json; null is the shipped Assets\lookup copy.</param>
    /// <param name="clock">active_profile.json's loaded_at; null is now.</param>
    public ProfileLoaderBackend(
        string revitVersion,
        IProfileSwitchScheduler? switchScheduler,
        IReadOnlyList<string>? allTabs,
        LoaderPaths? paths = null,
        IAddinSwitching? switching = null,
        string? lookupPath = null,
        Func<DateTime>? clock = null)
    {
        RevitVersion = revitVersion ?? "";
        AllTabs = allTabs ?? [];
        var p = paths ?? LoaderPaths.Default;
        var log = SerilogLoaderLog.Instance;
        var addins = switching ?? new AddinSwitching(
            ledgerPath: System.IO.Path.Combine(p.Root, ActivationLedger.FileName),
            onSkip: (path, ex) => log.Debug(ex, "AddinInventory: skipped {Path}", path));
        _library = new ProfileLibraryService(p, log);
        _load = new ProfileLoadService(p, switchScheduler, addins, log, clock);
        _prefs = new LoaderPrefsService(p, log);
        _addins = new AddinMaintenanceService(p, addins, log);
        _lookup = new AddinLookupService(lookupPath ?? LoaderAssets.LookupPath, log);
    }

    public string RevitVersion { get; }

    public IReadOnlyList<string> AllTabs { get; }

    public IReadOnlyList<ProfileEntry> GetProfiles() => _library.List();

    public ActiveProfile GetActiveProfile() => _library.GetActive();

    public UserProfilePrefs GetPrefs() => _prefs.Read();

    public IReadOnlyDictionary<string, AddinLookupEntry> GetAddinLookup() => _lookup.Read();

    public AddinInventory GetAddinInventory() => _addins.Inventory();

    public OpResult RecordPrefs(string profileId, IReadOnlyList<string> hiddenTabs, bool disableUnused, bool presetsAdopted) =>
        _prefs.Record(profileId, hiddenTabs, disableUnused, presetsAdopted);

    public OpResult RecordOpacityOverride(string profileId, int? opacity) => _prefs.RecordOpacityOverride(profileId, opacity);

    public ImportOutcome Import(string zipPath) => _library.Import(zipPath);

    public LoadPlanOutcome PlanLoad(string name, string? id, bool disableUnused) => _addins.PlanLoad(name, id, disableUnused);

    public async Task<LoadResult> LoadAsync(LoadRequest request)
    {
        // Spec #16, Planner default 8: the files are written here (UI thread), the add-in changes
        // run on the thread pool, and the Revit scheduler is called back on the UI thread.
        var start = _load.Begin(request);
        if (start.Failure is not null) return start.Failure;
        var passes = await Task.Run(() => _load.RunAddinPasses(start));
        return _load.Finish(start, passes);
    }

    public UnloadOutcome Unload() => _load.Unload();

    public DeleteOutcome Delete(string name, string? id) => _library.Delete(name, id);

    public RestoreOutcome RestoreAll() => _addins.RestoreAll();

    public ActivationReport? MigrateLegacy() => _addins.MigrateLegacy();
}
