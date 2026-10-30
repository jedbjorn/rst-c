// rst-c's own (not vendored): LoaderBridge's LoadProfile and UnloadProfile orchestration, moved
// out of the bridge (spec #16, WU3) with unchanged behavior: write the active profile and the
// prefs entry, classify the required add-ins and auto-restore the disabled ones (QA), run the
// disable pass, schedule the live ribbon switch, and work out whether Revit must restart.
//
// The load runs in three steps so a window can keep the add-in renames off the UI thread
// (spec #16, Planner default 8): Begin (UI thread: files), RunAddinPasses (any thread: the
// .addin renames), Finish (UI thread: the Revit scheduler). Load runs all three in a row, as the
// bridge does.

using RST.Core.AddIns;
using RST.Core.Configuration;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>What Load Profile sends: the profile (id wins over name), the disable flag and the RSTify tabs.</summary>
public sealed record LoadRequest(string Name, string? Id, bool DisableNonRequired, IReadOnlyList<string> HiddenTabs);

/// <summary>A required add-in the QA pass found disabled and renamed back (tab name — file).</summary>
public sealed record QaRestoredItem(string TabName, string? AddinFile);

/// <summary>A required add-in the QA pass found missing, with its download link (may be empty).</summary>
public sealed record QaMissingItem(string TabName, string? AddinFile, string Url);

/// <summary>The "Add-in Check" result shown after every successful load.</summary>
public sealed record QaCheck(IReadOnlyList<QaRestoredItem> Restored, IReadOnlyList<QaMissingItem> NotInstalled)
{
    public static readonly QaCheck Clear = new([], []);

    public bool AllClear => Restored.Count == 0 && NotInstalled.Count == 0;
}

/// <summary>Load Profile's outcome, as the bridge returned it.</summary>
public sealed record LoadResult(
    bool Ok,
    IReadOnlyList<string> Warnings,
    bool RestartNeeded,
    IReadOnlyList<FailedDisable> FailedDisables,
    QaCheck? Qa)
{
    public static LoadResult Fail(string warning) => new(false, [warning], false, [], null);
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

/// <summary>The add-in step's outcome: the QA lists, the failed disables, and whether either forces a restart.</summary>
public sealed record AddinPassResult(QaCheck Qa, bool QaForcedRestart, IReadOnlyList<FailedDisable> FailedDisables, bool DisableForcedRestart);

/// <summary>Loads and unloads profiles (active_profile.json, prefs, add-in renames, the live ribbon switch).</summary>
public sealed class ProfileLoadService
{
    private readonly LoaderPaths _paths;
    private readonly IReadOnlyList<string> _ribbonTabs;
    private readonly IProfileSwitchScheduler? _scheduler;
    private readonly IAddinPasses _passes;
    private readonly ILoaderLog _log;
    private readonly Func<DateTime> _clock;

    /// <param name="paths">The profile, active-profile and prefs files.</param>
    /// <param name="ribbonTabs">The ribbon tab titles captured before the window opened (QA tier 3).</param>
    /// <param name="scheduler">The live ribbon switch; null falls back to "restart Revit".</param>
    /// <param name="passes">The add-in file operations for the running Revit version.</param>
    /// <param name="log">Log lines; null drops them.</param>
    /// <param name="clock">active_profile.json's <c>loaded_at</c> (local time); null is <see cref="DateTime.Now"/>.</param>
    public ProfileLoadService(
        LoaderPaths paths,
        IReadOnlyList<string>? ribbonTabs,
        IProfileSwitchScheduler? scheduler,
        IAddinPasses passes,
        ILoaderLog? log = null,
        Func<DateTime>? clock = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _ribbonTabs = ribbonTabs ?? [];
        _scheduler = scheduler;
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _log = log ?? NullLoaderLog.Instance;
        _clock = clock ?? (() => DateTime.Now);
    }

    /// <summary>All three steps in a row, on the calling thread (the bridge's LoadProfile).</summary>
    public LoadResult Load(LoadRequest request)
    {
        var start = Begin(request);
        return start.Failure ?? Finish(start, RunAddinPasses(start));
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

        _log.Information("Loader.load_profile OK: name={Name} id={Id} disableNonRequired={Disable} hiddenTabs={HiddenTabs}",
            request.Name, request.Id, request.DisableNonRequired, hiddenTabs.Count);
        return new LoadStart(request, entry, null);
    }

    /// <summary>
    /// Step 2 (safe off the UI thread): classifies the required add-ins, renames the disabled
    /// ones back (forcing a restart when any was), then, when asked, disables the rest.
    /// </summary>
    public AddinPassResult RunAddinPasses(LoadStart start)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (start.Entry is null || start.Failure is not null) return new AddinPassResult(QaCheck.Clear, false, [], false);
        var profile = start.Entry.Profile;
        var name = start.Request.Name;

        var restored = new List<QaRestoredItem>();
        var missing = new List<QaMissingItem>();
        var qaForcedRestart = false;
        try
        {
            var qa = _passes.Classify(profile.RequiredAddins, _ribbonTabs);
            int active = 0, disabled = 0, notInstalled = 0;
            var disabledMatches = new List<RequiredAddin>();
            foreach (var r in qa)
            {
                switch (r.Status)
                {
                    case RequiredAddinStatus.InstalledActive:
                        active++;
                        break;
                    case RequiredAddinStatus.InstalledDisabled:
                        disabled++;
                        disabledMatches.Add(r.Required);
                        restored.Add(new QaRestoredItem(r.Required.TabName, r.MatchedManifest?.FileName ?? r.Required.AddinFile));
                        break;
                    case RequiredAddinStatus.NotInstalled:
                        notInstalled++;
                        missing.Add(new QaMissingItem(r.Required.TabName, r.Required.AddinFile, r.Required.Url ?? ""));
                        break;
                }
            }
            _log.Information("Loader.load_profile QA: active={Active} disabled={Disabled} missing={Missing}", active, disabled, notInstalled);

            if (disabledMatches.Count > 0)
            {
                var restore = _passes.RestoreRequired(disabledMatches,
                    (path, ex) => _log.Warning(ex, "AddinDisabler.RestoreRequired: rename failed for {Path}", path));
                _log.Information("Loader.load_profile QA: auto-restored {Count} required addins, failed={Failed}",
                    restore.RestoredCount, restore.Failed);
                if (restore.RestoredCount > 0) qaForcedRestart = true;
            }
        }
        catch (Exception ex)
        {
            _log.Warning(ex, "Loader.load_profile: QA classification threw — proceeding without auto-restore");
        }

        // Disabling renames .addin to .addin.RSTdisabled; it takes effect on the next launch,
        // so any disable forces a restart.
        var failed = new List<FailedDisable>();
        var disableForcedRestart = false;
        if (start.Request.DisableNonRequired)
        {
            try
            {
                var result = _passes.DisableNonRequired(profile.RequiredAddins,
                    (path, ex) => _log.Warning(ex, "AddinDisabler: rename failed for {Path}", path));
                _log.Information("Loader.load_profile: disabled {Count} addins, skippedReadOnly={ReadOnly}, alreadyDisabled={Already}, failed={Failed}",
                    result.DisabledCount, result.SkippedReadOnly, result.SkippedAlreadyDisabled, result.Failed);
                failed.AddRange(result.FailedFiles.Select(f => new FailedDisable(f)));
                if (result.DisabledCount > 0) disableForcedRestart = true;
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Loader.load_profile: AddinDisabler threw for {Name}", name);
                failed.Add(new FailedDisable("(disable subsystem failure)", Error: ex.Message));
                disableForcedRestart = true; // be conservative: assume the DLLs need a restart
            }
        }

        return new AddinPassResult(new QaCheck(restored, missing), qaForcedRestart, failed, disableForcedRestart);
    }

    /// <summary>
    /// Step 3 (UI thread): schedules the live ribbon rebuild (RST-020; it runs once the modal
    /// window closes). With no scheduler, or when scheduling fails, Revit must restart.
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
        if (passes.DisableForcedRestart || passes.QaForcedRestart) restartNeeded = true;
        return new LoadResult(true, [], restartNeeded, passes.FailedDisables, passes.Qa);
    }

    /// <summary>
    /// Unload: blanks active_profile.json and schedules the profile tab's removal
    /// (<c>Schedule(null)</c>). The profile file itself is kept.
    /// </summary>
    public OpResult Unload()
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
        _log.Information("Loader.unload_profile OK");
        return OpResult.Success;
    }
}
