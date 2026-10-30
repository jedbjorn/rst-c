// rst-c's own (not vendored): LoaderBridge's GetDisablePreview, RestoreAddins, GetLoadedAddins
// and GetUserConfig, moved out of the bridge (spec #16, WU3). The disable preview now resolves the
// profile by id first, then name (spec #16, Planner default 5: the bridge looked up by name only).

using RST.Core.AddIns;
using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>One "Confirm Add-in Changes" row: the manifest file and its display name (the file stem).</summary>
public sealed record DisablePreviewItem(string DisplayName, string FileName, string FilePath, string? AddinId, string? Assembly, string SourceKind)
{
    public static DisablePreviewItem From(AddinPreviewEntry e) => new(
        Path.GetFileNameWithoutExtension(e.FileName), e.FileName, e.FilePath, e.FirstAddinId, e.FirstAssemblyPath, e.SourceKind.ToString());
}

/// <summary>The disable preview: staying, will be disabled, try disable (read-only path), skipped (already disabled).</summary>
public sealed record DisablePreviewOutcome(
    string? Error,
    IReadOnlyList<DisablePreviewItem> Staying,
    IReadOnlyList<DisablePreviewItem> Disabling,
    IReadOnlyList<DisablePreviewItem> TryDisable,
    IReadOnlyList<DisablePreviewItem> Skipped);

/// <summary>Restore All Add-ins' outcome; the restored file names feed the "Please Restart Revit" toast.</summary>
public sealed record RestoreOutcome(bool Ok, string? Error, bool RestartNeeded, IReadOnlyList<string> Restored, IReadOnlyList<string> Failed);

/// <summary>The disable preview, Restore All, the manifest scan and the user add-in config.</summary>
public sealed class AddinMaintenanceService
{
    private readonly LoaderPaths _paths;
    private readonly IAddinPasses _passes;
    private readonly ILoaderLog _log;

    public AddinMaintenanceService(LoaderPaths paths, IAddinPasses passes, ILoaderLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _passes = passes ?? throw new ArgumentNullException(nameof(passes));
        _log = log ?? NullLoaderLog.Instance;
    }

    /// <summary>
    /// What a disable pass would do for the profile (by id, else name). Pure: nothing is renamed.
    /// A profile that cannot be found gives an <see cref="DisablePreviewOutcome.Error"/>.
    /// </summary>
    public DisablePreviewOutcome Preview(string? name, string? id)
    {
        var entry = ProfileStore.Resolve(name, id, _paths.ProfilesDir);
        if (entry is null)
        {
            _log.Warning(null, "Loader.get_disable_preview: profile not found {Name} id={Id}", name, id);
            return new DisablePreviewOutcome("Profile not found: " + name, [], [], [], []);
        }

        var preview = _passes.Preview(entry.Profile.RequiredAddins);
        _log.Information("Loader.get_disable_preview: name={Name} staying={S} disabling={D} tryDisable={T} skipped={Sk}",
            name, preview.Staying.Count, preview.Disabling.Count, preview.TryDisable.Count, preview.Skipped.Count);
        return new DisablePreviewOutcome(null,
            preview.Staying.Select(DisablePreviewItem.From).ToList(),
            preview.Disabling.Select(DisablePreviewItem.From).ToList(),
            preview.TryDisable.Select(DisablePreviewItem.From).ToList(),
            preview.Skipped.Select(DisablePreviewItem.From).ToList());
    }

    /// <summary>
    /// Renames every .addin.RSTdisabled back to .addin (effective on the next launch, so a
    /// restart is needed when anything was restored).
    /// </summary>
    public RestoreOutcome RestoreAll()
    {
        try
        {
            var result = _passes.RestoreAll((path, ex) => _log.Warning(ex, "AddinDisabler.Restore: rename failed for {Path}", path));
            _log.Information("Loader.restore_addins → restored={Restored}, failed={Failed}", result.RestoredCount, result.Failed);
            return new RestoreOutcome(true, null, result.RestoredCount > 0, result.RestoredFiles, result.FailedFiles);
        }
        catch (Exception ex)
        {
            _log.Error(ex, "Loader.restore_addins: AddinDisabler.RestoreAll threw");
            return new RestoreOutcome(false, ex.Message, false, [], []);
        }
    }

    /// <summary>Every .addin and .addin.RSTdisabled on the search paths (the "Loaded" matcher's manifest list).</summary>
    public IReadOnlyList<LoadedAddin> ScanLoaded()
    {
        var manifests = _passes.Scan((p, ex) => _log.Debug(ex, "AddinDirectoryScanner: skipped {Path}", p));
        _log.Information("Loader.get_loaded_addins → {Count} manifests scanned", manifests.Count);
        return manifests.Select(LoadedAddin.FromManifest).ToList();
    }

    /// <summary>The user add-in config: a stub, always empty (spec #16, Out of Scope).</summary>
    public UserAddinConfig GetUserConfig() => UserAddinConfig.Empty;
}
