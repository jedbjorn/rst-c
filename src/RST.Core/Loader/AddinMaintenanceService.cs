// rst-c's own (not vendored): the Loader's add-in operations outside Load and Unload (spec #16,
// WU3; rebuilt for spec #18, feature #16): the inventory for the status pills, the plan the Confirm
// Add-in Changes dialog shows (by profile id first, then name), Restore All Add-ins, and the
// one-time move of v3.0's renamed manifests. Every add-in outcome is logged, one line per change,
// so the log, the report dialog and the disk can be compared.

using RST.Core.Activation;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>The plan for Load Profile, or why there is none.</summary>
public sealed record LoadPlanOutcome(string? Error, ActivationPlan? Plan);

/// <summary>Restore All Add-ins' outcome: the report of what was switched back on and what could not be.</summary>
public sealed record RestoreOutcome(bool Ok, string? Error, ActivationReport? Addins)
{
    public bool RestartNeeded => Addins?.AnyDone == true;
}

public sealed class AddinMaintenanceService
{
    private readonly LoaderPaths _paths;
    private readonly IAddinSwitching _switching;
    private readonly ILoaderLog _log;

    public AddinMaintenanceService(LoaderPaths paths, IAddinSwitching switching, ILoaderLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _switching = switching ?? throw new ArgumentNullException(nameof(switching));
        _log = log ?? NullLoaderLog.Instance;
    }

    /// <summary>The add-ins of every installed Revit year (empty, logged, when the scan fails).</summary>
    public AddinInventory Inventory()
    {
        try
        {
            var inventory = _switching.Inventory();
            _log.Information("Loader.addins: inventory years={Years} items={Items}",
                string.Join(",", inventory.Years.Select(y => y.Year + ":" + y.Settings)), inventory.Items.Count);
            return inventory;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.addins: inventory scan threw");
            return AddinInventory.Empty;
        }
    }

    /// <summary>
    /// What Load Profile would change for the profile (by id, else name), with "Disable unused"
    /// set as given. Pure: nothing is written. A profile that cannot be found gives an error.
    /// </summary>
    public LoadPlanOutcome PlanLoad(string? name, string? id, bool disableUnused)
    {
        var entry = ProfileStore.Resolve(name, id, _paths.ProfilesDir);
        if (entry is null)
        {
            _log.Warning(null, "Loader.addins.plan: profile not found {Name} id={Id}", name, id);
            return new LoadPlanOutcome("Profile not found: " + name, null);
        }
        var plan = _switching.Plan(_switching.Inventory(), ActivationPolicy.Load(entry.Profile.RequiredAddins, disableUnused));
        LogPlan(_log, plan, name);
        return new LoadPlanOutcome(null, plan);
    }

    /// <summary>Switches back on everything rst-c switched off, in every installed year (effective at the next start).</summary>
    public RestoreOutcome RestoreAll()
    {
        try
        {
            var plan = _switching.Plan(_switching.Inventory(), ActivationPolicy.RestoreAll);
            LogPlan(_log, plan, "Restore All");
            var report = _switching.Execute(plan);
            LogReport(_log, report);
            return new RestoreOutcome(true, null, report);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.addins.restore_all threw");
            return new RestoreOutcome(false, ex.Message, null);
        }
    }

    /// <summary>The one-time move of v3.0's renamed manifests; null when there was nothing to move.</summary>
    public ActivationReport? MigrateLegacy()
    {
        try
        {
            var report = _switching.MigrateLegacy();
            if (report is not null) LogReport(_log, report);
            return report;
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.addins.migrate threw");
            return null;
        }
    }

    public static void LogPlan(ILoaderLog log, ActivationPlan plan, string? what)
    {
        log.Information("Loader.addins.plan {What}: intent={Intent} disableUnused={Disable} entries={Entries} changes={Changes} years={Years}",
            what, plan.Intent, plan.DisableUnused, plan.Entries.Count, plan.Changes.Count, string.Join(",", plan.Years));
        foreach (var e in plan.Changes)
            log.Information("Loader.addins.plan: {Action} {Name} ({Year}) scope={Scope} renamed={Renamed} ids={Ids} blocked={Blocked} path={Path}",
                e.Action, e.Name, e.Year, e.Scope, e.Renamed, string.Join(",", e.TargetIds), e.ExpectedBlock, e.ManifestPath);
    }

    public static void LogReport(ILoaderLog log, ActivationReport report)
    {
        log.Information("Loader.addins.report {Intent}: done={Done} notChanged={NotChanged}", report.Intent, report.Done.Count, report.NotChanged.Count);
        foreach (var o in report.Outcomes)
            log.Information("Loader.addins.report: {Action}{Migrated} {Name} ({Year}) scope={Scope} → {Result} {Reason} {Detail}",
                o.Action, o.Migrated ? " (v3.0 move)" : "", o.Name, o.Year, o.Scope, o.Result, o.Reason, o.Detail);
        foreach (var n in report.Notes) log.Information("Loader.addins.report note: {Note}", n);
    }
}
