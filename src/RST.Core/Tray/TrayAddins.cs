// rst-c's own (not vendored): the RST tray's Add-ins page over the Loader's own services (spec #20 §3; F17 WU2).
//
// There is no second implementation of add-in switching. This class only composes the RST.Core services the
// Profile Loader's backend composes (RST.UI ProfileLoaderBackend), over the same per-user files:
//   - AddinMaintenanceService: the inventory, the Load plan the Confirm dialog shows, Restore All;
//   - ProfileLoadService (with no ribbon scheduler, so the profile is used at Revit's next start): Apply runs
//     exactly the confirmed plan and writes active_profile.json and the profile's prefs entry, as Load Profile does;
//   - ProfileLibraryService and LoaderPrefsService: the profiles, the active profile and the saved choices.
// Decision #29: the tray and a running Loader meet only in those files (atomic writes; the record's lock-file
// merge, #139). The page's per-year status is the Restore All plan, which the planner computes without writing
// anything: what it would switch back on is exactly what RST switched off and is still off.
//
// RST.Tests proves the tray and the Loader produce the same plans and reports for the same inputs (AC3).

using System.Globalization;
using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Tray;

/// <summary>One installed Revit year on the Add-ins page.</summary>
/// <param name="Year">The Revit year (2025 or later).</param>
/// <param name="Running">Revit of this year is open in this Windows session.</param>
/// <param name="SwitchedOff">The add-ins RST switched off there that are still off, by name.</param>
/// <param name="CouldNotChange">What RST cannot switch back on there, with the reason (the reports' wording).</param>
/// <param name="Notes">The year's notes (an unreadable settings file, "Disable all Add-Ins").</param>
public sealed record TrayAddinYear(
    int Year,
    bool Running,
    IReadOnlyList<string> SwitchedOff,
    IReadOnlyList<string> CouldNotChange,
    IReadOnlyList<string> Notes)
{
    public string Label => "Revit " + Year.ToString(CultureInfo.InvariantCulture);

    public bool IsClear => SwitchedOff.Count == 0 && CouldNotChange.Count == 0;
}

/// <summary>What the Add-ins page shows: the active profile and every installed Revit year.</summary>
/// <param name="ActiveProfile">The loaded profile's name; null when none is loaded.</param>
/// <param name="Years">The installed Revit years, oldest first.</param>
/// <param name="Error">Why the add-ins could not be read; null when they could.</param>
public sealed record TrayAddinStatus(string? ActiveProfile, IReadOnlyList<TrayAddinYear> Years, string? Error = null)
{
    public static TrayAddinStatus Empty { get; } = new(null, []);
}

public sealed class TrayAddinService
{
    private readonly IAddinSwitching _switching;
    private readonly string _ledgerPath;
    private readonly AddinMaintenanceService _addins;
    private readonly ProfileLoadService _load;
    private readonly ProfileLibraryService _library;
    private readonly LoaderPrefsService _prefs;

    /// <param name="paths">The per-user files (%AppData%\RST); the Loader's <see cref="LoaderPaths.Default"/> when null.</param>
    /// <param name="switching">The add-in switching over every installed Revit year when null.</param>
    /// <param name="log">The log lines (the same "Loader.addins…" lines the Loader writes).</param>
    public TrayAddinService(LoaderPaths? paths = null, IAddinSwitching? switching = null, ILoaderLog? log = null)
    {
        var p = paths ?? LoaderPaths.Default;
        var l = log ?? NullLoaderLog.Instance;
        _ledgerPath = Path.Combine(p.Root, ActivationLedger.FileName);
        // The same construction as the Loader's backend: the record beside the other per-user files.
        _switching = switching ?? new AddinSwitching(
            ledgerPath: _ledgerPath,
            onSkip: (path, ex) => l.Debug(ex, "AddinInventory: skipped {Path}", path));
        _addins = new AddinMaintenanceService(p, _switching, l);
        // No ribbon scheduler: the tray runs outside Revit, so the profile is used the next time Revit starts.
        _load = new ProfileLoadService(p, scheduler: null, _switching, l);
        _library = new ProfileLibraryService(p, l);
        _prefs = new LoaderPrefsService(p, l);
    }

    /// <summary>"Revit 2027 is open: restart it to see the change." (spec #20 §3).</summary>
    public static string RunningNote(int year) =>
        "Revit " + year.ToString(CultureInfo.InvariantCulture) + " is open: restart it to see the change.";

    public IReadOnlyList<ProfileEntry> Profiles() => _library.List();

    public ActiveProfile Active() => _library.GetActive();

    /// <summary>The saved per-profile choices (after the Loader's one-time migration, as the Loader reads them).</summary>
    public UserProfilePrefs Prefs() => _prefs.Read();

    /// <summary>
    /// The page's status: the active profile and, per installed Revit year, what RST switched off and what it could
    /// not switch back on. Pure: nothing is written. Never throws.
    /// </summary>
    public TrayAddinStatus Status(IReadOnlyCollection<int>? runningYears = null)
    {
        string? active = null;
        try
        {
            var ap = _library.GetActive();
            if (!ap.IsBlank) active = ap.ProfileName;
            var inventory = _switching.Inventory();
            var plan = _switching.Plan(inventory, ActivationPolicy.RestoreAll);
            var ledger = ActivationLedger.Load(_ledgerPath);
            var years = inventory.Years
                .Select(y => YearStatus(y.Year, plan, ledger, runningYears?.Contains(y.Year) == true))
                .ToList();
            return new TrayAddinStatus(active, years);
        }
        catch (Exception ex)
        {
            return new TrayAddinStatus(active, [], ex.Message);
        }
    }

    /// <summary>
    /// One year from the Restore All plan: its Enable entries are RST's own switches that are still off. One RST
    /// could not undo before (a v3.0 rename of an all-users add-in, kept in the record) and one the plan already
    /// knows is blocked (an admin lock, an unreadable file) are "could not change", in the reports' words.
    /// </summary>
    public static TrayAddinYear YearStatus(int year, ActivationPlan plan, ActivationLedger ledger, bool running)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(ledger);
        var ours = plan.Entries.Where(e => e.Year == year && e.Action == PlanAction.Enable).ToList();
        var switchedOff = new List<string>();
        var couldNot = new List<string>();
        foreach (var e in ours)
        {
            if (e.ExpectedBlock is not null)
                couldNot.Add(ActivationWording.BlockedLine(e));
            else if (e.Renamed && ledger.HasV30Rename(e.Year, e.ManifestPath))
                couldNot.Add(ActivationWording.NotChangedLine(ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.NeedsAdmin)));
            else if (!switchedOff.Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                switchedOff.Add(e.Name);
        }
        var notes = plan.Notes.Where(n => n.Year == year).Select(n => n.Text).ToList();
        return new TrayAddinYear(year, running, switchedOff, couldNot.Distinct().ToList(), notes);
    }

    /// <summary>
    /// The saved "Disable unused add-ins" and RSTify choices for <paramref name="entry"/>, as the Loader seeds them
    /// when the profile is picked: the profile's prefs entry, else off and no hidden tabs.
    /// </summary>
    public static (bool DisableUnused, IReadOnlyList<string> HiddenTabs) Choices(ProfileEntry entry, UserProfilePrefs prefs)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(prefs);
        var saved = string.IsNullOrEmpty(entry.Profile.Id) ? null : prefs.Get(entry.Profile.Id!);
        return saved is null ? (false, []) : (saved.DisableUnusedAddins, [.. saved.HiddenTabs ?? []]);
    }

    /// <summary>What Apply would change (the Loader's Load plan; the Confirm dialog shows it). Pure.</summary>
    public LoadPlanOutcome PlanApply(ProfileEntry entry, bool disableUnused)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _addins.PlanLoad(entry.Profile.ProfileName, entry.Profile.Id, disableUnused);
    }

    /// <summary>
    /// Apply profile: the Loader's Load with the confirmed <paramref name="plan"/> (null changes no add-in). Writes the
    /// active profile and its prefs entry, runs exactly the plan, and reports it. The profile's ribbon and the
    /// add-in changes apply the next time Revit starts.
    /// </summary>
    public LoadResult Apply(ProfileEntry entry, bool disableUnused, IReadOnlyList<string> hiddenTabs, ActivationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return _load.Load(new LoadRequest(entry.Profile.ProfileName, entry.Profile.Id, disableUnused, hiddenTabs ?? [], plan));
    }

    /// <summary>Restore All Add-ins: switches back on only what RST switched off, in every installed year.</summary>
    public RestoreOutcome RestoreAll() => _addins.RestoreAll();

    /// <summary>The required-add-in part of the Apply report: the plan's required add-ins left off (no running year, so nothing is "missing").</summary>
    public static QaCheck QaFor(ProfileEntry entry, ActivationPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return QaCheck.For(entry.Profile, new AddinSessionContext(), plan) with { NotInstalled = [] };
    }

    /// <summary>The report with one "Revit &lt;year&gt; is open" note per running year (spec #20 §3).</summary>
    public static ActivationReport WithRunningNotes(ActivationReport report, IEnumerable<int>? runningYears)
    {
        ArgumentNullException.ThrowIfNull(report);
        var notes = (runningYears ?? []).Distinct().Order().Select(RunningNote).ToList();
        return notes.Count == 0 ? report : report with { Notes = [.. report.Notes, .. notes] };
    }

    /// <summary>
    /// The Revit year a running Revit.exe belongs to: the installed year whose folder holds it, else its file
    /// version's major (26.x is 2026). Null when it is neither.
    /// </summary>
    public static int? RevitYearOf(string? exePath, int? fileMajorVersion, IReadOnlyList<RevitYear> installed)
    {
        ArgumentNullException.ThrowIfNull(installed);
        if (!string.IsNullOrEmpty(exePath))
        {
            var dir = Path.GetDirectoryName(exePath);
            foreach (var y in installed)
                if (y.InstallDir is { Length: > 0 } install
                    && string.Equals(Path.TrimEndingDirectorySeparator(install), Path.TrimEndingDirectorySeparator(dir ?? ""), StringComparison.OrdinalIgnoreCase))
                    return y.Year;
        }
        if (fileMajorVersion is >= 25 and < 100)
        {
            var year = 2000 + fileMajorVersion.Value;
            if (year >= RevitInstallations.MinimumYear) return year;
        }
        return null;
    }

    /// <summary>The installed Revit years, as the inventory finds them (for <see cref="RevitYearOf"/>).</summary>
    public static IReadOnlyList<RevitYear> InstalledRevit() => RevitInstallations.Detect(AddinRoots.FromEnvironment());
}
