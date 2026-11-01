// rst-c's own (not vendored): a model card's thumbnail from Windows (spec #25, Thumbnails; F19 #25 WU3), the same image
// Explorer shows: IShellItemImageFactory::GetImage on the model's local file, thumbnails only (never the file-type
// icon), at 256 px. Windows' own thumbnail handler and cache produce it; RST never reads the .rvt's content or its
// embedded preview here (that fallback needs FnB agreement, decision #59). Callers pass only paths LocalPathGuard
// accepts as local (ModelThumbnailCache), so no network path ever reaches the shell.

using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace RST.Tray.Thumbnails;

/// <summary>Where a thumbnail comes from: the Windows shell in the tray, a fake in RuntimeTests.</summary>
internal interface IShellThumbnailSource
{
    /// <summary>The shell's thumbnail of <paramref name="path"/> (a verified-local file) at most <paramref name="size"/> px,
    /// frozen; null with <paramref name="reason"/> when Windows has none (no thumbnail handler, a failed extraction).</summary>
    BitmapSource? Extract(string path, int size, out string? reason);
}

internal sealed class ShellThumbnailSource : IShellThumbnailSource
{
    public static readonly ShellThumbnailSource Instance = new();

    // SIIGBF_THUMBNAILONLY: the thumbnail, or a failure; never the file type's icon in its place.
    private const int SiigbfThumbnailOnly = 0x08;
    private const int DibRgbColors = 0;

    public BitmapSource? Extract(string path, int size, out string? reason)
    {
        reason = null;
        object? item = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            var hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (hr < 0 || item is not IShellItemImageFactory factory)
            {
                reason = "no shell item (0x" + hr.ToString("X8") + ")";
                return null;
            }
            hr = factory.GetImage(new NativeSize { Cx = size, Cy = size }, SiigbfThumbnailOnly, out var bitmap);
            if (hr < 0 || bitmap == IntPtr.Zero)
            {
                reason = "0x" + hr.ToString("X8");
                return null;
            }
            try
            {
                return ToBitmapSource(bitmap, out reason);
            }
            finally
            {
                DeleteObject(bitmap);
            }
        }
        catch (Exception ex)
        {
            reason = ex.GetType().Name + ": " + ex.Message;
            return null;
        }
        finally
        {
            if (item is not null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>The 32-bit bitmap's pixels, top-down. A bitmap with no alpha at all is opaque (some handlers leave it 0).</summary>
    private static BitmapSource? ToBitmapSource(IntPtr hbitmap, out string? reason)
    {
        reason = null;
        if (GetObject(hbitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Width <= 0 || info.Height <= 0)
        {
            reason = "unreadable bitmap";
            return null;
        }
        int w = info.Width, h = info.Height, stride = w * 4;
        var pixels = new byte[stride * h];
        var header = new BitmapInfoHeader
        {
            Size = Marshal.SizeOf<BitmapInfoHeader>(),
            Width = w,
            Height = -h, // top-down
            Planes = 1,
            BitCount = 32,
        };
        var dc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(dc, hbitmap, 0, (uint)h, pixels, ref header, DibRgbColors) == 0)
            {
                reason = "unreadable pixels";
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
        var hasAlpha = false;
        for (var i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] == 0) continue;
            hasAlpha = true;
            break;
        }
        if (!hasAlpha)
            for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;
        var source = BitmapSource.Create(w, h, 96, 96, hasAlpha ? PixelFormats.Pbgra32 : PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public IntPtr Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public int Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetObject(IntPtr hgdiobj, int cbBuffer, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, int usage);

    [DllImport("gdi32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
}
