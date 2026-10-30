// rst-c's own (not vendored): the Health tab's severity thresholds at every boundary, and the
// value formatters, against the rules of the retired health_viewer.html (spec #16, WU2, AC5).

using System.Globalization;
using RST.Core.Health;
using Xunit;

namespace RST.Tests.Health;

public class HealthSeverityTests
{
    [Theory]
    [InlineData(null, HealthSeverity.Ok)]
    [InlineData(0, HealthSeverity.Ok)]
    [InlineData(74, HealthSeverity.Ok)]
    [InlineData(75, HealthSeverity.Warn)]
    [InlineData(84, HealthSeverity.Warn)]
    [InlineData(85, HealthSeverity.Danger)]
    [InlineData(100, HealthSeverity.Danger)]
    public void Ram(int? usedPercent, HealthSeverity expected) => Assert.Equal(expected, HealthSeverityRules.Ram(usedPercent));

    [Theory]
    [InlineData(null, HealthSeverity.Ok)]
    [InlineData(59, HealthSeverity.Ok)]
    [InlineData(60, HealthSeverity.WarnSoft)]
    [InlineData(79, HealthSeverity.WarnSoft)]
    [InlineData(80, HealthSeverity.Warn)]
    [InlineData(89, HealthSeverity.Warn)]
    [InlineData(90, HealthSeverity.Danger)]
    public void Cpu(int? usedPercent, HealthSeverity expected) => Assert.Equal(expected, HealthSeverityRules.Cpu(usedPercent));

    [Theory]
    [InlineData(null, HealthSeverity.Ok)]
    [InlineData(500.0, HealthSeverity.Ok)]
    [InlineData(100.0, HealthSeverity.Ok)]
    [InlineData(99.9, HealthSeverity.WarnSoft)]
    [InlineData(50.0, HealthSeverity.WarnSoft)]
    [InlineData(49.9, HealthSeverity.Warn)]
    [InlineData(30.0, HealthSeverity.Warn)]
    [InlineData(29.9, HealthSeverity.Danger)]
    [InlineData(0.0, HealthSeverity.Danger)]
    public void DiskFree(double? availableGb, HealthSeverity expected) =>
        Assert.Equal(expected, HealthSeverityRules.DiskFree(availableGb));

    [Theory]
    [InlineData(null, HealthSeverity.Ok)]
    [InlineData(1023.9, HealthSeverity.Ok)]
    [InlineData(1024.0, HealthSeverity.WarnSoft)]
    [InlineData(1535.9, HealthSeverity.WarnSoft)]
    [InlineData(1536.0, HealthSeverity.Warn)]
    [InlineData(2047.9, HealthSeverity.Warn)]
    [InlineData(2048.0, HealthSeverity.Danger)]
    public void ModelSize(double? sizeMb, HealthSeverity expected) =>
        Assert.Equal(expected, HealthSeverityRules.ModelSize(sizeMb));

    [Theory]
    [InlineData(null, false)]
    [InlineData(499, false)]
    [InlineData(500, true)]
    public void ManyWarnings(int? count, bool expected) => Assert.Equal(expected, HealthSeverityRules.TooManyWarnings(count));

    [Theory]
    [InlineData(HealthSeverity.Ok, HealthSeverity.WarnSoft, HealthSeverity.WarnSoft)]
    [InlineData(HealthSeverity.Warn, HealthSeverity.WarnSoft, HealthSeverity.Warn)]
    [InlineData(HealthSeverity.Danger, HealthSeverity.Warn, HealthSeverity.Danger)]
    [InlineData(HealthSeverity.Ok, HealthSeverity.Ok, HealthSeverity.Ok)]
    public void Worse_RanksOkBelowSoftBelowWarnBelowDanger(HealthSeverity a, HealthSeverity b, HealthSeverity expected)
    {
        Assert.Equal(expected, HealthSeverityRules.Worse(a, b));
        Assert.Equal(expected, HealthSeverityRules.Worse(b, a));
    }

    [Theory]
    [InlineData(50, 200.0, 10, HealthSeverity.Ok)]
    [InlineData(76, 200.0, 10, HealthSeverity.Warn)]
    [InlineData(50, 20.0, 10, HealthSeverity.Danger)]
    [InlineData(50, 200.0, 65, HealthSeverity.WarnSoft)]
    [InlineData(80, 60.0, 95, HealthSeverity.Danger)]
    [InlineData(null, null, null, HealthSeverity.Ok)]
    public void HardwareDot_IsTheWorstOfRamDiskAndCpu(int? ram, double? disk, int? cpu, HealthSeverity expected) =>
        Assert.Equal(expected, HealthSeverityRules.HardwareDot(ram, disk, cpu));

    [Theory]
    [InlineData(true, 10, HealthSeverity.Info)]
    [InlineData(null, null, HealthSeverity.Info)]
    [InlineData(false, 10, HealthSeverity.Warn)]
    [InlineData(true, 500, HealthSeverity.Warn)]
    [InlineData(true, 499, HealthSeverity.Info)]
    public void RevitDot(bool? accel, int? warnings, HealthSeverity expected) =>
        Assert.Equal(expected, HealthSeverityRules.RevitDot(accel, warnings));
}

public class HealthFormatTests
{
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0.0, "0 B")]
    [InlineData(512.0, "512 B")]
    [InlineData(1023.0, "1023 B")]
    [InlineData(1024.0, "1.0 KB")]
    [InlineData(4096.0, "4.0 KB")]
    [InlineData(5.2 * 1024 * 1024 * 1024, "5.2 GB")]
    [InlineData(140.4 * 1024 * 1024, "140 MB")]
    [InlineData(3.0 * 1024 * 1024 * 1024 * 1024 * 1024, "3072 TB")]
    public void Bytes_AutoScalesBinaryUnits(double? n, string expected) => Assert.Equal(expected, HealthFormat.Bytes(n));

    [Fact]
    public void Numbers_AreGroupedLikeToLocaleString()
    {
        Assert.Equal("16,384 MB", HealthFormat.Mb(16384, EnUs));
        Assert.Equal("123.45 GB", HealthFormat.Gb(123.45, EnUs));
        Assert.Equal("1,000 Mbps", HealthFormat.Mbps(1000, EnUs));
        Assert.Equal("1,234.568", HealthFormat.Num(1234.5678, EnUs));
        Assert.Equal("—", HealthFormat.Num(null, EnUs));
        Assert.Equal("—", HealthFormat.Mb(null, EnUs));
        Assert.Equal("—", HealthFormat.Gb(null, EnUs));
        Assert.Equal("—", HealthFormat.Mbps(null, EnUs));
    }

    [Fact]
    public void Pct_IsTheRawNumber()
    {
        Assert.Equal("45.3%", HealthFormat.Pct(45.3));
        Assert.Equal("80%", HealthFormat.Pct(80));
        Assert.Equal("—", HealthFormat.Pct(null));
    }

    [Theory]
    [InlineData(null, "—")]
    [InlineData("", "—")]
    [InlineData("SSD", "SSD")]
    public void Str(string? s, string expected) => Assert.Equal(expected, HealthFormat.Str(s));

    [Fact]
    public void Time_IsLocalGeneralDateTime_OrTheRawTextWhenUnparseable()
    {
        // ICU may put a narrow no-break space before the AM/PM designator.
        Assert.Equal("7/20/2026 2:30:00 PM",
            HealthFormat.Time("2026-07-20T14:30:00Z", EnUs, TimeZoneInfo.Utc).Replace('\u202F', ' ').Replace('\u00A0', ' '));
        Assert.Equal("—", HealthFormat.Time(null, EnUs, TimeZoneInfo.Utc));
        Assert.Equal("—", HealthFormat.Time("", EnUs, TimeZoneInfo.Utc));
        Assert.Equal("not a date", HealthFormat.Time("not a date", EnUs, TimeZoneInfo.Utc));
    }

    [Theory]
    [InlineData(0.0, "0.0s")]
    [InlineData(9.95, "9.9s")]
    [InlineData(10.0, "10s")]
    [InlineData(10.5, "11s")]
    [InlineData(89.4, "89s")]
    [InlineData(125.0, "2m 05s")]
    [InlineData(5399.0, "89m 59s")]
    [InlineData(7260.0, "2h 01m")]
    [InlineData(double.NaN, "—")]
    [InlineData(double.PositiveInfinity, "—")]
    public void Dur_EveryBranch(double seconds, string expected) => Assert.Equal(expected, HealthFormat.Dur(seconds));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0.0, "0h")]
    [InlineData(4.0, "4h")]
    [InlineData(2.5, "2.5h")]
    [InlineData(2.25, "2.3h")]
    [InlineData(0.3, "0.3h")]
    [InlineData(12.04, "12h")]
    public void HoursShort(double? hours, string expected) => Assert.Equal(expected, HealthFormat.HoursShort(hours));

    [Theory]
    [InlineData(null, "—")]
    [InlineData(-1.0, "—")]
    [InlineData(0.0, "00:00:00")]
    [InlineData(999.0, "00:00:00")]
    [InlineData(61_000.0, "00:01:01")]
    [InlineData(3_723_000.0, "01:02:03")]
    [InlineData(360_000_000.0, "100:00:00")]
    public void Elapsed(double? ms, string expected) => Assert.Equal(expected, HealthFormat.Elapsed(ms));
}
