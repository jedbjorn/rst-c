// IconAssets.cs — bundled image resources for ribbon button icons.
//
// blank.png is a 1x1 transparent PNG used as a non-null LargeImage on
// every ribbon button we create. AdWindows/Revit render Size=Large
// buttons with no LargeImage as a dropdown-chevron placeholder, even
// when no dropdown is wired. Setting LargeImage to anything (including
// a fully transparent pixel) suppresses the chevron without committing
// to icon design — real per-slot icons land in a follow-up flag (the
// `pack:foo` resolver + shipped icon pack referenced by Slot.IconFile).

using System;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Serilog;

namespace RST.Engine.Ribbon;

internal static class IconAssets
{
    private static ImageSource? _default;
    private static bool _loadAttempted;

    public static ImageSource? Default
    {
        get
        {
            if (_loadAttempted) return _default;
            _loadAttempted = true;

            var path = BundledPath("blank.png");
            if (path is null || !File.Exists(path))
            {
                Log.Warning("IconAssets: blank.png not found at expected path={Path}", path);
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
                _default = bmp;
                Log.Debug("IconAssets: loaded blank.png ({Width}x{Height}) from {Path}", bmp.PixelWidth, bmp.PixelHeight, path);
                return _default;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "IconAssets: failed to load blank.png from {Path}", path);
                return null;
            }
        }
    }

    private static string? BundledPath(string filename)
    {
        var loc = typeof(IconAssets).Assembly.Location;
        if (string.IsNullOrEmpty(loc)) return null;
        var dir = Path.GetDirectoryName(loc);
        return string.IsNullOrEmpty(dir) ? null : Path.Combine(dir!, "Assets", filename);
    }
}
