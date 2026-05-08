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
// Real per-slot icons (the `pack:foo` resolver against Assets/icons/pack/)
// land in a follow-up flag — Slot.IconFile is in the model but unwired.
// Until then every button shows the same default icon, like pyRevit RST
// did before its iconpack matured.

using System;
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
