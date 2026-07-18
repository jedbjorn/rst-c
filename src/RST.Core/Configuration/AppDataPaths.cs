// AppDataPaths.cs — well-known on-disk locations under %AppData%\RST\.
//
// The pyRevit-era addin parked all per-user state under %AppData%\RST\.
// We keep that root for continuity (existing profiles round-trip in
// place) and centralise the path math here so callers don't recompute
// the layout.
//
//   %AppData%\RST\
//     profiles\<safeName>_<safeDate>.json     — one file per profile
//     active_profile.json                      — pointer to the loaded profile
//     logs\                                    — managed by RST.Engine
//
// bans.json is intentionally NOT here — it lives next to the addin DLL
// so the staged install folder is self-contained (see BanList.DefaultPath).

using System;
using System.IO;

namespace RST.Core.Configuration;

public static class AppDataPaths
{
    private static string? _rootOverride;

    /// <summary>%AppData%\RST\.</summary>
    public static string Root =>
        _rootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RST");

    /// <summary>
    /// Test hook — redirect Root to an arbitrary directory. Pass null to
    /// restore the real %AppData%\RST\ resolution. Production code never
    /// calls this; tests use it to point AppData reads/writes at a temp
    /// directory so they don't pollute the user's real config.
    /// </summary>
    public static void OverrideRootForTests(string? root)
    {
        _rootOverride = root;
    }

    /// <summary>%AppData%\RST\profiles\.</summary>
    public static string ProfilesDir => Path.Combine(Root, "profiles");

    private static string? _telemetryRootOverride;

    /// <summary>
    /// %LOCALAPPDATA%\RST\telemetry\ — deliberately NOT under the roaming
    /// Root: roaming profiles sync %AppData%, and an outbox that roams
    /// would merge events from different machines and collide on live
    /// session files. Machine-scoped state (install_id, outbox) lives
    /// here; telemetry *preferences* stay roaming (TelemetryPrefs).
    /// </summary>
    public static string TelemetryRoot =>
        _telemetryRootOverride ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RST", "telemetry");

    /// <summary>%LOCALAPPDATA%\RST\telemetry\outbox\ — one .jsonl per session.</summary>
    public static string TelemetryOutboxDir => Path.Combine(TelemetryRoot, "outbox");

    /// <summary>
    /// Test hook — same pattern as <see cref="OverrideRootForTests"/>,
    /// separate override because TelemetryRoot resolves to LocalAppData,
    /// not the roaming Root. Pass null to restore real resolution.
    /// </summary>
    public static void OverrideTelemetryRootForTests(string? root)
    {
        _telemetryRootOverride = root;
    }

    /// <summary>%AppData%\RST\active_profile.json — pointer to the loaded profile.</summary>
    public static string ActiveProfileFile => Path.Combine(Root, "active_profile.json");

    /// <summary>Ensure the RST root + profiles dir exist; idempotent.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ProfilesDir);
    }

    /// <summary>
    /// True when <paramref name="absolutePath"/> resolves to a location at or
    /// under <see cref="Root"/>. Used to confine filesystem operations driven
    /// by profile-supplied values — an imported profile is untrusted input, so
    /// a traversal ("..\\..\\") or rooted ("C:\\…") path must not escape
    /// %AppData%\RST\. Returns false on null/empty or an unparseable path.
    /// </summary>
    public static bool IsUnderRoot(string? absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return false;
        string root, full;
        try
        {
            root = Path.GetFullPath(Root);
            full = Path.GetFullPath(absolutePath!);
        }
        catch
        {
            return false;
        }
        var prefix = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }
}
