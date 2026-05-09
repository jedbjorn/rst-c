// BrandingDefaults.cs — machine-wide default branding (logo + URL).
//
// One company branding per Windows user, stored under %AppData%\RST\:
//
//   branding.png     — the logo (writer wins; PickLogoFile overwrites it)
//   branding.json    — { "version": 1, "url": "https://..." }
//
// Per-profile override semantics: Profile.Branding is kept on the model
// for forward-compat, but the builder UI today writes it as null and the
// effective branding for any profile resolves to this default. If
// Branding is non-null on a loaded profile (legacy / hand-edited), its
// LogoFile is treated as a relative path under %AppData%\RST\.
//
// Seeding: EnsureSeeded() copies the bundled RST logo from the addin
// install folder to %AppData%\RST\branding.png on first launch when no
// per-machine logo exists yet. Idempotent — never overwrites an
// existing branding.png.

using System;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Profiles;

namespace RST.Core.Configuration;

public sealed class BrandingDefaults
{
    /// <summary>Relative URL — null/empty means "no branding URL set".</summary>
    public string? Url { get; set; }

    /// <summary>%AppData%\RST\branding.png — fixed name by design.</summary>
    public static string LogoPath => Path.Combine(AppDataPaths.Root, "branding.png");

    /// <summary>%AppData%\RST\branding.json — URL + future config.</summary>
    public static string ConfigPath => Path.Combine(AppDataPaths.Root, "branding.json");

    /// <summary>Filename stored in Profile.Branding.LogoFile when a profile opts into branding.</summary>
    public const string LogoFileName = "branding.png";

    /// <summary>
    /// Square edge length the branding panel renders at and the encoder
    /// targets when resizing a picked logo. Single source of truth shared
    /// between RST.UI's PickLogoFile and RST.Engine's PanelStyling.
    /// </summary>
    public const int PanelSizePx = 85;

    /// <summary>True when a logo file currently exists on disk.</summary>
    public static bool HasLogo => File.Exists(LogoPath);

    /// <summary>
    /// Read the per-machine branding config. Missing or corrupt file →
    /// empty defaults (Url=null). Never throws on Load.
    /// </summary>
    public static BrandingDefaults Load()
    {
        if (!File.Exists(ConfigPath)) return new BrandingDefaults();
        try
        {
            var text = File.ReadAllText(ConfigPath);
            var doc = JsonSerializer.Deserialize<BrandingDocument>(text, JsonOptions);
            return new BrandingDefaults { Url = doc?.Url };
        }
        catch
        {
            return new BrandingDefaults();
        }
    }

    /// <summary>Write the per-machine branding config. Creates %AppData%\RST\ if missing.</summary>
    public void Save()
    {
        AppDataPaths.EnsureCreated();
        var doc = new BrandingDocument(Version: 1, Url: string.IsNullOrWhiteSpace(Url) ? null : Url);
        var text = JsonSerializer.Serialize(doc, JsonOptions);
        File.WriteAllText(ConfigPath, text);
    }

    /// <summary>
    /// Copy the bundled default logo into %AppData%\RST\branding.png if
    /// no per-machine logo exists yet. Idempotent — does nothing when
    /// branding.png is already present (admin-set logos are preserved).
    /// Bundled source path: <c>&lt;addin-folder&gt;/Assets/branding.png</c>.
    /// </summary>
    public static void EnsureSeeded()
    {
        if (HasLogo) return;
        var bundled = BundledLogoPath();
        if (bundled is null || !File.Exists(bundled)) return;
        AppDataPaths.EnsureCreated();
        File.Copy(bundled, LogoPath, overwrite: false);
    }

    /// <summary>
    /// Resolve the effective branding for a loaded profile:
    ///   - Per-profile Branding wins when its LogoFile points at an
    ///     existing file under %AppData%\RST\ (relative path).
    ///   - Otherwise fall back to the per-machine default.
    /// Returns (logoAbsolutePath, url) — either may be null/empty.
    /// </summary>
    public static (string? LogoPath, string? Url) Resolve(Profile profile)
    {
        if (profile.Branding is { LogoFile.Length: > 0 } b)
        {
            var rel = b.LogoFile!.Replace('/', Path.DirectorySeparatorChar);
            var abs = Path.Combine(AppDataPaths.Root, rel);
            if (File.Exists(abs))
            {
                return (abs, b.Url);
            }
            // Per-profile path doesn't exist; fall through to default.
        }
        var defaults = Load();
        return (HasLogo ? LogoPath : null, defaults.Url);
    }

    private static string? BundledLogoPath()
    {
        var location = typeof(BrandingDefaults).Assembly.Location;
        if (string.IsNullOrEmpty(location)) return null;
        var dir = Path.GetDirectoryName(location);
        if (string.IsNullOrEmpty(dir)) return null;
        return Path.Combine(dir, "Assets", "branding.png");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private sealed record BrandingDocument(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("url")] string? Url);
}
