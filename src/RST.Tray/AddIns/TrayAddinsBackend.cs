// rst-c's own (not vendored): what the tray's Add-ins page reads and runs (spec #20 §3; F17 WU2). The real backend
// is RST.Core's TrayAddinService (the Profile Loader's own services) plus one Windows read: which Revit years are
// open in this session, for "Revit <year> is open: restart it to see the change". RuntimeTests fakes it.

using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.Core.Tray;
using RST.UI.Loader;
using RST.Core.Logging;

namespace RST.Tray.AddIns;

/// <summary>Everything the Add-ins page reads and runs. Called off the UI thread.</summary>
internal interface ITrayAddinsBackend
{
    /// <summary>The active profile and every installed Revit year's switches.</summary>
    TrayAddinStatus Status(IReadOnlyCollection<int> runningYears);

    IReadOnlyList<ProfileEntry> Profiles();

    UserProfilePrefs Prefs();

    /// <summary>The Revit years open in this Windows session.</summary>
    IReadOnlyList<int> RunningYears();

    /// <summary>The Loader's Load plan for the profile (what Confirm Add-in Changes shows). Pure.</summary>
    LoadPlanOutcome PlanApply(ProfileEntry entry, bool disableUnused);

    /// <summary>The Loader's Load with the confirmed plan.</summary>
    LoadResult Apply(ProfileEntry entry, bool disableUnused, IReadOnlyList<string> hiddenTabs, ActivationPlan? plan);

    /// <summary>Starts a new instance of this installed Revit year. Returns its pid; throws if it cannot start.</summary>
    int LaunchRevit(int year);

    /// <summary>The Loader's Restore All Add-ins.</summary>
    RestoreOutcome RestoreAll();

    /// <summary>Records the answer to the Loader's "Adopt RSTify Presets?" and returns what was recorded.</summary>
    (bool DisableUnused, IReadOnlyList<string> HiddenTabs) AdoptPresets(ProfileEntry entry, bool adopt);
}

/// <summary>The real backend: the per-user files under %AppData%\RST and the installed Revit years.</summary>
internal sealed class TrayAddinsBackend : ITrayAddinsBackend
{
    private readonly TrayAddinService _service;

    public TrayAddinsBackend(TrayAddinService? service = null)
    {
        // The Loader services' lines ("Loader.addins.plan …", "Loader.addins.report …") go to the tray's log.
        _service = service ?? new TrayAddinService(log: SerilogLoaderLog.Instance);
    }

    public TrayAddinStatus Status(IReadOnlyCollection<int> runningYears) => _service.Status(runningYears);

    public IReadOnlyList<ProfileEntry> Profiles() => _service.Profiles();

    public UserProfilePrefs Prefs() => _service.Prefs();

    public LoadPlanOutcome PlanApply(ProfileEntry entry, bool disableUnused) => _service.PlanApply(entry, disableUnused);

    public LoadResult Apply(ProfileEntry entry, bool disableUnused, IReadOnlyList<string> hiddenTabs, ActivationPlan? plan) =>
        _service.Apply(entry, disableUnused, hiddenTabs, plan);

    public int LaunchRevit(int year) => new RevitLauncher().Launch(year);

    public RestoreOutcome RestoreAll() => _service.RestoreAll();

    /// <summary>The Revit years open in this session (the tray's one "is Revit running" read).</summary>
    public IReadOnlyList<int> RunningYears() => RunningRevit.Years();

    public (bool DisableUnused, IReadOnlyList<string> HiddenTabs) AdoptPresets(ProfileEntry entry, bool adopt) => _service.AdoptPresets(entry, adopt);
}

/// <summary>Nothing installed, no profiles: the page before a backend exists (and in tests that do not need one).</summary>
internal sealed class NoTrayAddinsBackend : ITrayAddinsBackend
{
    public static readonly NoTrayAddinsBackend Instance = new();

    public TrayAddinStatus Status(IReadOnlyCollection<int> runningYears) => TrayAddinStatus.Empty;

    public IReadOnlyList<ProfileEntry> Profiles() => [];

    public UserProfilePrefs Prefs() => new();

    public IReadOnlyList<int> RunningYears() => [];

    public LoadPlanOutcome PlanApply(ProfileEntry entry, bool disableUnused) => new("No add-ins here.", null);

    public LoadResult Apply(ProfileEntry entry, bool disableUnused, IReadOnlyList<string> hiddenTabs, ActivationPlan? plan) =>
        LoadResult.Fail("No add-ins here.");

    public int LaunchRevit(int year) => throw new InvalidOperationException(Resources.AddInsStrings.RevitNotFound(year));

    public RestoreOutcome RestoreAll() => new(true, null, ActivationReport.Empty(ActivationIntent.RestoreAll));

    public (bool DisableUnused, IReadOnlyList<string> HiddenTabs) AdoptPresets(ProfileEntry entry, bool adopt) => (false, []);
}
