// CleanupTarget.cs — one entry in the Health tool's per-profile cleanup
// list. Replaces the 5 hardcoded categories that lived inside
// HealthCleaner before RST-031.
//
// Path tokens:
//   %LocalAppData%        — Environment.SpecialFolder.LocalApplicationData
//   %AppData%             — Environment.SpecialFolder.ApplicationData
//   %UserProfile%         — Environment.SpecialFolder.UserProfile
//   {revit-version}       — fan-out wildcard; expands to every subdir
//                           matching ^Autodesk Revit \d{4}$ under the
//                           segment's parent. One entry => 0..N concrete
//                           paths at runtime.
//
// Kind:
//   "directory"           — recursive purge of file contents (locked
//                           files skip individually). Matches the
//                           pre-RST-031 _purge_flat / _purge_collab_cache
//                           behavior.
//   "iniRecentFiles"      — strip [Recent File List] FileN= entries from
//                           a Revit.ini, preserving the rest. Same
//                           semantics as the legacy `recentFiles` option.
//
// Backwards-compat: a profile with no `cleanupTargets` field falls back
// to CleanupDefaults at HealthBridge.GetCleanupTargets time. Saving the
// profile through Builder will write the field, upgrading in place.

using System.Text.Json.Serialization;

namespace RST.Core.Profiles;

public sealed class CleanupTarget
{
    /// <summary>
    /// Stable identifier so the Health viewer can correlate per-target
    /// counts in the result modal even when names get edited. Generated
    /// at seed time for OOB entries; assigned on add for user entries.
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    /// <summary>Display label shown in the cleanup modal.</summary>
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    /// <summary>
    /// Path with optional tokens. See class header for the supported
    /// token vocabulary. Resolves to 0..N concrete filesystem paths at
    /// runtime via CleanupPathResolver.
    /// </summary>
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    /// <summary>
    /// One of: "directory" or "iniRecentFiles". Unknown kinds are
    /// skipped at cleaner-run time with a warning rather than crashing.
    /// </summary>
    [JsonPropertyName("kind")]
    public string Kind { get; set; } = KindDirectory;

    /// <summary>
    /// Whether this target is offered to the user in the cleanup modal.
    /// Disabled targets stay in the profile (admin can re-enable later)
    /// but don't surface as a checkbox to the operator.
    /// </summary>
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    public const string KindDirectory      = "directory";
    public const string KindIniRecentFiles = "iniRecentFiles";
}
