// rst-c's own (not vendored): the files the Loader reads and writes (spec #16, WU3).

using RST.Core.Configuration;

namespace RST.Core.Loader;

/// <summary>
/// The per-user files behind the Loader: the profiles folder, active_profile.json and
/// user_profile_prefs.json, all under <see cref="Root"/> (%AppData%\RST). The same files the
/// v2.2.0 bridge used (spec #16, Reach: nothing new is written).
/// </summary>
public sealed record LoaderPaths(string Root, string ProfilesDir, string ActiveProfileFile, string PrefsFile)
{
    /// <summary>The real locations under <see cref="AppDataPaths.Root"/>, read now.</summary>
    public static LoaderPaths Default =>
        new(AppDataPaths.Root, AppDataPaths.ProfilesDir, AppDataPaths.ActiveProfileFile, UserProfilePrefs.DefaultPath);

    /// <summary>The same layout under another root (tests, fixtures).</summary>
    public static LoaderPaths Under(string root) =>
        new(root,
            Path.Combine(root, "profiles"),
            Path.Combine(root, "active_profile.json"),
            Path.Combine(root, "user_profile_prefs.json"));
}

/// <summary>A write's outcome: ok, or the error the bridge returned as <c>{ ok:false, error }</c>.</summary>
public sealed record OpResult(bool Ok, string? Error = null)
{
    public static readonly OpResult Success = new(true);

    public static OpResult Fail(string error) => new(false, error);
}
