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

    [Fact]
    public void The_win32_reads_never_throw_on_this_machine()
    {
        var adapters = HealthNative.DisplayAdapters();
        Assert.All(adapters, a => Assert.False(string.IsNullOrWhiteSpace(a.Name)));
        Assert.True(adapters.Count(a => a.Primary) <= 1);
        Assert.True(HealthNative.MonitorCount() >= 0);
        _ = HealthNative.PrimaryResolution();
        _ = HealthNative.SystemDiskMedia();
    }
}
