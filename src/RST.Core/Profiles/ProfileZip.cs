// ProfileZip.cs — pack/unpack a profile + its referenced assets as a
// single distributable .zip archive.
//
// Zip layout:
//   profile.json                 — the profile JSON (schemaVersion 1+)
//   assets/branding.png          — company branding logo bytes (optional;
//                                  omitted when no logo resolved)
//
// Per-profile branding override: Pack snapshots the *resolved* branding
// (per-profile if set, else machine-wide default) into the exported
// JSON's Branding field, with LogoFile pointing at a path under
// "<profile-id>/branding.png". Install extracts assets to
// %AppData%\RST\<profile-id>\branding.png so the rewritten LogoFile
// resolves cleanly via BrandingDefaults.Resolve on the receiving
// machine — without touching that machine's global branding.png.

using System;
using System.IO;
using System.IO.Compression;
using RST.Core.Configuration;

namespace RST.Core.Profiles;

public sealed record ProfilePackage(Profile Profile, byte[]? LogoBytes);

public static class ProfileZip
{
    public const string ProfileJsonEntryName = "profile.json";
    public const string LogoEntryName = "assets/branding.png";

    /// <summary>
    /// Write <paramref name="profile"/> + (optional) logo bytes into a
    /// zip archive on <paramref name="destination"/>. <paramref name="resolvedLogoPath"/>
    /// is the absolute path to the logo to bundle (typically the result
    /// of <see cref="BrandingDefaults.Resolve"/> on the source machine).
    /// <paramref name="resolvedUrl"/> is the branding URL; baked into
    /// the exported JSON's Branding.Url. Either may be null/empty.
    /// </summary>
    /// <remarks>
    /// Mutates <paramref name="profile"/>: rewrites <see cref="Profile.Branding"/>
    /// to point at the in-archive logo path. Caller passes a transient
    /// deserialized copy (the Bridge does this), not a long-lived reference.
    /// </remarks>
    public static void Pack(
        Profile profile,
        string? resolvedLogoPath,
        string? resolvedUrl,
        Stream destination)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (destination is null) throw new ArgumentNullException(nameof(destination));

        if (string.IsNullOrEmpty(profile.Id))
            profile.Id = Guid.NewGuid().ToString();

        byte[]? logoBytes = null;
        if (!string.IsNullOrEmpty(resolvedLogoPath) && File.Exists(resolvedLogoPath))
        {
            logoBytes = File.ReadAllBytes(resolvedLogoPath!);
        }

        var hasBranding = logoBytes is not null || !string.IsNullOrEmpty(resolvedUrl);
        if (hasBranding)
        {
            profile.Branding = new Branding
            {
                LogoFile = logoBytes is null ? null : $"{profile.Id}/branding.png",
                Url = string.IsNullOrEmpty(resolvedUrl) ? null : resolvedUrl,
            };
        }
        else
        {
            profile.Branding = null;
        }

        using var archive = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);

        var jsonEntry = archive.CreateEntry(ProfileJsonEntryName, CompressionLevel.Fastest);
        using (var jsonStream = jsonEntry.Open())
        {
            ProfileSerializer.Write(profile, jsonStream);
        }

        if (logoBytes is not null)
        {
            var logoEntry = archive.CreateEntry(LogoEntryName, CompressionLevel.NoCompression);
            using var ws = logoEntry.Open();
            ws.Write(logoBytes, 0, logoBytes.Length);
        }
    }

    /// <summary>
    /// Read a profile package from <paramref name="source"/>. Throws
    /// <see cref="InvalidDataException"/> when profile.json is missing.
    /// </summary>
    public static ProfilePackage Unpack(Stream source)
    {
        if (source is null) throw new ArgumentNullException(nameof(source));

        using var archive = new ZipArchive(source, ZipArchiveMode.Read);

        var profileEntry = archive.GetEntry(ProfileJsonEntryName)
            ?? throw new InvalidDataException(
                $"Profile package is missing required entry '{ProfileJsonEntryName}'.");

        Profile profile;
        using (var ps = profileEntry.Open())
        {
            profile = ProfileSerializer.Read(ps);
        }

        byte[]? logoBytes = null;
        var logoEntry = archive.GetEntry(LogoEntryName);
        if (logoEntry is not null)
        {
            using var ms = new MemoryStream();
            using (var ls = logoEntry.Open())
            {
                ls.CopyTo(ms);
            }
            logoBytes = ms.ToArray();
        }

        return new ProfilePackage(profile, logoBytes);
    }

    /// <summary>
    /// Install <paramref name="package"/> on the local machine: write
    /// the logo (if any) to %AppData%\RST\&lt;profile-id&gt;\branding.png,
    /// rewrite Profile.Branding.LogoFile to that relative path, and
    /// persist the profile via <see cref="ProfileStore.Save"/>. Returns
    /// the canonical filename written.
    /// </summary>
    public static string Install(ProfilePackage package, string? appDataRoot = null, string? profilesDir = null)
    {
        if (package is null) throw new ArgumentNullException(nameof(package));
        appDataRoot ??= AppDataPaths.Root;

        var profile = package.Profile;
        if (string.IsNullOrEmpty(profile.Id))
            profile.Id = Guid.NewGuid().ToString();

        if (package.LogoBytes is not null)
        {
            var assetDir = Path.Combine(appDataRoot, profile.Id!);
            Directory.CreateDirectory(assetDir);
            var logoPath = Path.Combine(assetDir, "branding.png");
            File.WriteAllBytes(logoPath, package.LogoBytes);

            profile.Branding ??= new Branding();
            profile.Branding.LogoFile = $"{profile.Id}/branding.png";
        }

        return ProfileStore.Save(profile, profilesDir);
    }
}
