// rst-c's own (not vendored): the company logo pick, moved out of LoaderBridge.PickLogoFile
// (LB:626-697) for the WPF Builder (spec #16, WU4) with unchanged behavior and messages. The
// bridge is retired (WU5); this is the only copy.

using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Configuration;
using Serilog;

namespace RST.UI.Builder;

/// <summary>A logo pick's outcome: the stored file name ("branding.png"), or the error text.</summary>
public sealed record LogoOutcome(bool Ok, string? Error, string? FileName)
{
    public static LogoOutcome Fail(string error) => new(false, error, null);
}

public static class BrandingLogo
{
    /// <summary>
    /// Stores <paramref name="source"/> as the machine-wide logo (%AppData%\RST\branding.png),
    /// resized to the branding panel's 85 × 85 and PNG-encoded; when decoding or resizing fails, the
    /// file is copied as it is (better to ship what the user picked than reject it).
    /// </summary>
    /// <param name="source">The picked image.</param>
    /// <param name="destination">Where the logo goes; null is <see cref="BrandingDefaults.LogoPath"/>.</param>
    public static LogoOutcome Store(string source, string? destination = null)
    {
        var dest = destination ?? BrandingDefaults.LogoPath;
        try
        {
            if (destination is null) AppDataPaths.EnsureCreated();
            else Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            try
            {
                Encode(source, dest, BrandingDefaults.PanelSizePx);
                Log.Information("Branding logo OK (resized {Size}x{Size} PNG): {Source} → {Dest}",
                    BrandingDefaults.PanelSizePx, BrandingDefaults.PanelSizePx, source, dest);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Branding logo: encode/resize failed; falling back to raw copy");
                File.Copy(source, dest, overwrite: true);
                Log.Information("Branding logo OK (raw copy): {Source} → {Dest}", source, dest);
            }
            return new LogoOutcome(true, null, BrandingDefaults.LogoFileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Error(ex, "Branding logo: copy failed {Source} → {Dest}", source, dest);
            return LogoOutcome.Fail("Copy failed: " + ex.Message);
        }
    }

    /// <summary>
    /// Decodes <paramref name="sourcePath"/> (PNG or JPEG), force-resizes it to
    /// <paramref name="targetSize"/> × <paramref name="targetSize"/> (a non-square image is stretched,
    /// as PIL's resize was) and PNG-encodes it to <paramref name="destPath"/>. Throws on any failure.
    /// </summary>
    public static void Encode(string sourcePath, string destPath, int targetSize)
    {
        var src = new BitmapImage();
        src.BeginInit();
        src.UriSource = new Uri(sourcePath, UriKind.Absolute);
        // OnLoad detaches the image from the file, so re-picking the current logo can overwrite it.
        src.CacheOption = BitmapCacheOption.OnLoad;
        src.EndInit();
        if (src.CanFreeze) src.Freeze();

        var scaleX = (double)targetSize / Math.Max(1, src.PixelWidth);
        var scaleY = (double)targetSize / Math.Max(1, src.PixelHeight);
        var resized = new TransformedBitmap(src, new ScaleTransform(scaleX, scaleY));
        if (resized.CanFreeze) resized.Freeze();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(resized));
        using var fs = File.Create(destPath);
        encoder.Save(fs);
    }
}
