// HealthScanner.cs — port of upstream RST/app/health_scanner.py.
//
// Captures RAM (kernel32 GlobalMemoryStatusEx), CPU (kernel32
// GetSystemTimes + HKLM CentralProcessor), Disk (DriveInfo C:\),
// OS (Environment + RuntimeInformation), GPU + network + disk media +
// monitors (System.Management WMI/CIM in-process), and Revit-side
// HardwareAcceleration (Revit.ini [Graphics] UseGraphicsHardware).
//
// Same JSON schema as upstream so the existing health_viewer UI
// renders without changes.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Management;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;
using RST.Core.Health;
using Serilog;

namespace RST.UI.Health;

public static class HealthScanner
{
    public static HealthSnapshot Capture(
        string? revitVersion = null,
        string? revitBuild = null,
        string? revitUsername = null,
        string? modelName = null,
        string? modelPath = null,
        double? modelSizeMb = null,
        int? warningsCount = null,
        IReadOnlyDictionary<string, int>? warningsBySeverity = null,
        IEnumerable<RST.Core.Profiles.CleanupTarget>? cleanupTargets = null)
    {
        Log.Information("HealthScanner.Capture: starting");

        var snap = new HealthSnapshot
        {
            CaptureTimestamp = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            Identity = new Identity
            {
                WindowsUsername = Environment.UserName ?? "",
                DeviceName = Environment.MachineName ?? "",
            },
            Ram = ReadRam(),
            Cpu = ReadCpu(),
            Disk = ReadDisk(),
            Os = ReadOs(),
        };

        // One WMI batch — every CIM query goes through Local in-proc
        // System.Management. Errors degrade per-section; missing CIM data
        // leaves the relevant snapshot fields null/empty.
        snap.Gpus = ReadGpus();
        snap.Gpu  = snap.Gpus.Count > 0 ? snap.Gpus[0] : new Gpu();
        var (mediaType, busType, friendlyName) = ReadDiskMedia();
        snap.Disk.Type = string.IsNullOrEmpty(mediaType) ? "Unknown" : mediaType;
        snap.Disk.BusType = busType;
        snap.Disk.FriendlyName = friendlyName;
        snap.Display = ReadDisplay();
        snap.Network = ReadNetwork();

        snap.Revit = new RevitInfo
        {
            Version = revitVersion ?? "",
            Build = revitBuild ?? "",
            Username = revitUsername ?? "",
            HardwareAcceleration = ReadHardwareAcceleration(revitVersion),
            Model = new RevitModel
            {
                Name = modelName ?? "",
                Path = modelPath ?? "",
                SizeMB = modelSizeMb ?? TryGetFileSizeMb(modelPath),
            },
            WarningsCount = warningsCount,
            WarningsBySeverity = warningsBySeverity is null
                ? new Dictionary<string, int>()
                : new Dictionary<string, int>(warningsBySeverity),
        };

        if (cleanupTargets is not null)
        {
            snap.Cleanup = MeasureCleanup(cleanupTargets);
        }

        Log.Information(
            "HealthScanner.Capture: done device={Device} ramUsed={RamUsed}MB diskFree={DiskFree}GB junkBytes={JunkBytes}",
            snap.Identity.DeviceName,
            snap.Ram.UsedMB ?? 0,
            snap.Disk.AvailableGB ?? 0,
            snap.Cleanup?.TotalSizeBytes ?? 0);
        return snap;
    }

    /// <summary>
    /// Walk every enabled cleanup target's resolved paths and tally on-disk
    /// size + file count. Surfaces in the Hardware section's Junk Files
    /// sub-list so users see how much space the next "Clean Junk Files"
    /// pass would reclaim, before deleting anything.
    /// </summary>
    public static CleanupSummary MeasureCleanup(IEnumerable<RST.Core.Profiles.CleanupTarget> targets)
    {
        var summary = new CleanupSummary();
        foreach (var t in targets)
        {
            if (t is null || !t.Enabled) continue;
            var ts = new CleanupTargetSummary
            {
                Id   = string.IsNullOrEmpty(t.Id) ? t.Name : t.Id,
                Name = t.Name,
                Path = t.Path,
                Kind = t.Kind,
            };
            var resolved = CleanupPathResolver.Resolve(t.Path);
            ts.ResolvedPaths = resolved.ToList();

            foreach (var concrete in resolved)
            {
                if (string.Equals(t.Kind, RST.Core.Profiles.CleanupTarget.KindIniRecentFiles, StringComparison.Ordinal))
                {
                    var (entries, skipped) = CountIniRecentEntries(concrete);
                    ts.FileCount += entries;
                    ts.Skipped   += skipped;
                    // No bytes — operation strips ini entries, doesn't free disk.
                }
                else
                {
                    var (count, bytes, skipped) = MeasureDirectory(concrete);
                    ts.FileCount += count;
                    ts.SizeBytes += bytes;
                    ts.Skipped   += skipped;
                }
            }

            summary.Targets.Add(ts);
            summary.TotalFileCount += ts.FileCount;
            summary.TotalSizeBytes += ts.SizeBytes;
            summary.TotalSkipped   += ts.Skipped;
        }
        return summary;
    }

    private static (long count, long bytes, long skipped) MeasureDirectory(string path)
    {
        if (!Directory.Exists(path)) return (0, 0, 0);
        long count = 0, bytes = 0, skipped = 0;
        var stack = new Stack<string>();
        stack.Push(path);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch (Exception ex) { Log.Debug(ex, "MeasureDirectory: file enum failed for {Dir}", current); continue; }

            foreach (var f in files)
            {
                try
                {
                    var fi = new FileInfo(f);
                    if (!fi.Exists) continue;
                    bytes += fi.Length;
                    count++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    Log.Debug(ex, "MeasureDirectory: stat failed for {File}", f);
                }
            }

            string[] subs;
            try { subs = Directory.GetDirectories(current); }
            catch (Exception ex) { Log.Debug(ex, "MeasureDirectory: subdir enum failed for {Dir}", current); continue; }
            foreach (var s in subs) stack.Push(s);
        }
        return (count, bytes, skipped);
    }

    private static readonly System.Text.RegularExpressions.Regex _iniRecentRe =
        new(@"^\s*File\d+\s*=", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (long entries, long skipped) CountIniRecentEntries(string iniPath)
    {
        if (!File.Exists(iniPath)) return (0, 0);
        try
        {
            // Same UTF-16 LE BOM detection as HealthCleaner uses on rewrite,
            // so the count agrees with what a subsequent CleanJunk would
            // actually strip.
            var bytes = File.ReadAllBytes(iniPath);
            var (text, _) = HealthCleaner.DecodeIniBytes(bytes);
            bool inSection = false;
            long entries = 0;
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim().Trim('\r');
                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    inSection = string.Equals(line, "[Recent File List]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (inSection && _iniRecentRe.IsMatch(raw)) entries++;
            }
            return (entries, 0);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "CountIniRecentEntries failed for {Path}", iniPath);
            return (0, 1);
        }
    }

    // ─── RAM ────────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private sealed class MemoryStatusEx
    {
        public uint  dwLength;
        public uint  dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
        public MemoryStatusEx() { dwLength = (uint)Marshal.SizeOf(typeof(MemoryStatusEx)); }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx buffer);

    private static Ram ReadRam()
    {
        try
        {
            var m = new MemoryStatusEx();
            if (!GlobalMemoryStatusEx(m))
            {
                Log.Warning("HealthScanner.ReadRam: GlobalMemoryStatusEx returned false (lastError={Err})",
                            Marshal.GetLastWin32Error());
                return new Ram();
            }
            long total = (long)Math.Round(m.ullTotalPhys / (1024.0 * 1024.0));
            long avail = (long)Math.Round(m.ullAvailPhys / (1024.0 * 1024.0));
            return new Ram
            {
                TotalMB = total,
                AvailableMB = avail,
                UsedMB = total - avail,
                UsedPercent = (int)m.dwMemoryLoad,
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.ReadRam failed");
            return new Ram();
        }
    }

    // ─── CPU ────────────────────────────────────────────────────────────

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out long lpIdleTime, out long lpKernelTime, out long lpUserTime);

    private static int? SampleCpuPercent(int intervalMs = 500)
    {
        try
        {
            if (!GetSystemTimes(out long idle1, out long kernel1, out long user1)) return null;
            System.Threading.Thread.Sleep(intervalMs);
            if (!GetSystemTimes(out long idle2, out long kernel2, out long user2)) return null;

            long idleD = idle2 - idle1;
            long kernelD = kernel2 - kernel1;
            long userD = user2 - user1;
            long total = kernelD + userD;
            if (total <= 0) return 0;
            // Windows kernel time INCLUDES idle time, so:
            //   used = 1 - idle / (kernel + user)
            double used = 1.0 - ((double)idleD / total);
            int pct = (int)Math.Round(used * 100.0);
            return Math.Max(0, Math.Min(100, pct));
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.SampleCpuPercent failed");
            return null;
        }
    }

    private static Cpu ReadCpu()
    {
        var cpu = new Cpu
        {
            LogicalCores = Environment.ProcessorCount,
            PhysicalCores = Environment.ProcessorCount,
            UsedPercent = SampleCpuPercent(),
        };

        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name)
                cpu.Name = name.Trim();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.ReadCpu: name lookup failed");
        }

        try
        {
            using var parent = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor");
            if (parent is not null)
            {
                cpu.PhysicalCores = parent.GetSubKeyNames().Length;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.ReadCpu: physical-core enum failed");
        }

        return cpu;
    }

    // ─── Disk ───────────────────────────────────────────────────────────

    private static Disk ReadDisk()
    {
        var disk = new Disk();
        try
        {
            var c = new DriveInfo("C");
            if (c.IsReady)
            {
                double totalGB = Math.Round(c.TotalSize / (1024.0 * 1024.0 * 1024.0), 1);
                double availGB = Math.Round(c.AvailableFreeSpace / (1024.0 * 1024.0 * 1024.0), 1);
                disk.TotalGB = totalGB;
                disk.AvailableGB = availGB;
                disk.UsedGB = Math.Round(totalGB - availGB, 1);
                disk.UsedPercent = c.TotalSize > 0
                    ? Math.Round((1.0 - (double)c.AvailableFreeSpace / c.TotalSize) * 100.0, 1)
                    : 0.0;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.ReadDisk failed");
        }
        return disk;
    }

    // ─── OS ─────────────────────────────────────────────────────────────

    private static OsInfo ReadOs()
    {
        var os = new OsInfo
        {
            Name = "Windows",
            Version = Environment.OSVersion.Version.ToString(),
            Release = Environment.OSVersion.VersionString,
        };
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key?.GetValue("CurrentBuild") is string b) os.Build = b;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.ReadOs: build lookup failed");
        }
        return os;
    }

    // ─── GPU (WMI) ──────────────────────────────────────────────────────

    /// <summary>
    /// Enumerate every display adapter (integrated + discrete) via WMI, each
    /// with its real 64-bit VRAM matched from the registry by name. The first
    /// entry is the primary adapter (Win32_VideoController order); callers use
    /// it for the backward-compatible single <see cref="Gpu"/> field.
    /// </summary>
    private static List<Gpu> ReadGpus()
    {
        var gpus = new List<Gpu>();
        try
        {
            // All adapters' real (64-bit) VRAM, keyed by DriverDesc.
            var adapters = ReadAdapterVramTable();

            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                var name = mo["Name"]?.ToString() ?? "";
                // Skip nameless / virtual adapters (RDP mirror, Basic Display)
                // so the list is real cards only — no empty "GPU n" rows.
                if (string.IsNullOrWhiteSpace(name)) continue;

                var gpu = new Gpu
                {
                    Name          = name,
                    DriverVersion = mo["DriverVersion"]?.ToString() ?? "",
                };
                // WMI AdapterRAM is uint32 and saturates at 4 GB — first guess.
                if (mo["AdapterRAM"] is uint ram)
                    gpu.VramTotalMB = (long)Math.Round(ram / (1024.0 * 1024.0));

                // Prefer the 64-bit registry size for THIS adapter, matched by
                // name so a multi-GPU box never reports one card's VRAM for
                // another. AdapterRAM caps at ~4096 MB, so any 6/8/12/24 GB card
                // needs the registry QWORD for its true size.
                var vram64 = adapters.FirstOrDefault(a =>
                    !string.IsNullOrEmpty(gpu.Name) &&
                    string.Equals(a.Desc, gpu.Name, StringComparison.OrdinalIgnoreCase)).Mb;
                if (vram64 > 0) gpu.VramTotalMB = vram64;

                gpus.Add(gpu);
            }

            // Unambiguous single-adapter fallback: exactly one GPU and exactly
            // one sized registry adapter that didn't name-match (DriverDesc can
            // differ from the WMI Name on some single-GPU rigs). Safe because
            // there is only one card it could be.
            if (gpus.Count == 1 && adapters.Count == 1
                && (gpus[0].VramTotalMB is null || gpus[0].VramTotalMB <= 4096))
                gpus[0].VramTotalMB = adapters[0].Mb;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.ReadGpus (Win32_VideoController) failed");
        }
        return gpus;
    }

    // Display-adapter class GUID — every GPU's registry key lives under here as
    // a 4-digit subkey (0000, 0001, …) carrying DriverDesc + memory size.
    private const string DisplayClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    /// <summary>
    /// Read each display adapter's <c>DriverDesc</c> + total VRAM (MB) from the
    /// 64-bit <c>HardwareInformation.qwMemorySize</c> registry value. One entry
    /// per numbered adapter subkey that reports a usable size; empty on error.
    /// Callers match an entry to a WMI GPU by <c>DriverDesc</c> == GPU name.
    /// </summary>
    private static List<(string Desc, long Mb)> ReadAdapterVramTable()
    {
        var list = new List<(string Desc, long Mb)>();
        try
        {
            using var root = Registry.LocalMachine.OpenSubKey(DisplayClassKey);
            if (root is null) return list;

            foreach (var subName in root.GetSubKeyNames())
            {
                // Only the numbered adapter subkeys (0000, 0001, …); skip
                // "Properties" and similar.
                if (subName.Length != 4 || !int.TryParse(subName, out _)) continue;
                using var sub = root.OpenSubKey(subName);
                if (sub is null) continue;

                var bytes = ReadMemorySizeBytes(sub);
                if (bytes <= 0) continue;
                var mb = (long)Math.Round(bytes / (1024.0 * 1024.0));
                var desc = sub.GetValue("DriverDesc")?.ToString() ?? "";
                list.Add((desc, mb));
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.ReadAdapterVramTable failed");
        }
        return list;
    }

    private static long ReadMemorySizeBytes(RegistryKey adapter)
    {
        // qwMemorySize is the modern 64-bit QWORD; .NET surfaces REG_QWORD as a
        // boxed long, but some drivers store it as an 8-byte REG_BINARY.
        var qw = adapter.GetValue("HardwareInformation.qwMemorySize");
        switch (qw)
        {
            case long l when l > 0: return l;
            case int i when i > 0: return i;
            case byte[] b when b.Length == 8: return BitConverter.ToInt64(b, 0);
        }
        return 0;
    }

    // ─── Disk media (WMI MSFT_PhysicalDisk; Win32_DiskDrive fallback) ──

    private static (string mediaType, string busType, string friendlyName) ReadDiskMedia()
    {
        // MSFT_PhysicalDisk lives in the Storage namespace and is the
        // canonical source of MediaType (3=HDD, 4=SSD, 5=SCM, 0=Unspec).
        try
        {
            using var searcher = new ManagementObjectSearcher(
                @"\\.\ROOT\Microsoft\Windows\Storage",
                "SELECT FriendlyName, MediaType, BusType FROM MSFT_PhysicalDisk");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                var friendly = mo["FriendlyName"]?.ToString() ?? "";
                var mediaCode = ReadUInt16Like(mo["MediaType"]);
                var busCode = ReadUInt16Like(mo["BusType"]);
                return (
                    mediaType: MapMediaType(mediaCode),
                    busType: MapBusType(busCode),
                    friendlyName: friendly);
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.ReadDiskMedia: MSFT_PhysicalDisk failed; falling back to Win32_DiskDrive");
        }

        // Win32_DiskDrive has no MediaType code that distinguishes SSD/HDD,
        // but Model + InterfaceType give us enough for friendlyName/busType.
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Model, InterfaceType FROM Win32_DiskDrive WHERE DeviceID LIKE '%PHYSICALDRIVE0%'");
            foreach (var mo in searcher.Get().Cast<ManagementObject>())
            {
                return (
                    mediaType: "Unknown",
                    busType: mo["InterfaceType"]?.ToString() ?? "",
                    friendlyName: mo["Model"]?.ToString() ?? "");
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.ReadDiskMedia: Win32_DiskDrive fallback failed");
        }

        return ("", "", "");
    }

    private static int ReadUInt16Like(object? v) => v switch
    {
        ushort u => u,
        short s  => s,
        int i    => i,
        uint u32 => (int)u32,
        _        => 0,
    };

    private static string MapMediaType(int code) => code switch
    {
        3 => "HDD",
        4 => "SSD",
        5 => "SCM",
        _ => "Unknown",
    };

    private static string MapBusType(int code) => code switch
    {
        0 => "Unknown",
        1 => "SCSI",
        2 => "ATAPI",
        3 => "ATA",
        4 => "1394",
        5 => "SSA",
        6 => "FibreChannel",
        7 => "USB",
        8 => "RAID",
        9 => "iSCSI",
        10 => "SAS",
        11 => "SATA",
        12 => "SD",
        13 => "MMC",
        17 => "NVMe",
        _ => $"Bus{code}",
    };

    // ─── Display (WMI) ──────────────────────────────────────────────────

    private static Display ReadDisplay()
    {
        var disp = new Display();
        try
        {
            using var monSearcher = new ManagementObjectSearcher(
                "SELECT ScreenWidth, ScreenHeight FROM Win32_DesktopMonitor");
            int count = 0;
            int firstW = 0, firstH = 0;
            foreach (var mo in monSearcher.Get().Cast<ManagementObject>())
            {
                count++;
                if (firstW == 0)
                {
                    firstW = ReadUInt16Like(mo["ScreenWidth"]);
                    firstH = ReadUInt16Like(mo["ScreenHeight"]);
                }
            }
            disp.MonitorCount = count;

            // Win32_DesktopMonitor often returns 0 — fall back to
            // CurrentHorizontalResolution on Win32_VideoController.
            if (firstW == 0 || firstH == 0)
            {
                using var vcSearcher = new ManagementObjectSearcher(
                    "SELECT CurrentHorizontalResolution, CurrentVerticalResolution FROM Win32_VideoController");
                foreach (var mo in vcSearcher.Get().Cast<ManagementObject>())
                {
                    firstW = ReadUInt16Like(mo["CurrentHorizontalResolution"]);
                    firstH = ReadUInt16Like(mo["CurrentVerticalResolution"]);
                    if (firstW > 0 && firstH > 0) break;
                }
            }

            disp.PrimaryResolution = (firstW > 0 && firstH > 0) ? $"{firstW}x{firstH}" : "";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.ReadDisplay failed");
        }
        return disp;
    }

    // ─── Network ────────────────────────────────────────────────────────

    private static Network ReadNetwork()
    {
        var net = new Network();
        try
        {
            // Pick the first OperationalStatus.Up adapter that isn't loopback.
            // NetworkInterface.Speed is a long Mbps already (after / 1e6).
            var iface = NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(i =>
                    i.OperationalStatus == OperationalStatus.Up &&
                    i.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                    i.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
            if (iface is not null)
            {
                net.AdapterName = iface.Description ?? iface.Name ?? "";
                net.Type = iface.NetworkInterfaceType switch
                {
                    NetworkInterfaceType.Wireless80211 => "WiFi",
                    NetworkInterfaceType.Ethernet      => "Ethernet",
                    NetworkInterfaceType.GigabitEthernet => "Ethernet",
                    NetworkInterfaceType.FastEthernetT => "Ethernet",
                    _ => iface.NetworkInterfaceType.ToString(),
                };
                if (iface.Speed > 0)
                    net.SpeedMbps = iface.Speed / 1_000_000;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.ReadNetwork failed");
        }
        return net;
    }

    // ─── Revit.ini hardware acceleration ───────────────────────────────

    /// <summary>
    /// Read [Graphics]/UseGraphicsHardware from the per-user Revit.ini.
    /// Returns true / false / null (null when ini or key can't be resolved).
    /// Path: %AppData%\Autodesk\Revit\Autodesk Revit &lt;ver&gt;\Revit.ini.
    /// </summary>
    public static bool? ReadHardwareAcceleration(string? revitVersion)
    {
        if (string.IsNullOrEmpty(revitVersion)) return null;
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrEmpty(appData)) return null;
        var iniPath = Path.Combine(appData, "Autodesk", "Revit",
                                   $"Autodesk Revit {revitVersion}", "Revit.ini");
        if (!File.Exists(iniPath)) return null;
        try
        {
            // Modern Revit writes Revit.ini as UTF-16 LE with BOM. .NET
            // detects the BOM automatically when no encoding is supplied.
            string text = File.ReadAllText(iniPath);
            bool inGraphics = false;
            foreach (var rawLine in text.Split('\n'))
            {
                var line = rawLine.Trim().Trim('\r');
                if (line.StartsWith("[", StringComparison.Ordinal) &&
                    line.EndsWith("]", StringComparison.Ordinal))
                {
                    inGraphics = string.Equals(line, "[Graphics]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inGraphics) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var key = line.Substring(0, eq).Trim();
                var val = line.Substring(eq + 1).Trim();
                if (string.Equals(key, "UseGraphicsHardware", StringComparison.OrdinalIgnoreCase))
                    return val == "1";
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.ReadHardwareAcceleration: failed reading {Path}", iniPath);
        }
        return null;
    }

    private static double? TryGetFileSizeMb(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            var fi = new FileInfo(path!);
            if (!fi.Exists) return null;
            return Math.Round(fi.Length / (1024.0 * 1024.0), 1);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "HealthScanner.TryGetFileSizeMb failed for {Path}", path);
            return null;
        }
    }

    /// <summary>
    /// Persist <paramref name="snap"/> as JSON to <paramref name="path"/>.
    /// Atomic via temp + replace.
    /// </summary>
    public static void Save(HealthSnapshot snap, string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir!);
        var json = System.Text.Json.JsonSerializer.Serialize(snap, JsonOpts);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (File.Exists(path)) File.Delete(path);
        File.Move(tmp, path);
        Log.Information("HealthScanner.Save: wrote snapshot to {Path}", path);
    }

    /// <summary>
    /// Read the most recent snapshot from <paramref name="path"/>. Returns
    /// null if missing or unparseable.
    /// </summary>
    public static HealthSnapshot? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var text = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<HealthSnapshot>(text, JsonOpts);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthScanner.Load: failed reading {Path}", path);
            return null;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
    };
}
