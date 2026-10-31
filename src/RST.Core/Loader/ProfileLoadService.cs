// rst-c's own (not vendored): LoaderBridge's LoadProfile and UnloadProfile orchestration, moved
// out of the bridge (spec #16, WU3): write the active profile and the prefs entry, run the add-in
// changes, schedule the live ribbon switch, and work out whether Revit must restart.
//
// Spec #18 (feature #16) changed the add-in step: it runs the plan the user confirmed in the
// Confirm Add-in Changes dialog (LoadRequest.Plan) through RST.Core.Activation, and nothing else,
// so what was shown is what runs (D15). A request without a plan changes no add-in: that is the
// Builder's ribbon-only auto-reload (decision #16.4). Unload switches back on what rst-c switched off.
//
// The load runs in three steps so a window can keep the file work off the UI thread (spec #16,
// Planner default 8): Begin (UI thread: files), RunAddinPasses (any thread: the add-in changes),
// Finish (UI thread: the Revit scheduler). Load runs all three in a row.

using RST.Core.Activation;
using RST.Core.Configuration;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>
/// What Load Profile sends: the profile (id wins over name), the disable flag, the RSTify tabs,
/// and the add-in plan the user confirmed (null changes no add-in).
/// </summary>
public sealed record LoadRequest(string Name, string? Id, bool DisableNonRequired, IReadOnlyList<string> HiddenTabs, ActivationPlan? Plan = null);

/// <summary>A required add-in that is not installed for the running Revit year, with its download link (may be empty).</summary>
public sealed record QaMissingItem(string TabName, string? AddinFile, string Url);

/// <summary>
/// The required-add-in part of the report shown after every load: what is missing, and the
/// required add-ins rst-c will not turn on (switched off by the user, or locked off by IT).
/// </summary>
public sealed record QaCheck(IReadOnlyList<QaMissingItem> NotInstalled, IReadOnlyList<string> RequiredOff)
{
    public static readonly QaCheck Clear = new([], []);

    public bool AllClear => NotInstalled.Count == 0 && RequiredOff.Count == 0;

    /// <summary>The check for <paramref name="profile"/>: missing from the running year, and the plan's required add-ins left off.</summary>
    public static QaCheck For(Profile profile, AddinSessionContext session, ActivationPlan? plan)
    {
        var missing = RequiredAddinRules.NotFound(profile, session)
            .Select(a => new QaMissingItem(a.TabName, a.AddinFile, a.Url.Length > 0 ? a.Url : session.LookupFor(a.TabName)?.Url ?? ""))
            .ToList();
        var off = (plan?.Entries ?? [])
            .Where(e => e.Required && e.Action == PlanAction.NotManaged && e.NotManaged is NotManagedReason.SwitchedOffByUser or NotManagedReason.LockedOffByAdmin)
            .Select(ActivationWording.RequiredOffLine)
            .Distinct()
            .ToList();
        return new QaCheck(missing, off);
    }
}

/// <summary>Load Profile's outcome: the add-in changes (one outcome per planned change) and the required-add-in check.</summary>
public sealed record LoadResult(
    bool Ok,
    IReadOnlyList<string> Warnings,
    bool RestartNeeded,
    IReadOnlyList<ActivationOutcome> AddinChanges,
    QaCheck? Qa,
    IReadOnlyList<string>? AddinNotes = null)
{
    public static LoadResult Fail(string warning) => new(false, [warning], false, [], null);

    /// <summary>The add-in report of this load.</summary>
    public ActivationReport Report => new(ActivationIntent.Load, AddinChanges, AddinNotes ?? []);
}

/// <summary>Unload's outcome: the switch-back-on report (empty when rst-c had switched nothing off).</summary>
public sealed record UnloadOutcome(bool Ok, string? Error, ActivationReport Addins)
{
    public static UnloadOutcome Fail(string error) => new(false, error, ActivationReport.Empty(ActivationIntent.Unload));
}

/// <summary>The first step's state: the resolved profile, or the failure to return.</summary>
public sealed class LoadStart
{
    internal LoadStart(LoadRequest request, ProfileEntry? entry, LoadResult? failure)
    {
        Request = request;
        Entry = entry;
        Failure = failure;
    }

    public LoadRequest Request { get; }

    public ProfileEntry? Entry { get; }

    /// <summary>Set when the load stopped in <see cref="ProfileLoadService.Begin"/> (not found, write failed).</summary>
    public LoadResult? Failure { get; }
}

/// <summary>The add-in step's outcome.</summary>
public sealed record AddinPassResult(ActivationReport Report)
{
    /// <summary>No add-in was touched (no plan, or the Builder's ribbon-only reload).</summary>
    public static readonly AddinPassResult None = new(ActivationReport.Empty(ActivationIntent.Load));
}

/// <summary>Loads and unloads profiles (active_profile.json, prefs, add-in changes, the live ribbon switch).</summary>
public sealed class ProfileLoadService
{
    private readonly LoaderPaths _paths;
    private readonly IProfileSwitchScheduler? _scheduler;
    private readonly IAddinSwitching? _switching;
    private readonly ILoaderLog _log;
    private readonly Func<DateTime> _clock;

    /// <param name="paths">The profile, active-profile and prefs files.</param>
    /// <param name="scheduler">The live ribbon switch; null falls back to "restart Revit".</param>
    /// <param name="switching">The add-in switching; null changes no add-in (the Builder).</param>
    /// <param name="log">Log lines; null drops them.</param>
    /// <param name="clock">active_profile.json's <c>loaded_at</c> (local time); null is <see cref="DateTime.Now"/>.</param>
    public ProfileLoadService(
        LoaderPaths paths,
        IProfileSwitchScheduler? scheduler,
        IAddinSwitching? switching,
        ILoaderLog? log = null,
        Func<DateTime>? clock = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _scheduler = scheduler;
        _switching = switching;
        _log = log ?? NullLoaderLog.Instance;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>All three steps in a row, on the calling thread.</summary>
    public LoadResult Load(LoadRequest request)
    {
        var start = Begin(request);
        return start.Failure ?? Finish(start, RunAddinPasses(start));
    }

    /// <summary>The ribbon only: the files and the live switch, with no add-in change (the Builder's auto-reload).</summary>
    public LoadResult Reload(LoadRequest request)
    {
        var start = Begin(request with { Plan = null });
        return start.Failure ?? Finish(start, AddinPassResult.None);
    }

    /// <summary>
    /// Step 1: resolves the profile, writes active_profile.json and the profile's prefs entry
    /// (hidden tabs and disable flag; the adoption flag is left alone).
    /// </summary>
    public LoadStart Begin(LoadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var hiddenTabs = request.HiddenTabs ?? [];
        var entry = ProfileStore.Resolve(request.Name, request.Id, _paths.ProfilesDir);
        if (entry is null)
        {
            _log.Warning(null, "Loader.load_profile: profile not found name={Name} id={Id}", request.Name, request.Id);
            return new LoadStart(request, null, LoadResult.Fail("Profile not found: " + request.Name));
        }

        var ap = ActiveProfile.FromProfile(entry.Profile, entry.FileName, [.. hiddenTabs], request.DisableNonRequired, _clock());
        try
        {
            ap.Write(_paths.ActiveProfileFile);
        }
        catch (IOException ex)
        {
            _log.Error(ex, "Loader.load_profile: failed writing active profile {Name}", request.Name);
            return new LoadStart(request, entry, LoadResult.Fail("Failed to write active profile: " + ex.Message));
        }

        // This profile's RSTify + disable state goes to its prefs entry, so picking it again in
        // the Loader shows these selections. The adoption flag is the Loader's to manage.
        if (!string.IsNullOrEmpty(entry.Profile.Id))
        {
            try
            {
                var prefs = UserProfilePrefs.Read(_paths.PrefsFile);
                prefs.Set(entry.Profile.Id!, hiddenTabs, request.DisableNonRequired, path: _paths.PrefsFile);
            }
            catch (Exception ex)
            {
                _log.Warning(ex, "Loader.load_profile: failed writing user_profile_prefs for {Name}", request.Name);
            }
        }

        _log.Information("Loader.load_profile OK: name={Name} id={Id} disableNonRequired={Disable} hiddenTabs={HiddenTabs} planChanges={Changes}",
            request.Name, request.Id, request.DisableNonRequired, hiddenTabs.Count, request.Plan?.Changes.Count ?? 0);
        return new LoadStart(request, entry, null);
    }

    /// <summary>
    /// Step 2 (safe off the UI thread): runs the confirmed plan's add-in changes. Every change is
    /// reported; an executor that throws is reported, not swallowed.
    /// </summary>
    public AddinPassResult RunAddinPasses(LoadStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.Entry is null || start.Failure is not null) return AddinPassResult.None;
        var plan = start.Request.Plan;
        if (plan is null || _switching is null || !plan.HasWork)
        {
            return plan is { Notes.Count: > 0 }
                ? new AddinPassResult(ActivationReport.Empty(ActivationIntent.Load, [.. plan.Notes.Select(n => n.Text)]))
                : AddinPassResult.None;
        }
        try
        {
            var report = _switching.Execute(plan);
            AddinMaintenanceService.LogReport(_log, report);
            return new AddinPassResult(report);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.load_profile: add-in changes threw for {Name}", start.Request.Name);
            return new AddinPassResult(new ActivationReport(ActivationIntent.Load,
                plan.Changes.Select(e => ActivationOutcome.For(e, OutcomeResult.NotChanged, ChangeReason.Other, ex.Message)).ToList(),
                [.. plan.Notes.Select(n => n.Text), "RST could not complete the add-in changes: " + ex.Message]));
        }
    }

    /// <summary>
    /// Step 3 (UI thread): schedules the live ribbon rebuild (RST-020; it runs once the modal
    /// window closes). With no scheduler, or when scheduling fails, Revit must restart; any add-in
    /// change also needs a restart (it applies the next time Revit starts).
    /// </summary>
    public LoadResult Finish(LoadStart start, AddinPassResult passes)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(passes);
        if (start.Failure is not null) return start.Failure;

        var restartNeeded = true;
        if (_scheduler is not null)
        {
            try
            {
                _scheduler.Schedule(start.Entry!.Profile);
                restartNeeded = false;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Loader.load_profile: switch scheduler failed; falling back to restart-required for {Name}", start.Request.Name);
                restartNeeded = true;
            }
        }
        if (passes.Report.AnyDone) restartNeeded = true;
        return new LoadResult(true, [], restartNeeded, passes.Report.Outcomes, null, passes.Report.Notes);
    }

    /// <summary>
    /// Unload: blanks active_profile.json, schedules the profile tab's removal
    /// (<c>Schedule(null)</c>) and switches back on every add-in rst-c switched off (decision
    /// #16.2). The profile file itself is kept.
    /// </summary>
    public UnloadOutcome Unload()
    {
        ActiveProfile.WriteBlank(_paths.ActiveProfileFile);
        if (_scheduler is not null)
        {
            try
            {
                _scheduler.Schedule(null);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Loader.unload_profile: switch scheduler failed");
            }
        }

        var report = ActivationReport.Empty(ActivationIntent.Unload);
        if (_switching is not null)
        {
            try
            {
                var plan = _switching.Plan(_switching.Inventory(), ActivationPolicy.Unload);
                AddinMaintenanceService.LogPlan(_log, plan, "Unload");
                report = plan.HasWork
                    ? _switching.Execute(plan)
                    : ActivationReport.Empty(ActivationIntent.Unload, [.. plan.Notes.Select(n => n.Text)]);
                AddinMaintenanceService.LogReport(_log, report);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Loader.unload_profile: switching add-ins back on threw");
                report = ActivationReport.Empty(ActivationIntent.Unload, "RST could not switch its add-ins back on: " + ex.Message);
            }
        }
        _log.Information("Loader.unload_profile OK");
        return new UnloadOutcome(true, null, report);
    }
}
