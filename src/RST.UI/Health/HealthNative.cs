// rst-c's own (not vendored): the Win32 reads that replaced System.Management (WMI) in the Health scan
// (decision #33: RST ships no third-party DLL into Revit's load context). Each read is plain P/Invoke
// or the registry, needs no elevation, and returns empty values on any failure; HealthScanner degrades
// per section, as before.
//
//   Display adapters  user32 EnumDisplayDevices (was Win32_VideoController); the driver version and
//                     VRAM come from each adapter's display-class registry key, as the VRAM did before
//   Monitors          user32 GetSystemMetrics(SM_CMONITORS) (was Win32_DesktopMonitor)
//   Resolution        user32 EnumDisplaySettings(primary, current mode) (was Win32_DesktopMonitor, then
//                     Win32_VideoController's CurrentHorizontal/VerticalResolution)
//   Disk media        kernel32 DeviceIoControl on C:'s physical disk: IOCTL_STORAGE_QUERY_PROPERTY for the
//                     bus type and model, and the seek-penalty property for SSD or HDD (was
//                     MSFT_PhysicalDisk, with a Win32_DiskDrive fallback)

using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RST.UI.Health;

internal static class HealthNative
{
    // ─── Display adapters and monitors (user32) ──────────────────────────

    private const int DisplayPrimaryDevice = 0x4;
    private const int DisplayMirroringDriver = 0x8;
    private const int SmCxScreen = 0;
    private const int SmCyScreen = 1;
    private const int SmCMonitors = 80;
    private const int EnumCurrentSettings = -1;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion;
        public short dmDriverVersion;
        public short dmSize;
        public short dmDriverExtra;
        public int dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel;
        public int dmPelsWidth;
        public int dmPelsHeight;
        public int dmDisplayFlags;
        public int dmDisplayFrequency;
        public int dmICMMethod;
        public int dmICMIntent;
        public int dmMediaType;
        public int dmDitherType;
        public int dmReserved1;
        public int dmReserved2;
        public int dmPanningWidth;
        public int dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplayDevicesW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayDevices(string? lpDevice, uint iDevNum, ref DisplayDevice lpDisplayDevice, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "EnumDisplaySettingsW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettings(string? lpszDeviceName, int iModeNum, ref DevMode lpDevMode);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    /// <summary>One display adapter: its name, and whether it drives the primary monitor.</summary>
    internal sealed record Adapter(string Name, bool Primary);

    /// <summary>
    /// Every display adapter, primary first: EnumDisplayDevices lists one entry per adapter output, so
    /// entries are merged by adapter (the device key's adapter part), and mirroring drivers are skipped.
    /// </summary>
    internal static List<Adapter> DisplayAdapters()
    {
        var seen = new Dictionary<string, Adapter>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();
        for (uint i = 0; i < 64; i++)
        {
            var d = new DisplayDevice { cb = Marshal.SizeOf<DisplayDevice>() };
            if (!EnumDisplayDevices(null, i, ref d, 0)) break;
            if ((d.StateFlags & DisplayMirroringDriver) != 0) continue;
            var name = (d.DeviceString ?? "").Trim();
            if (name.Length == 0) continue;
            var key = AdapterKey(d.DeviceKey, d.DeviceID, name);
            var primary = (d.StateFlags & DisplayPrimaryDevice) != 0;
            if (seen.TryGetValue(key, out var known))
            {
                if (primary && !known.Primary) seen[key] = known with { Primary = true };
                continue;
            }
            seen[key] = new Adapter(name, primary);
            order.Add(key);
        }
        return order.Select(k => seen[k]).OrderByDescending(a => a.Primary).ToList();
    }

    /// <summary>
    /// <c>\Registry\Machine\System\CurrentControlSet\Control\Video\{GUID}\0000</c> names the adapter by
    /// its GUID (the trailing number is the output); without one, the hardware ID and name.
    /// </summary>
    internal static string AdapterKey(string? deviceKey, string? deviceId, string name)
    {
        var k = (deviceKey ?? "").Trim();
        var open = k.IndexOf('{');
        var close = open >= 0 ? k.IndexOf('}', open) : -1;
        if (open >= 0 && close > open) return k.Substring(open, close - open + 1);
        return (deviceId ?? "") + "|" + name;
    }

    /// <summary>Monitors on the desktop (0 when unknown).</summary>
    internal static int MonitorCount()
    {
        try { return Math.Max(0, GetSystemMetrics(SmCMonitors)); }
        catch { return 0; }
    }

    /// <summary>The primary monitor's current mode in pixels, "" when unknown.</summary>
    internal static string PrimaryResolution()
    {
        int w = 0, h = 0;
        try
        {
            var mode = new DevMode { dmSize = (short)Marshal.SizeOf<DevMode>() };
            if (EnumDisplaySettings(null, EnumCurrentSettings, ref mode))
            {
                w = mode.dmPelsWidth;
                h = mode.dmPelsHeight;
            }
            if (w <= 0 || h <= 0)
            {
                w = GetSystemMetrics(SmCxScreen);
                h = GetSystemMetrics(SmCyScreen);
            }
        }
        catch
        {
            // Unknown.
        }
        return w > 0 && h > 0 ? $"{w}x{h}" : "";
    }

    // ─── Disk media (kernel32 DeviceIoControl) ───────────────────────────

    private const uint OpenExisting = 3;
    private const uint FileShareReadWrite = 0x1 | 0x2;
    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;
    private const int StorageDeviceProperty = 0;
    private const int StorageDeviceSeekPenaltyProperty = 7;
    private const int BusTypeScm = 0x12;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, byte[]? lpInBuffer, int nInBufferSize,
        byte[] lpOutBuffer, int nOutBufferSize, out int lpBytesReturned, IntPtr lpOverlapped);

    /// <summary>What the disk under C: reports; empty strings for what it does not.</summary>
    internal sealed record DiskMedia(string MediaType, string BusType, string FriendlyName);

    /// <summary>The bus, model and SSD/HDD kind of the physical disk holding C: (disk 0 when that is unknown).</summary>
    internal static DiskMedia? SystemDiskMedia(Action<Exception, string>? debug = null)
    {
        var disk = SystemDiskNumber(debug) ?? 0;
        using var h = CreateFile($@"\\.\PhysicalDrive{disk}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            debug?.Invoke(new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()), $"open PhysicalDrive{disk}");
            return null;
        }

        var descriptor = QueryProperty(h, StorageDeviceProperty, 1024);
        if (descriptor is null) return null;
        var (bus, model) = ParseDeviceDescriptor(descriptor);

        bool? seekPenalty = null;
        var penalty = QueryProperty(h, StorageDeviceSeekPenaltyProperty, 12);
        if (penalty is { Length: >= 9 }) seekPenalty = penalty[8] != 0;

        return new DiskMedia(MediaTypeFor(bus, seekPenalty), HealthScanner.MapBusType(bus), model);
    }

    /// <summary>SCM by its bus; otherwise SSD when the disk has no seek penalty, HDD when it has one.</summary>
    internal static string MediaTypeFor(int bus, bool? seekPenalty) =>
        bus == BusTypeScm ? "SCM" : seekPenalty switch
        {
            true => "HDD",
            false => "SSD",
            null => "Unknown",
        };

    private static int? SystemDiskNumber(Action<Exception, string>? debug)
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory)?.TrimEnd('\\') ?? "C:";
            using var h = CreateFile($@"\\.\{root}", 0, FileShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
            if (h.IsInvalid) return null;
            var buffer = new byte[256];
            if (!DeviceIoControl(h, IoctlVolumeGetVolumeDiskExtents, null, 0, buffer, buffer.Length, out var got, IntPtr.Zero) || got < 12)
                return null;
            // VOLUME_DISK_EXTENTS: NumberOfDiskExtents (DWORD, padded to 8), then DISK_EXTENT { DiskNumber, … }.
            return BitConverter.ToInt32(buffer, 0) > 0 ? BitConverter.ToInt32(buffer, 8) : null;
        }
        catch (Exception ex)
        {
            debug?.Invoke(ex, "volume disk extents");
            return null;
        }
    }

    private static byte[]? QueryProperty(SafeFileHandle h, int propertyId, int size)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId, QueryType = PropertyStandardQuery, AdditionalParameters[1] }.
        var query = new byte[12];
        BitConverter.GetBytes(propertyId).CopyTo(query, 0);
        var buffer = new byte[size];
        return DeviceIoControl(h, IoctlStorageQueryProperty, query, query.Length, buffer, buffer.Length, out var got, IntPtr.Zero) && got > 8
            ? buffer
            : null;
    }

    /// <summary>
    /// STORAGE_DEVICE_DESCRIPTOR: VendorIdOffset at 12, ProductIdOffset at 16, BusType at 28; the IDs are
    /// NUL-terminated ASCII at their offsets (0: none). The model is "vendor product", without a vendor
    /// that only names the bus (ATA, NVMe).
    /// </summary>
    internal static (int Bus, string Model) ParseDeviceDescriptor(byte[] d)
    {
        if (d.Length < 32) return (0, "");
        var vendor = AsciiAt(d, BitConverter.ToInt32(d, 12));
        var product = AsciiAt(d, BitConverter.ToInt32(d, 16));
        var bus = BitConverter.ToInt32(d, 28);
        if (vendor.Equals("ATA", StringComparison.OrdinalIgnoreCase) || vendor.Equals("NVMe", StringComparison.OrdinalIgnoreCase))
            vendor = "";
        var model = string.Join(" ", new[] { vendor, product }.Where(s => s.Length > 0));
        return (bus, model);
    }

    private static string AsciiAt(byte[] d, int offset)
    {
        if (offset <= 0 || offset >= d.Length) return "";
        var end = Array.IndexOf(d, (byte)0, offset);
        if (end < 0) end = d.Length;
        return Encoding.ASCII.GetString(d, offset, end - offset).Trim();
    }
}
