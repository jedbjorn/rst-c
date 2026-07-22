// IconAssets.cs — bundled image resources for ribbon button icons.
//
// AdWindows/Revit render Size=Large buttons without visible Image and
// LargeImage as a dropdown-chevron placeholder (split-button look),
// even when no dropdown is wired. A 1x1 transparent PNG is NOT enough
// to suppress this — the renderer treats sub-pixel images as "no image"
// in Revit 2025. We ship a real 32x32 default (default_32.png, copied
// from upstream pyRevit RST) and set BOTH Image AND LargeImage on every
// button — same pattern startup.py uses for unmapped slots.
//
// Per-slot icons resolve via ResolveSlotIcon(slot.IconFile), parsed by
// the shared Core contract (RST.Core.Ribbon.IconPack):
//   "pack:foo"        → Assets/icons/pack/32_foo.png (blue compatibility alias)
//   "pack:foo_green"  → Assets/icons/pack/32_foo_green.png (explicit variant)
//   anything else     → null (caller falls back to Default32)
// Resolved icons are cached so a profile rebuild doesn't re-decode the
// same PNG on every button.
//
// Slots pointing at the RST native tools (Builder/Loader/RSTify/Health,
// placeable on profile toolbars since PR #112) carry no iconFile by
// default — ResolveNativeToolIcon(commandId) maps them back to their
// branded icons so they don't render the generic default. An explicit
// pack icon on the slot still wins (override).

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Ribbon;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class IconAssets
{
    private static ImageSource? _default32;
    private static bool _default32Attempted;
    private static ImageSource? _default16;
    private static bool _default16Attempted;
    private static ImageSource? _loader;
    private static bool _loaderAttempted;
    private static ImageSource? _loader16;
    private static bool _loader16Attempted;
    private static ImageSource? _builder;
    private static bool _builderAttempted;
    private static ImageSource? _builder16;
    private static bool _builder16Attempted;
    private static ImageSource? _rstifyOff;
    private static bool _rstifyOffAttempted;
    private static ImageSource? _rstifyOff16;
    private static bool _rstifyOff16Attempted;
    private static ImageSource? _rstifyOn;
    private static bool _rstifyOnAttempted;
    private static ImageSource? _rstifyOn16;
    private static bool _rstifyOn16Attempted;
    private static ImageSource? _health;
    private static bool _healthAttempted;
    private static ImageSource? _health16;
    private static bool _health16Attempted;

    /// <summary>
    /// 32x32 visible default icon (Assets/icons/default_32.png). Used as
    /// LargeImage fallback on any button without a per-slot icon. For the
    /// matching 16x16 used as Image (small / Quick Access Toolbar slot),
    /// see <see cref="Default16"/>.
    /// </summary>
    public static ImageSource? Default32
    {
        get
        {
            if (_default32Attempted) return _default32;
            _default32Attempted = true;
            _default32 = LoadBundled("icons/default_32.png");
            return _default32;
        }
    }

    /// <summary>
    /// 16x16 visible default icon (Assets/icons/default_16.png). Used as
    /// the Image (small) fallback so QAT-pinned buttons render a real 16
    /// instead of letting Revit downscale a 32 (which produces a soft,
    /// off-center result).
    /// </summary>
    public static ImageSource? Default16
    {
        get
        {
            if (_default16Attempted) return _default16;
            _default16Attempted = true;
            _default16 = LoadBundled("icons/default_16.png");
            return _default16;
        }
    }

    /// <summary>32x32 icon for the Loader ribbon button (Assets/icons/icon_loader.png).</summary>
    public static ImageSource? LoaderIcon
    {
        get
        {
            if (_loaderAttempted) return _loader;
            _loaderAttempted = true;
            _loader = LoadBundled("icons/icon_loader.png");
            return _loader;
        }
    }

    /// <summary>16x16 small variant for QAT (Assets/icons/icon_loader_16.png).</summary>
    public static ImageSource? LoaderIcon16
    {
        get
        {
            if (_loader16Attempted) return _loader16;
            _loader16Attempted = true;
            _loader16 = LoadBundled("icons/icon_loader_16.png");
            return _loader16;
        }
    }

    /// <summary>32x32 icon for the Builder ribbon button (Assets/icons/icon_creator.png).</summary>
    public static ImageSource? BuilderIcon
    {
        get
        {
            if (_builderAttempted) return _builder;
            _builderAttempted = true;
            _builder = LoadBundled("icons/icon_creator.png");
            return _builder;
        }
    }

    /// <summary>16x16 small variant for QAT (Assets/icons/icon_creator_16.png).</summary>
    public static ImageSource? BuilderIcon16
    {
        get
        {
            if (_builder16Attempted) return _builder16;
            _builder16Attempted = true;
            _builder16 = LoadBundled("icons/icon_creator_16.png");
            return _builder16;
        }
    }

    /// <summary>RSTify "inactive" icon — hidden tabs are currently visible.</summary>
    public static ImageSource? RstifyIconOff
    {
        get
        {
            if (_rstifyOffAttempted) return _rstifyOff;
            _rstifyOffAttempted = true;
            _rstifyOff = LoadBundled("icons/icon_minify.png");
            return _rstifyOff;
        }
    }

    /// <summary>16x16 small variant for QAT (Assets/icons/icon_minify_16.png).</summary>
    public static ImageSource? RstifyIconOff16
    {
        get
        {
            if (_rstifyOff16Attempted) return _rstifyOff16;
            _rstifyOff16Attempted = true;
            _rstifyOff16 = LoadBundled("icons/icon_minify_16.png");
            return _rstifyOff16;
        }
    }

    /// <summary>RSTify "active" icon — hidden tabs are currently suppressed.</summary>
    public static ImageSource? RstifyIconOn
    {
        get
        {
            if (_rstifyOnAttempted) return _rstifyOn;
            _rstifyOnAttempted = true;
            _rstifyOn = LoadBundled("icons/icon_minify_on.png");
            return _rstifyOn;
        }
    }

    /// <summary>16x16 small variant for QAT (Assets/icons/icon_minify_on_16.png).</summary>
    public static ImageSource? RstifyIconOn16
    {
        get
        {
            if (_rstifyOn16Attempted) return _rstifyOn16;
            _rstifyOn16Attempted = true;
            _rstifyOn16 = LoadBundled("icons/icon_minify_on_16.png");
            return _rstifyOn16;
        }
    }

    /// <summary>Heart icon for the Health ribbon button (Assets/icons/icon_health.png).</summary>
    public static ImageSource? HealthIcon
    {
        get
        {
            if (_healthAttempted) return _health;
            _healthAttempted = true;
            _health = LoadBundled("icons/icon_health.png");
            return _health;
        }
    }

    /// <summary>16x16 small variant for QAT (Assets/icons/icon_health_16.png).</summary>
    public static ImageSource? HealthIcon16
    {
        get
        {
            if (_health16Attempted) return _health16;
            _health16Attempted = true;
            _health16 = LoadBundled("icons/icon_health_16.png");
            return _health16;
        }
    }

    private static readonly ConcurrentDictionary<string, ImageSource?> _packCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a slot's <c>iconFile</c> field into a Revit-ready
    /// <see cref="ImageSource"/>. The value is parsed by the shared Core
    /// contract (<see cref="IconPack.TryParseValue"/>): an explicit
    /// <c>pack:&lt;name&gt;_&lt;color&gt;</c> resolves to
    /// <c>Assets/icons/pack/32_&lt;name&gt;_&lt;color&gt;.png</c>, a
    /// legacy bare <c>pack:&lt;name&gt;</c> to the blue compatibility
    /// alias <c>Assets/icons/pack/32_&lt;name&gt;.png</c>. Returns null
    /// when the value is unrecognised/malformed or the file is missing;
    /// caller falls back to <see cref="Default32"/>.
    /// Resolved bitmaps are process-cached by normalized pack key so a
    /// live profile rebuild (RST-020) doesn't re-decode the same PNG
    /// for every button.
    /// </summary>
    public static ImageSource? ResolveSlotIcon(string? iconFile)
    {
        // Per-profile asset paths (e.g. "<profile-id>/foo.png") will
        // resolve in a follow-up flag once the upload + zip-bundle path
        // lands. Until then the parser rejects anything that isn't a
        // well-formed pack value, and it falls back to the default icon.
        if (!IconPack.TryParseValue(iconFile, out var pack)) return null;
        return _packCache.GetOrAdd(pack.NormalizedKey, _ => LoadBundled(pack.RelativePath));
    }

    /// <summary>
    /// Branded icon for an RST native tool (Builder/Loader/RSTify/Health)
    /// placed on a profile tab through the command catalog. A catalog
    /// slot's commandId is the scanned AdWindows button Id, which embeds
    /// the PushButtonData name RibbonBuilder stamped at OnStartup (e.g.
    /// "CustomCtrl_%CustomCtrl_%Add-Ins%RST%RST_Health") — a substring
    /// match on the marker is the robust cut, immune to the prefix shape.
    /// Returns null for non-RST commands; the caller then falls back to
    /// <see cref="Default32"/>. An explicit per-slot pack icon
    /// (<see cref="ResolveSlotIcon"/>) takes precedence over this.
    /// RSTify always reports the "off" icon: the live on/off swap only
    /// tracks the real RST-panel button (RstifyToggle.RefreshIcon stops
    /// at the first marker match), so a profile-tab clone stays static.
    /// </summary>
    public static ImageSource? ResolveNativeToolIcon(string? commandId)
    {
        if (string.IsNullOrEmpty(commandId)) return null;
        // Markers = the PushButtonData names in RibbonBuilder + the
        // RSTify cookie (its PushButtonData name IS the cookie).
        if (commandId.Contains("RST_Health",  StringComparison.Ordinal)) return HealthIcon;
        if (commandId.Contains("RST_Loader",  StringComparison.Ordinal)) return LoaderIcon;
        if (commandId.Contains("RST_Builder", StringComparison.Ordinal)) return BuilderIcon;
        if (commandId.Contains(RstifyToggle.RstifyButtonCookie, StringComparison.Ordinal)) return RstifyIconOff;
        return null;
    }

    private static ImageSource? LoadBundled(string relativePath)
    {
        var loc = typeof(IconAssets).Assembly.Location;
        if (string.IsNullOrEmpty(loc))
        {
            Log.Warning("IconAssets: assembly location unavailable, can't load {Rel}", relativePath);
            return null;
        }
        var dir = Path.GetDirectoryName(loc);
        if (string.IsNullOrEmpty(dir)) return null;
        var path = Path.Combine(dir!, "Assets", relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(path))
        {
            Log.Warning("IconAssets: bundled icon not found at {Path}", path);
            return null;
        }
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            if (bmp.CanFreeze) bmp.Freeze();
            Log.Debug("IconAssets: loaded {Rel} ({W}x{H})", relativePath, bmp.PixelWidth, bmp.PixelHeight);
            return bmp;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "IconAssets: failed to load {Path}", path);
            return null;
        }
    }
}
