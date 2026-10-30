// rst-c's own (not vendored): the parsing half of the Win32 reads that replaced WMI in the Health scan
// (decision #33): the storage descriptor, the adapter key, the SSD/HDD rule and the bus names. The calls
// themselves run in HealthLiveScanTests on the Windows runner.

using System.Text;
using RST.UI.Health;
using Xunit;

namespace RST.RuntimeTests.Health;

public class HealthNativeTests
{
    /// <summary>A STORAGE_DEVICE_DESCRIPTOR with the vendor and product IDs at their offsets.</summary>
    private static byte[] Descriptor(string? vendor, string? product, int bus)
    {
        var d = new byte[256];
        var at = 64;
        void Put(int field, string? s)
        {
            if (s is null) return;
            BitConverter.GetBytes(at).CopyTo(d, field);
            var bytes = Encoding.ASCII.GetBytes(s);
            bytes.CopyTo(d, at);
            at += bytes.Length + 1; // NUL-terminated
        }
        BitConverter.GetBytes(d.Length).CopyTo(d, 4);
        Put(12, vendor);
        Put(16, product);
        BitConverter.GetBytes(bus).CopyTo(d, 28);
        return d;
    }

    [Theory]
    [InlineData("Msft    ", "Virtual Disk    ", 1, "Msft Virtual Disk", "SCSI")]
    [InlineData(null, "Samsung SSD 980 PRO 1TB", 17, "Samsung SSD 980 PRO 1TB", "NVMe")]
    [InlineData("NVMe    ", "WDC WDS100T2B0C", 17, "WDC WDS100T2B0C", "NVMe")]
    [InlineData("ATA", "ST2000DM008-2FR102", 11, "ST2000DM008-2FR102", "SATA")]
    [InlineData(null, null, 7, "", "USB")]
    public void The_descriptor_gives_the_model_and_the_bus(string? vendor, string? product, int bus, string model, string busName)
    {
        var (parsedBus, parsedModel) = HealthNative.ParseDeviceDescriptor(Descriptor(vendor, product, bus));

        Assert.Equal(bus, parsedBus);
        Assert.Equal(model, parsedModel);
        Assert.Equal(busName, HealthScanner.MapBusType(parsedBus));
    }

    [Fact]
    public void A_short_or_bad_descriptor_gives_nothing()
    {
        Assert.Equal((0, ""), HealthNative.ParseDeviceDescriptor(new byte[8]));
        var d = Descriptor("V", "P", 3);
        BitConverter.GetBytes(10_000).CopyTo(d, 16); // an offset past the end
        Assert.Equal("V", HealthNative.ParseDeviceDescriptor(d).Model);
    }

    [Theory]
    [InlineData(17, false, "SSD")]
    [InlineData(11, true, "HDD")]
    [InlineData(1, null, "Unknown")]
    [InlineData(0x12, false, "SCM")]
    public void The_seek_penalty_decides_SSD_or_HDD(int bus, bool? seekPenalty, string expected) =>
        Assert.Equal(expected, HealthNative.MediaTypeFor(bus, seekPenalty));

    [Fact]
    public void Outputs_of_one_adapter_share_its_key()
    {
        var a = HealthNative.AdapterKey(@"\Registry\Machine\System\CurrentControlSet\Control\Video\{1A2B-33}\0000", "PCI\\VEN_10DE", "NVIDIA RTX A2000");
        var b = HealthNative.AdapterKey(@"\Registry\Machine\System\CurrentControlSet\Control\Video\{1A2B-33}\0001", "PCI\\VEN_10DE", "NVIDIA RTX A2000");
        var other = HealthNative.AdapterKey(@"\Registry\Machine\System\CurrentControlSet\Control\Video\{9F00-01}\0000", "PCI\\VEN_10DE", "NVIDIA RTX A2000");

        Assert.Equal(a, b);
        Assert.NotEqual(a, other);
        Assert.Equal("PCI\\VEN_8086|Intel(R) UHD", HealthNative.AdapterKey("", "PCI\\VEN_8086", "Intel(R) UHD"));
    }

    // --- the GPU rows (review B2) ----------------------------------------------------------------

    private const string RtxHw = @"PCI\VEN_10DE&DEV_2531&SUBSYS_151E103C&REV_A1";
    private const string IntelHw = @"PCI\VEN_8086&DEV_A7A0&SUBSYS_8B41103C&REV_04";
    private static readonly HealthNative.PresentDevice Rtx = new(RtxHw + @"\4&1A2B3C4D&0&0008", "NVIDIA RTX A2000 8GB Laptop GPU", "31.0.15.3742", 8L << 30);
    private static readonly HealthNative.PresentDevice Intel = new(IntelHw + @"\3&11583659&0&10", "Intel(R) Iris(R) Xe Graphics", "31.0.101.4502", 128L << 20);
    private static readonly HealthNative.PresentDevice Rdp = new(@"SWD\REMOTEDISPLAYENUM\RDPIDD_INDIRECTDISPLAY&SESSIONID_0002", "Microsoft Remote Display Adapter", "10.0.22621.1", 0);

    private static string Rows(IEnumerable<RST.Core.Health.Gpu> gpus) =>
        string.Join(" | ", gpus.Select(g => $"{g.Name} {g.DriverVersion} {(g.VramTotalMB?.ToString() ?? "—")}"));

    [Fact]
    public void Over_rdp_the_physical_gpu_is_listed_with_its_own_vram_and_the_remote_adapter_has_none()
    {
        // The session's only output is the remote display adapter; setupapi lists both present devices.
        var outputs = new[] { new HealthNative.Adapter("Microsoft Remote Display Adapter", "", Primary: true) };

        var gpus = HealthScanner.ComposeGpus(new[] { Rtx, Rdp }, outputs, []);

        Assert.Equal("NVIDIA RTX A2000 8GB Laptop GPU 31.0.15.3742 8192 | Microsoft Remote Display Adapter 10.0.22621.1 —", Rows(gpus));
    }

    [Fact]
    public void Over_rdp_a_remote_adapter_listed_first_by_setupapi_still_sorts_after_the_physical_gpus()
    {
        // Review D1: setupapi's order is undocumented. With no primary match, SWD\ devices go last, so the
        // legacy single Gpu field (the first row) is a physical card.
        var outputs = new[] { new HealthNative.Adapter("Microsoft Remote Display Adapter", "", Primary: true) };

        var gpus = HealthScanner.ComposeGpus(new[] { Rdp, Rtx, Intel }, outputs, []);

        Assert.Equal(
            "NVIDIA RTX A2000 8GB Laptop GPU 31.0.15.3742 8192 | Intel(R) Iris(R) Xe Graphics 31.0.101.4502 128 | Microsoft Remote Display Adapter 10.0.22621.1 —",
            Rows(gpus));
        Assert.True(HealthScanner.IsSoftwareDevice(Rdp));
        Assert.False(HealthScanner.IsSoftwareDevice(Rtx));
    }

    [Fact]
    public void On_a_hybrid_laptop_both_gpus_are_listed_with_the_one_driving_the_screen_first()
    {
        // Only the iGPU drives an output; the discrete GPU renders but has none.
        var outputs = new[] { new HealthNative.Adapter("Intel(R) Iris(R) Xe Graphics", IntelHw, Primary: true) };

        var gpus = HealthScanner.ComposeGpus(new[] { Rtx, Intel }, outputs, []);

        Assert.Equal("Intel(R) Iris(R) Xe Graphics 31.0.101.4502 128 | NVIDIA RTX A2000 8GB Laptop GPU 31.0.15.3742 8192", Rows(gpus));
    }

    [Fact]
    public void The_primary_is_matched_by_hardware_id_and_name_among_identical_cards()
    {
        var a = Rtx with { InstanceId = RtxHw + @"\4&AAAA&0&0008" };
        var b = Rtx with { InstanceId = RtxHw + @"\4&BBBB&0&0010", VramBytes = 6L << 30 };
        var outputs = new[] { new HealthNative.Adapter(Rtx.Name, RtxHw, Primary: true) };

        Assert.Equal(0, HealthNative.PrimaryIndex(new[] { a, b }, outputs));
        Assert.Equal(1, HealthNative.PrimaryIndex(new[] { Intel, a }, outputs));
        Assert.Equal(-1, HealthNative.PrimaryIndex(new[] { Intel }, outputs));
        Assert.Equal(-1, HealthNative.PrimaryIndex(new[] { Intel }, Array.Empty<HealthNative.Adapter>()));
        // A hardware ID is a prefix up to the instance part, not any prefix.
        Assert.Equal(-1, HealthNative.PrimaryIndex(new[] { Rtx with { InstanceId = RtxHw + "X\\1" } }, outputs));
    }

    [Fact]
    public void Without_setupapi_the_outputs_are_matched_by_name_and_a_remote_adapter_never_takes_another_cards_vram()
    {
        (string, long, string?)[] table = { ("NVIDIA RTX A4000", 16384, "31.0.15.3742"), ("Microsoft Remote Display Adapter", 0, "10.0.1") };

        var rdp = HealthScanner.ComposeGpus([], new[] { new HealthNative.Adapter("Microsoft Remote Display Adapter", "", true) }, table);
        Assert.Equal("Microsoft Remote Display Adapter 10.0.1 —", Rows(rdp));

        // A single card whose DriverDesc differs from its output name still gets the lone sized entry.
        var renamed = HealthScanner.ComposeGpus([], new[] { new HealthNative.Adapter("NVIDIA RTX A4000 (WDDM)", "", true) }, new (string, long, string?)[] { ("NVIDIA RTX A4000", 16384, "31.0.15.3742") });
        Assert.Equal("NVIDIA RTX A4000 (WDDM) 31.0.15.3742 16384", Rows(renamed));
    }

    [Fact]
    public void The_win32_reads_never_throw_on_this_machine()
    {
        // The Windows runner has one present display device (Hyper-V's video adapter).
        var devices = HealthNative.PresentDisplayDevices();
        Assert.NotEmpty(devices);
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.Name)));
        Assert.All(devices, d => Assert.False(string.IsNullOrWhiteSpace(d.InstanceId)));
        var adapters = HealthNative.DisplayAdapters();
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.True(adapters.Count(a => a.Primary) <= 1);
        Assert.True(HealthNative.MonitorCount() >= 0);
        _ = HealthNative.PrimaryResolution();
        _ = HealthNative.SystemDiskMedia();
    }
}
