// rst-c's own (not vendored): LoaderBridge's GetProfiles, GetActiveProfile, AddProfile (the
// import after the file dialog) and RemoveProfile, moved out of the bridge (spec #16, WU3) with
// unchanged behavior and messages.

using RST.Core.Profiles;

namespace RST.Core.Loader;

/// <summary>An import's outcome: the installed file and profile, or the "Import Failed" error text.</summary>
public sealed record ImportOutcome(bool Ok, string? Error, string? FileName, Profile? Profile)
{
    public static ImportOutcome Fail(string error) => new(false, error, null, null);
}

/// <summary>A delete's outcome; <see cref="WasActive"/> when the active pointer was blanked too.</summary>
public sealed record DeleteOutcome(bool Ok, string? Error, bool WasActive);

/// <summary>The profiles on disk and the active-profile pointer.</summary>
public sealed class ProfileLibraryService
{
    private readonly LoaderPaths _paths;
    private readonly ILoaderLog _log;

    public ProfileLibraryService(LoaderPaths paths, ILoaderLog? log = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _log = log ?? NullLoaderLog.Instance;
    }

    public LoaderPaths Paths => _paths;

    /// <summary>Every readable profile, in folder order; unreadable files are skipped and logged.</summary>
    public IReadOnlyList<ProfileEntry> List()
    {
        var entries = ProfileStore.List(_paths.ProfilesDir, onSkip: (path, ex) =>
            _log.Warning(ex, "Loader.get_profiles: skipped unreadable profile {Path}", path));
        _log.Information("Loader.get_profiles → {Count} profiles from {Dir}", entries.Count, _paths.ProfilesDir);
        return entries;
    }

    /// <summary>The active-profile pointer; a blank one when none is loaded or the file is unreadable.</summary>
    public ActiveProfile GetActive() => ActiveProfile.Read(_paths.ActiveProfileFile);

    /// <summary>
    /// Installs the profile package at <paramref name="zipPath"/> (RST-019: zip only; profile.json
    /// plus its branding logo). Every failure comes back as the bridge's error text.
    /// </summary>
    public ImportOutcome Import(string zipPath)
    {
        ProfilePackage package;
        try
        {
            using var fs = File.OpenRead(zipPath);
            package = ProfileZip.Unpack(fs);
        }
        catch (InvalidDataException ex)
        {
            _log.Warning(ex, "Loader.add_profile: not a valid profile package: {Path}", zipPath);
            return ImportOutcome.Fail("Not a valid RST profile package: " + ex.Message);
        }
        catch (ProfileLoadException ex)
        {
            _log.Warning(ex, "Loader.add_profile: profile.json load failed: {Path}", zipPath);
            return ImportOutcome.Fail(ex.Message);
        }
        catch (IOException ex)
        {
            _log.Error(ex, "Loader.add_profile: read failed for {Path}", zipPath);
            return ImportOutcome.Fail("Read failed: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error(ex, "Loader.add_profile: access denied reading {Path}", zipPath);
            return ImportOutcome.Fail("Access denied: " + ex.Message);
        }

        try
        {
            var fileName = ProfileZip.Install(package, _paths.Root, _paths.ProfilesDir);
            _log.Information("Loader.add_profile OK: imported {Source} (logo={HasLogo}) → {FileName}",
                zipPath, package.LogoBytes is not null, fileName);
            return new ImportOutcome(true, null, fileName, package.Profile);
        }
        catch (IOException ex)
        {
            _log.Error(ex, "Loader.add_profile: install failed for {Path}", zipPath);
            return ImportOutcome.Fail("Install failed: " + ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.Error(ex, "Loader.add_profile: access denied installing {Path}", zipPath);
            return ImportOutcome.Fail("Install failed (access denied): " + ex.Message);
        }
    }

    /// <summary>
    /// Deletes the local copy of the profile (by id, else name). If it was the active profile, the
    /// active pointer falls back to the blank stub.
    /// </summary>
    public DeleteOutcome Delete(string? name, string? id)
    {
        var entry = ProfileStore.Resolve(name, id, _paths.ProfilesDir);
        if (entry is null)
        {
            _log.Warning(null, "Loader.remove_profile: not found name={Name} id={Id}", name, id);
            return new DeleteOutcome(false, "Profile not found", false);
        }

        ProfileStore.Delete(entry.FileName, _paths.ProfilesDir);

        var ap = ActiveProfile.Read(_paths.ActiveProfileFile);
        var wasActive = !ap.IsBlank && (
            string.Equals(ap.ProfileId, entry.Profile.Id, StringComparison.Ordinal) ||
            string.Equals(ap.ProfileName, entry.Profile.ProfileName, StringComparison.Ordinal));
        if (wasActive) ActiveProfile.WriteBlank(_paths.ActiveProfileFile);
        _log.Information("Loader.remove_profile OK: {FileName} (wasActive={WasActive})", entry.FileName, wasActive);
        return new DeleteOutcome(true, null, wasActive);
    }
}
