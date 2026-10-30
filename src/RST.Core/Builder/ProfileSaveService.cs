// rst-c's own (not vendored): LoaderBridge's SaveProfile and ExportProfile writes (LB:509-620),
// moved out of the bridge for the WPF Builder (spec #16, WU4) with unchanged behavior and error
// texts. The bridge received the page's profile as JSON and wrote its own deserialized copy; the
// service writes a copy too, so the editor's profile changes only by the id and export date the
// save assigns, exactly as the page's did.

using RST.Core.Configuration;
using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>A save's outcome: the file and the profile as written, or the bridge's error text.</summary>
public sealed record SaveOutcome(bool Ok, string? Error, string? FileName, Profile? Saved)
{
    public static SaveOutcome Fail(string error) => new(false, error, null, null);
}

/// <summary>Writes Builder profiles to the profiles folder, and exports them as .zip packages.</summary>
public sealed class ProfileSaveService
{
    private readonly string _profilesDir;
    private readonly ILoaderLog _log;
    private readonly Func<DateTime> _utcNow;

    /// <param name="profilesDir">%AppData%\RST\profiles (or a test folder).</param>
    /// <param name="log">Log lines; null drops them.</param>
    /// <param name="utcNow">The export date a new profile gets; null is <see cref="DateTime.UtcNow"/>.</param>
    public ProfileSaveService(string profilesDir, ILoaderLog? log = null, Func<DateTime>? utcNow = null)
    {
        _profilesDir = profilesDir ?? throw new ArgumentNullException(nameof(profilesDir));
        _log = log ?? NullLoaderLog.Instance;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>
    /// save_profile: name and tab required, then a new id and today's export date when missing,
    /// then the stacks no panel refers to dropped (spec #17), then the full validation the read path
    /// enforces (a profile saved without it would vanish from every picker), then the atomic write.
    /// </summary>
    public SaveOutcome Save(Profile edited)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var profile = ProfileEditing.Clone(edited);
        if (string.IsNullOrWhiteSpace(profile.ProfileName))
        {
            _log.Warning(null, "Builder.save_profile: missing profile name");
            return SaveOutcome.Fail("Profile name required");
        }
        if (string.IsNullOrWhiteSpace(profile.Tab))
        {
            _log.Warning(null, "Builder.save_profile: missing tab for {Name}", profile.ProfileName);
            return SaveOutcome.Fail("Tab name required");
        }

        if (string.IsNullOrEmpty(profile.Id)) profile.Id = Guid.NewGuid().ToString();
        if (string.IsNullOrEmpty(profile.ExportDate)) profile.ExportDate = _utcNow().ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

        // Spec #17: a stack no panel slot refers to is a warning, and the save drops it.
        foreach (var orphan in ProfileEditing.RemoveOrphanStacks(profile))
            _log.Warning(null, "Builder.save_profile: stack {Stack} is not on any panel; removed from {Name}", orphan, profile.ProfileName);

        // Feature #18 (decision #28): a placed stack of 4+ tools refuses the save, naming its panel.
        if (PanelStacks.SaveBlocker(profile) is { } blocker)
        {
            _log.Warning(null, "Builder.save_profile: {Name} refused: {Blocker}", profile.ProfileName, blocker);
            return SaveOutcome.Fail(blocker);
        }

        var errors = ProfileSerializer.Validate(profile);
        if (errors.Count > 0)
        {
            _log.Warning(null, "Builder.save_profile: validation failed for {Name}: {Errors}", profile.ProfileName, string.Join("; ", errors));
            return SaveOutcome.Fail("Profile validation failed: " + string.Join("; ", errors));
        }

        try
        {
            var fileName = ProfileStore.Save(profile, _profilesDir);
            _log.Information("Builder.save_profile OK: name={Name} id={Id} panels={Panels} stacks={Stacks} → {FileName}",
                profile.ProfileName, profile.Id, profile.Panels.Count, profile.Stacks.Count, fileName);
            return new SaveOutcome(true, null, fileName, profile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Builder.save_profile: write failed for {Name}", profile.ProfileName);
            return SaveOutcome.Fail("Write failed: " + ex.Message);
        }
    }

    /// <summary>
    /// After the first save of a profile read from a file with no id (feature #18, WU1 review F6): the
    /// save gave it an id and wrote it under its canonical name, so the file it came from (which
    /// <see cref="ProfileStore.Save"/> cannot match by id) is removed, or the profile would be listed
    /// twice. Only a bare file name in the profiles folder is accepted; one already gone is fine.
    /// </summary>
    public OpResult RemoveSupersededFile(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.IndexOfAny(['/', '\\']) >= 0 || Path.GetFileName(fileName) != fileName
            || !fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            _log.Warning(null, "Builder.save_profile: refused to remove {File}: not a profile file name", fileName);
            return OpResult.Fail("Not a profile file name: " + fileName);
        }
        var path = Path.Combine(_profilesDir, fileName);
        try
        {
            if (File.Exists(path)) File.Delete(path);
            _log.Information("Builder.save_profile: removed {File}, superseded by the saved profile", fileName);
            return OpResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warning(ex, "Builder.save_profile: could not remove {File}, superseded by the saved profile", fileName);
            return OpResult.Fail(ex.Message);
        }
    }

    /// <summary>The Save dialog's suggested name: the canonical file name's stem plus ".zip" ("rst_profile.zip" with no name).</summary>
    public static string SuggestedExportName(Profile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var safeName = string.IsNullOrWhiteSpace(profile.ProfileName)
            ? "rst_profile"
            : Path.GetFileNameWithoutExtension(ProfileStore.CanonicalFileName(profile));
        return safeName + ".zip";
    }

    /// <summary>
    /// export_profile after the Save dialog: packs a copy of the profile with its resolved branding
    /// (the profile's own when set, else the machine-wide default) into <paramref name="path"/>.
    /// The profiles folder is not touched.
    /// </summary>
    public OpResult Export(Profile edited, string path)
    {
        ArgumentNullException.ThrowIfNull(edited);
        var profile = ProfileEditing.Clone(edited);
        // Spec #17: as the save, a stack no panel places is left out.
        foreach (var orphan in ProfileEditing.RemoveOrphanStacks(profile))
            _log.Warning(null, "Builder.export_profile: stack {Stack} is not on any panel; left out of {Name}", orphan, profile.ProfileName);
        var (logoPath, url) = BrandingDefaults.Resolve(profile);
        try
        {
            using var fs = File.Create(path);
            ProfileZip.Pack(profile, logoPath, url, fs);
            _log.Information("Builder.export_profile OK: name={Name} hasLogo={HasLogo} → {Path}", profile.ProfileName, logoPath is not null, path);
            return OpResult.Success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Error(ex, "Builder.export_profile: write failed for {Path}", path);
            return OpResult.Fail("Write failed: " + ex.Message);
        }
    }
}
