// rst-c's own (not vendored): the Win32 reads that replaced System.Management (WMI) in the Health scan
// (decision #33: RST ships no third-party DLL into Revit's load context). Each read is plain P/Invoke
// or the registry, needs no elevation, and returns empty values on any failure; HealthScanner degrades
// per section, as before.
//
//   Display adapters  setupapi: every PRESENT display-class device (was Win32_VideoController, which
//                     reflects the same devices), with the name, driver version and VRAM read from
//                     each device's own driver key. user32 EnumDisplayDevices only marks the primary
//                     adapter: it lists the outputs of this session, so over RDP it sees only the
//                     remote display adapter and on a hybrid laptop only the GPU that drives a screen
//                     (review B2)
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

    /// <summary>
    /// One adapter with outputs in this session: its name, its PnP hardware ID (<c>PCI\VEN_…&amp;DEV_…</c>,
    /// the prefix of the device's instance ID) and whether it drives the primary monitor.
    /// </summary>
    internal sealed record Adapter(string Name, string HardwareId, bool Primary);

    /// <summary>
    /// The adapters with outputs in this session, primary first: EnumDisplayDevices lists one entry per
    /// adapter output, so entries are merged by adapter (the device key's adapter part), and mirroring
    /// drivers are skipped. Used for the primary flag only: the GPU list is <see cref="PresentDisplayDevices"/>.
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
            seen[key] = new Adapter(name, (d.DeviceID ?? "").Trim(), primary);
            order.Add(key);
        }
        return order.Select(k => seen[k]).OrderByDescending(a => a.Primary).ToList();
    }

    // ─── Present display-class devices (setupapi) ────────────────────────

    private static readonly Guid DisplayClassGuid = new("4d36e968-e325-11ce-bfc1-08002be10318");
    private static readonly IntPtr InvalidHandle = new(-1);
    private const int DigcfPresent = 0x2;
    private const int DicsFlagGlobal = 0x1;
    private const int DiregDrv = 0x2;
    private const int KeyRead = 0x20019;
    private const int SpdrpDeviceDesc = 0x0;

    [StructLayout(LayoutKind.Sequential)]
    private struct SpDevinfoData
    {
        public int cbSize;
        public Guid ClassGuid;
        public int DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, string? enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr deviceInfoSet, int memberIndex, ref SpDevinfoData deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern IntPtr SetupDiOpenDevRegKey(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, int scope, int hwProfile, int keyType, int samDesired);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInstanceIdW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceId(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, [Out] char[] buffer, int bufferSize, out int requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr deviceInfoSet, ref SpDevinfoData deviceInfoData, int property,
        out int propertyRegDataType, byte[] buffer, int bufferSize, out int requiredSize);

    /// <summary>A present display-class device, read from its own driver key (VRAM in bytes, 0 when not reported).</summary>
    internal sealed record PresentDevice(string InstanceId, string Name, string DriverVersion, long VramBytes);

    /// <summary>
    /// Every present display-class device (DIGCF_PRESENT: devices no longer present, which still have a
    /// class-key subkey, are left out), in setupapi's order. Each device's name, driver version and VRAM
    /// come from its own driver key (<c>SetupDiOpenDevRegKey(DIREG_DRV)</c>), so two cards never mix.
    /// Empty on any failure.
    /// </summary>
    internal static List<PresentDevice> PresentDisplayDevices(Action<Exception, string>? debug = null)
    {
        var list = new List<PresentDevice>();
        var guid = DisplayClassGuid;
        var set = SetupDiGetClassDevs(ref guid, null, IntPtr.Zero, DigcfPresent);
        if (set == InvalidHandle || set == IntPtr.Zero) return list;
        try
        {
            for (var i = 0; i < 64; i++)
            {
                var data = new SpDevinfoData { cbSize = Marshal.SizeOf<SpDevinfoData>() };
                if (!SetupDiEnumDeviceInfo(set, i, ref data)) break;
                try
                {
                    var buffer = new char[512];
                    var instanceId = SetupDiGetDeviceInstanceId(set, ref data, buffer, buffer.Length, out _)
                        ? new string(buffer).TrimEnd('\0')
                        : "";
                    string? name = null, version = null;
                    long vram = 0;
                    var hkey = SetupDiOpenDevRegKey(set, ref data, DicsFlagGlobal, 0, DiregDrv, KeyRead);
                    if (hkey != InvalidHandle && hkey != IntPtr.Zero)
                    {
                        using var key = Microsoft.Win32.RegistryKey.FromHandle(new Microsoft.Win32.SafeHandles.SafeRegistryHandle(hkey, ownsHandle: true));
                        name = key.GetValue("DriverDesc")?.ToString();
                        version = key.GetValue("DriverVersion")?.ToString();
                        vram = HealthScanner.ReadMemorySizeBytes(key);
                    }
                    if (string.IsNullOrWhiteSpace(name)) name = DeviceDescription(set, ref data);
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    list.Add(new PresentDevice(instanceId, name!.Trim(), version ?? "", vram));
                }
                catch (Exception ex)
                {
                    debug?.Invoke(ex, $"display device {i}");
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private static string? DeviceDescription(IntPtr set, ref SpDevinfoData data)
    {
        var buffer = new byte[1024];
        return SetupDiGetDeviceRegistryProperty(set, ref data, SpdrpDeviceDesc, out _, buffer, buffer.Length, out _)
            ? Encoding.Unicode.GetString(buffer).Split('\0')[0]
            : null;
    }

    /// <summary>
    /// Which present device drives the primary monitor: its instance ID starts with the primary
    /// output's hardware ID. With several identical cards, the one whose name matches; else the first.
    /// -1 when none does (for example over RDP, where the primary output is the remote display adapter).
    /// </summary>
    internal static int PrimaryIndex(IReadOnlyList<PresentDevice> devices, IReadOnlyList<Adapter> outputs)
    {
        var primary = outputs.FirstOrDefault(o => o.Primary);
        if (primary is null || string.IsNullOrEmpty(primary.HardwareId)) return -1;
        var hw = primary.HardwareId;
        var matches = Enumerable.Range(0, devices.Count)
            .Where(i => devices[i].InstanceId.Equals(hw, StringComparison.OrdinalIgnoreCase)
                        || devices[i].InstanceId.StartsWith(hw + "\\", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count == 0) return -1;
        var byName = matches.Where(i => devices[i].Name.Equals(primary.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        return byName.Count > 0 ? byName[0] : matches[0];
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

    /// <summary>The bus, model and SSD/HDD kind of the physical disk holding C:; null when that disk cannot be found.</summary>
    internal static DiskMedia? SystemDiskMedia(Action<Exception, string>? debug = null)
    {
        // Review N3: no guessing PhysicalDrive0 when C:'s disk is unknown; the row is labelled "Disk (C:)".
        if (SystemDiskNumber(debug) is not { } disk) return null;
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
