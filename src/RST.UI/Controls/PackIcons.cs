// rst-c's own (not vendored): the pack icon images the RibbonPreview (WU3) and the Builder's
// Pick-icon buttons (WU4) draw, loaded once per file from Assets\icons\pack (spec #16). The
// preview's stack rows (spec #17) load them at 16 px, as the ribbon's stacked buttons do.

using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media.Imaging;
using RST.Core.Ribbon;
using RST.UI.Loader;

namespace RST.UI.Controls;

public static class PackIcons
{
    private static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The image of <paramref name="value"/> (32 px, or <paramref name="size"/>), or null when the file is missing or unreadable.</summary>
    /// <param name="value">A parsed <c>pack:</c> value.</param>
    /// <param name="directory">The icon pack folder; null is the shipped Assets\icons\pack next to RST.UI.dll.</param>
    /// <param name="size">The decoded width: 32 (the pack's size), or 16 for a stack row.</param>
    public static BitmapSource? Load(IconPackValue value, string? directory = null, int size = 32)
    {
        ArgumentNullException.ThrowIfNull(value);
        var dir = directory ?? LoaderAssets.IconPackDirectory;
        var path = Path.Combine(dir, Path.GetFileName(value.RelativePath));
        return Cache.GetOrAdd(path + "@" + size, _ =>
        {
            try
            {
                if (!File.Exists(path)) return null;
                var image = new BitmapImage();
                image.BeginInit();
                image.UriSource = new Uri(path, UriKind.Absolute);
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.DecodePixelWidth = size;
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
