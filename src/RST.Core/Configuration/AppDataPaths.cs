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
    /// <summary>%AppData%\RST\.</summary>
    public static string Root =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "RST");

    /// <summary>%AppData%\RST\profiles\.</summary>
    public static string ProfilesDir => Path.Combine(Root, "profiles");

    /// <summary>%AppData%\RST\active_profile.json — pointer to the loaded profile.</summary>
    public static string ActiveProfileFile => Path.Combine(Root, "active_profile.json");

    /// <summary>Ensure the RST root + profiles dir exist; idempotent.</summary>
    public static void EnsureCreated()
    {
        Directory.CreateDirectory(ProfilesDir);
    }
}
