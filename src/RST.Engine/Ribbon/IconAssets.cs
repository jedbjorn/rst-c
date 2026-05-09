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
// Per-slot icons resolve via ResolveSlotIcon(slot.IconFile):
//   "pack:foo" → Assets/icons/pack/32_foo.png (vendored 48-PNG iconpack)
//   anything else → null (caller falls back to Default32)
// Resolved icons are cached so a profile rebuild doesn't re-decode the
// same PNG on every button.

using System;
using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class IconAssets
{
    private static ImageSource? _default32;
    private static bool _default32Attempted;
    private static ImageSource? _loader;
    private static bool _loaderAttempted;
    private static ImageSource? _builder;
    private static bool _builderAttempted;
    private static ImageSource? _rstifyOff;
    private static bool _rstifyOffAttempted;
    private static ImageSource? _rstifyOn;
    private static bool _rstifyOnAttempted;
    private static ImageSource? _health;
    private static bool _healthAttempted;

    /// <summary>
    /// 32x32 visible default icon (Assets/icons/default_32.png). Used as
    /// both Image and LargeImage on any button without a per-slot icon.
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

    private static readonly ConcurrentDictionary<string, ImageSource?> _packCache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve a slot's <c>iconFile</c> field into a Revit-ready
    /// <see cref="ImageSource"/>. Today only the <c>pack:&lt;name&gt;</c>
    /// shorthand is supported — looks up
    /// <c>Assets/icons/pack/32_&lt;name&gt;.png</c>. Returns null when
    /// the field is empty/unrecognised or the file is missing; caller
    /// falls back to <see cref="Default32"/>.
    /// Resolved bitmaps are process-cached so a live profile rebuild
    /// (RST-020) doesn't re-decode the same PNG for every button.
    /// </summary>
    public static ImageSource? ResolveSlotIcon(string? iconFile)
    {
        if (string.IsNullOrWhiteSpace(iconFile)) return null;
        var trimmed = iconFile!.Trim();
        if (!trimmed.StartsWith("pack:", StringComparison.OrdinalIgnoreCase))
        {
            // Per-profile asset paths (e.g. "<profile-id>/foo.png") will
            // resolve here in a follow-up flag once the upload + zip-bundle
            // path lands. Until then, anything that isn't pack:* falls
            // back to the default icon.
            return null;
        }
        var name = trimmed.Substring(5).Trim();
        if (name.Length == 0) return null;
        return _packCache.GetOrAdd(name, key => LoadBundled($"icons/pack/32_{key}.png"));
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
