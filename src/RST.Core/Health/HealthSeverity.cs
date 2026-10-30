// rst-c's own (not vendored): the Health window's severity thresholds, ported from the
// render() function of the retired src/RST.UI/Assets/health_viewer.html (spec #16, WU2).

namespace RST.Core.Health;

/// <summary>
/// A Health value's or section's severity. <see cref="None"/> draws no color (and a section with
/// it shows no dot, as the HTML's Operating System section); <see cref="Info"/> is the accent dot.
/// </summary>
public enum HealthSeverity
{
    None,
    Ok,
    WarnSoft,
    Warn,
    Danger,
    Info,
}

/// <summary>
/// The thresholds the Health tab colors by, unchanged from the HTML viewer:
/// RAM used ≥ 75 % warn, ≥ 85 % danger; CPU used ≥ 60 % soft warn, ≥ 80 % warn, ≥ 90 % danger
/// (one noisy sample, so generous); disk free &lt; 100 GB soft warn, &lt; 50 GB warn, &lt; 30 GB danger;
/// model size ≥ 1024 MB soft warn, ≥ 1536 MB warn, ≥ 2048 MB danger; ≥ 500 warnings warn.
/// A missing measurement is <see cref="HealthSeverity.Ok"/>.
/// </summary>
public static class HealthSeverityRules
{
    public const int RamWarnPercent = 75;
    public const int RamDangerPercent = 85;
    public const int CpuWarnSoftPercent = 60;
    public const int CpuWarnPercent = 80;
    public const int CpuDangerPercent = 90;
    public const double DiskWarnSoftFreeGb = 100;
    public const double DiskWarnFreeGb = 50;
    public const double DiskDangerFreeGb = 30;
    public const double ModelWarnSoftMb = 1024;
    public const double ModelWarnMb = 1536;
    public const double ModelDangerMb = 2048;
    public const int ManyWarnings = 500;

    public static HealthSeverity Ram(int? usedPercent) => usedPercent switch
    {
        null => HealthSeverity.Ok,
        >= RamDangerPercent => HealthSeverity.Danger,
        >= RamWarnPercent => HealthSeverity.Warn,
        _ => HealthSeverity.Ok,
    };

    public static HealthSeverity Cpu(int? usedPercent) => usedPercent switch
    {
        null => HealthSeverity.Ok,
        >= CpuDangerPercent => HealthSeverity.Danger,
        >= CpuWarnPercent => HealthSeverity.Warn,
        >= CpuWarnSoftPercent => HealthSeverity.WarnSoft,
        _ => HealthSeverity.Ok,
    };

    public static HealthSeverity DiskFree(double? availableGb) => availableGb switch
    {
        null => HealthSeverity.Ok,
        < DiskDangerFreeGb => HealthSeverity.Danger,
        < DiskWarnFreeGb => HealthSeverity.Warn,
        < DiskWarnSoftFreeGb => HealthSeverity.WarnSoft,
        _ => HealthSeverity.Ok,
    };

    public static HealthSeverity ModelSize(double? sizeMb) => sizeMb switch
    {
        null => HealthSeverity.Ok,
        >= ModelDangerMb => HealthSeverity.Danger,
        >= ModelWarnMb => HealthSeverity.Warn,
        >= ModelWarnSoftMb => HealthSeverity.WarnSoft,
        _ => HealthSeverity.Ok,
    };

    /// <summary>True from <see cref="ManyWarnings"/> warnings up: the Warnings row and the Revit dot warn.</summary>
    public static bool TooManyWarnings(int? warningsCount) => warningsCount is >= ManyWarnings;

    /// <summary>The worse of two of Ok, WarnSoft, Warn and Danger; on a tie, <paramref name="a"/>.</summary>
    public static HealthSeverity Worse(HealthSeverity a, HealthSeverity b) => Rank(a) >= Rank(b) ? a : b;

    /// <summary>The Hardware section's dot: the worst of RAM, disk and CPU.</summary>
    public static HealthSeverity HardwareDot(int? ramUsedPercent, double? diskAvailableGb, int? cpuUsedPercent) =>
        Worse(Worse(Ram(ramUsedPercent), DiskFree(diskAvailableGb)), Cpu(cpuUsedPercent));

    /// <summary>The Revit section's dot: warn when hardware acceleration is off or there are many warnings, else info.</summary>
    public static HealthSeverity RevitDot(bool? hardwareAcceleration, int? warningsCount) =>
        hardwareAcceleration == false || TooManyWarnings(warningsCount) ? HealthSeverity.Warn : HealthSeverity.Info;

    private static int Rank(HealthSeverity s) => s switch
    {
        HealthSeverity.WarnSoft => 1,
        HealthSeverity.Warn => 2,
        HealthSeverity.Danger => 3,
        _ => 0,
    };
}
