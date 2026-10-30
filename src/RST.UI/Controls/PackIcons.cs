// rst-c's own (not vendored): the pack icon images the RibbonPreview (WU3) and the Builder's
// Pick-icon buttons (WU4) draw, loaded once per file from Assets\icons\pack (spec #16).

using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using RST.Core.Ribbon;
using RST.UI.Loader;

namespace RST.UI.Controls;

public static class PackIcons
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The 32 px image of <paramref name="value"/>, or null when the file is missing or unreadable.</summary>
    /// <param name="value">A parsed <c>pack:</c> value.</param>
    /// <param name="directory">The icon pack folder; null is the shipped Assets\icons\pack next to RST.UI.dll.</param>
    public static BitmapSource? Load(IconPackValue value, string? directory = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dir = directory ?? LoaderAssets.IconPackDirectory;
        var path = Path.Combine(dir, Path.GetFileName(value.RelativePath));
        return Cache.GetOrAdd(path, static p =>
        {
            try
            {
                if (!File.Exists(p)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(p, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = 32;
                image.EndInit();
                image.Freeze();
                return image;
            }
            catch (Exception)
            {
                return null;
            }
        });
    }

    /// <summary>The image of a slot's <c>iconFile</c>, or null for a non-pack value.</summary>
    public static BitmapSource? ForIconFile(string? iconFile, string? directory = null) =>
        IconPack.TryParseValue(iconFile, out var value) ? Load(value, directory) : null;
}
