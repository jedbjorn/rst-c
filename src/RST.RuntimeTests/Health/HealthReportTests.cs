// rst-c's own (not vendored): the Health tab's rows built from a snapshot, against render() of the
// retired health_viewer.html (spec #16, WU2, AC4/AC5): labels, values, sub-lines, severity colors,
// the per-target Junk Files rows and their total, GPU numbering and the section dots.

using System.Globalization;
using RST.Core.Health;
using RST.UI.Health;
using Xunit;

namespace RST.RuntimeTests.Health;

public class HealthReportTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private static HealthReport Report(HealthSnapshot? snap) => HealthReport.Build(snap, EnUs, TimeZoneInfo.Utc);

    private static HealthSection Section(HealthReport r, string title) => r.Sections.Single(s => s.Title == title);

    private static HealthRow Row(HealthReport r, string section, string label) => Section(r, section).Rows.Single(x => x.Label == label);

    private static string SubText(HealthRow row, int i = 0) => string.Concat(row.Subs[i].Select(r => r.Text));

    [Fact]
    public void Sections_InOrder_WithTheirDots()
    {
        var r = Report(HealthSamples.Snapshot());
        Assert.Equal(["Hardware", "Display & Network", "Revit", "Operating System"], r.Sections.Select(s => s.Title));
        Assert.Equal(HealthSeverity.Danger, Section(r, "Hardware").Dot);   // RAM 88 %
        Assert.Equal(HealthSeverity.Info, Section(r, "Display & Network").Dot);
        Assert.Equal(HealthSeverity.Warn, Section(r, "Revit").Dot);       // 612 warnings
        Assert.False(Section(r, "Operating System").HasDot);
        Assert.All(r.Sections, s => Assert.False(s.IsExpanded));
    }

    [Fact]
    public void Hardware_Rows()
    {
        var r = Report(HealthSamples.Snapshot());
        Assert.Equal(
            ["User", "CPU", "RAM", "GPU 1", "GPU 2", "Disk (C:)", "Revit journals", "Collaboration cache", "Recent files list", "Junk total"],
            Section(r, "Hardware").Rows.Select(x => x.Label));

        Assert.Equal("j.doe", Row(r, "Hardware", "User").Value);

        var cpu = Row(r, "Hardware", "CPU");
        Assert.Equal("Intel(R) Core(TM) i9-13900K", cpu.Value);
        Assert.Equal("24 cores · 32 logical · 64% used", SubText(cpu));
        // Only the load is colored (sub-accent), here a soft warning.
        Assert.Equal(HealthSeverity.None, cpu.Subs[0][0].Severity);
        Assert.Equal(HealthSeverity.WarnSoft, cpu.Subs[0][^1].Severity);

        var ram = Row(r, "Hardware", "RAM");
        Assert.Equal("32,768 MB", ram.Value);
        Assert.Equal("28,836 MB used · 88%", SubText(ram));
        Assert.Equal(HealthSeverity.Danger, ram.Subs[0][0].Severity);

        Assert.Equal("driver 31.0.15.3742 · VRAM 16,384 MB", SubText(Row(r, "Hardware", "GPU 2")));

        var disk = Row(r, "Hardware", "Disk (C:)");
        Assert.Equal("Samsung SSD 980 PRO 1TB", disk.Value);
        Assert.Equal("SSD · 42.6 GB free of 953.3 GB · 95.5% used", SubText(disk));
        Assert.Equal(HealthSeverity.Warn, disk.Subs[0][0].Severity);
    }

    [Fact]
    public void JunkFiles_OneRowPerTarget_ThenTheTotal()
    {
        var r = Report(HealthSamples.Snapshot());
        var journals = Row(r, "Hardware", "Revit journals");
        Assert.Equal("642 MB", journals.Value);
        Assert.Equal(@"1,284 files · C:\Users\j.doe\AppData\Local\Autodesk\Revit\Autodesk Revit 2026\Journals", SubText(journals));

        var cache = Row(r, "Hardware", "Collaboration cache");
        Assert.Equal("5.2 GB", cache.Value);
        Assert.EndsWith(@"CollaborationCache · 2 paths", SubText(cache), StringComparison.Ordinal);

        var ini = Row(r, "Hardware", "Recent files list");
        Assert.Equal("18 entries", ini.Value);
        Assert.EndsWith("Revit.ini", SubText(ini), StringComparison.Ordinal);

        var total = Row(r, "Hardware", "Junk total");
        Assert.Equal("5.8 GB", total.Value);
        Assert.True(total.ValueBold);
        Assert.Equal("10,614 files · reclaimable on Clean Junk Files", SubText(total));
    }

    [Fact]
    public void JunkFiles_AreLeftOut_BeforeAScanMeasuredThem()
    {
        var r = Report(HealthSamples.Healthy());
        Assert.DoesNotContain(Section(r, "Hardware").Rows, x => x.Label == "Junk total");
        Assert.Equal(HealthSeverity.Ok, Section(r, "Hardware").Dot);
        // CPU OK is still drawn in green; RAM OK is plain.
        Assert.Equal(HealthSeverity.Ok, Row(r, "Hardware", "CPU").Subs[0][^1].Severity);
        Assert.Equal(HealthSeverity.None, Row(r, "Hardware", "RAM").Subs[0][0].Severity);
    }

    [Fact]
    public void DisplayAndNetwork_Rows()
    {
        var r = Report(HealthSamples.Snapshot());
        Assert.Equal("2560x1440", Row(r, "Display & Network", "Resolution").Value);
        Assert.Equal("2", Row(r, "Display & Network", "Monitors").Value);
        Assert.Equal("Ethernet", SubText(Row(r, "Display & Network", "Adapter")));
        Assert.Equal("1,000 Mbps", Row(r, "Display & Network", "Speed").Value);
    }

    [Fact]
    public void Revit_Rows()
    {
        var r = Report(HealthSamples.Snapshot());
        Assert.Equal("build 26.1.0.34", SubText(Row(r, "Revit", "Version")));
        var accel = Row(r, "Revit", "Hardware Accel");
        Assert.Equal("Enabled", accel.Value);
        Assert.Equal(HealthSeverity.Ok, accel.ValueSeverity);

        var model = Row(r, "Revit", "Model");
        Assert.Equal("Tower-A_Central.rvt", model.Value);
        Assert.Equal(@"\\fs01\projects\2410 Tower A\Tower-A_Central.rvt", SubText(model, 0));
        Assert.Equal("1,740.5 MB", SubText(model, 1));
        Assert.Equal(HealthSeverity.Warn, model.Subs[1][0].Severity);

        var warnings = Row(r, "Revit", "Warnings");
        Assert.Equal("612", warnings.Value);
        Assert.Equal(HealthSeverity.Warn, warnings.ValueSeverity);
        Assert.Equal("Warning: 598 · Error: 14", SubText(warnings));

        var off = Report(HealthSamples.Healthy());
        Assert.Equal("Disabled", Row(off, "Revit", "Hardware Accel").Value);
        Assert.Equal(HealthSeverity.Warn, Row(off, "Revit", "Hardware Accel").ValueSeverity);
        Assert.Equal(HealthSeverity.None, Row(off, "Revit", "Warnings").ValueSeverity);
        Assert.Equal(HealthSeverity.Warn, Section(off, "Revit").Dot);
    }

    [Fact]
    public void OperatingSystem_Rows()
    {
        var r = Report(HealthSamples.Snapshot());
        Assert.Equal("Windows 11 Pro 23H2", Row(r, "Operating System", "Name").Value);
        Assert.Equal("22631.4317", Row(r, "Operating System", "Build").Value);
    }

    [Fact]
    public void OneGpu_IsUnnumbered_AndOlderSnapshotsUseTheSingleGpu()
    {
        var s = HealthSamples.Snapshot();
        s.Gpus = [];
        var r = Report(s);
        Assert.Equal("Intel(R) UHD Graphics 770", Row(r, "Hardware", "GPU").Value);
    }

    [Fact]
    public void ScannedZeroMonitors_ReadsZero_ButNoSnapshotReadsADash()
    {
        var s = HealthSamples.Snapshot();
        s.Display.MonitorCount = 0;
        Assert.Equal("0", Row(Report(s), "Display & Network", "Monitors").Value);
        Assert.True(Row(Report(null), "Display & Network", "Monitors").IsMissing);
    }
}
