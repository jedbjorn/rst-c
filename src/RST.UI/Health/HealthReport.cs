// rst-c's own (not vendored): the Health tab's sections and rows, ported from render(), row()
// and section() of the retired src/RST.UI/Assets/health_viewer.html (spec #16, WU2). The same
// four sections, rows, sub-lines, severity colors and "—" for a missing value; the severity
// thresholds and formatters are RST.Core's (HealthSeverityRules, HealthFormat).

using System.Globalization;
using RST.Core.Health;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Health;

/// <summary>A run of text in a sub-line, colored by its severity (<see cref="HealthSeverity.None"/>: the sub-line's muted color).</summary>
public sealed record HealthRun(string Text, HealthSeverity Severity = HealthSeverity.None);

/// <summary>One row: a label, a value (with its severity color and weight) and grey sub-lines under it.</summary>
public sealed class HealthRow
{
    public HealthRow(string label, string? value, IReadOnlyList<IReadOnlyList<HealthRun>>? subs = null,
                     HealthSeverity valueSeverity = HealthSeverity.None, bool valueBold = false)
    {
        Label = label;
        Subs = subs?.Where(s => s.Count > 0 && s.Any(r => r.Text.Length > 0)).ToList() ?? [];
        // row(): an empty value is "—", and a bare "—" (no sub-lines) is drawn muted and italic.
        IsMissing = string.IsNullOrEmpty(value) || (value == HealthFormat.Missing && Subs.Count == 0);
        Value = string.IsNullOrEmpty(value) ? HealthFormat.Missing : value;
        ValueSeverity = IsMissing ? HealthSeverity.None : valueSeverity;
        ValueBold = valueBold;
    }

    public string Label { get; }

    public string Value { get; }

    public bool IsMissing { get; }

    public HealthSeverity ValueSeverity { get; }

    public bool ValueBold { get; }

    public IReadOnlyList<IReadOnlyList<HealthRun>> Subs { get; }

    public bool HasSubs => Subs.Count > 0;

    /// <summary>The row as plain text (label, value, sub-lines), for logs and tests.</summary>
    public override string ToString() =>
        string.Join(" | ", new[] { Label, Value }.Concat(Subs.Select(s => string.Concat(s.Select(r => r.Text)))));
}

/// <summary>A collapsible section: title, severity dot (<see cref="HealthSeverity.None"/>: no dot) and rows. Collapsed by default.</summary>
public sealed class HealthSection : ObservableObject
{
    private bool _isExpanded;

    public HealthSection(string title, HealthSeverity dot, IReadOnlyList<HealthRow> rows)
    {
        Title = title;
        Dot = dot;
        Rows = rows;
    }

    public string Title { get; }

    public HealthSeverity Dot { get; }

    public bool HasDot => Dot != HealthSeverity.None;

    public IReadOnlyList<HealthRow> Rows { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetField(ref _isExpanded, value);
    }

    /// <summary>The title (the expander's accessible name).</summary>
    public override string ToString() => Title;
}

/// <summary>The Health tab built from a snapshot.</summary>
public sealed record HealthReport(string Device, string Captured, IReadOnlyList<HealthSection> Sections)
{
    /// <summary>
    /// Builds the header fields and the four sections from <paramref name="snap"/>. Null (no snapshot
    /// yet, or an unreadable one) renders every value as "—", as the HTML's <c>render(null)</c> did.
    /// </summary>
    public static HealthReport Build(HealthSnapshot? snap, CultureInfo? culture = null, TimeZoneInfo? zone = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        var hasSnap = snap is not null;
        snap ??= new HealthSnapshot();

        var id = snap.Identity ?? new Identity();
        var cpu = snap.Cpu ?? new Cpu();
        var ram = snap.Ram ?? new Ram();
        var disk = snap.Disk ?? new Disk();
        var dsp = snap.Display ?? new Display();
        var net = snap.Network ?? new Network();
        var os = snap.Os ?? new OsInfo();
        var rv = snap.Revit ?? new RevitInfo();
        var mdl = rv.Model ?? new RevitModel();
        var wbs = rv.WarningsBySeverity ?? [];

        var ramSev = HealthSeverityRules.Ram(ram.UsedPercent);
        var cpuSev = HealthSeverityRules.Cpu(cpu.UsedPercent);
        var diskSev = HealthSeverityRules.DiskFree(disk.AvailableGB);
        var modelSev = HealthSeverityRules.ModelSize(mdl.SizeMB);
        var hwDot = HealthSeverityRules.HardwareDot(ram.UsedPercent, disk.AvailableGB, cpu.UsedPercent);

        // Severity on a whole sub-line: only when not OK (the HTML's subCls).
        static HealthSeverity SubSev(HealthSeverity s) => s == HealthSeverity.Ok ? HealthSeverity.None : s;
        static IReadOnlyList<HealthRun> Sub(string text, HealthSeverity severity = HealthSeverity.None) => [new HealthRun(text, severity)];

        var hardware = new List<HealthRow>
        {
            new(UiStrings.Health_RowUser, id.WindowsUsername),
        };

        // CPU: name, then "N cores · M logical" and the load colored by severity (OK in green).
        if (!string.IsNullOrEmpty(cpu.Name))
        {
            var runs = new List<HealthRun>
            {
                new(UiStrings.Health_CpuCores(CoresOrUnknown(cpu.PhysicalCores), CoresOrUnknown(cpu.LogicalCores))),
            };
            if (cpu.UsedPercent is { } used)
            {
                runs.Add(new HealthRun(" · "));
                runs.Add(new HealthRun(UiStrings.Health_CpuUsed(HealthFormat.Pct(used)), cpuSev));
            }
            hardware.Add(new HealthRow(UiStrings.Health_RowCpu, cpu.Name, [runs]));
        }
        else
        {
            hardware.Add(new HealthRow(UiStrings.Health_RowCpu, null));
        }

        hardware.Add(ram.TotalMB is { } totalMb
            ? new HealthRow(UiStrings.Health_RowRam, HealthFormat.Mb(totalMb, c),
                [Sub(UiStrings.Health_RamUsed(HealthFormat.Mb(ram.UsedMB, c), HealthFormat.Pct(ram.UsedPercent)), SubSev(ramSev))])
            : new HealthRow(UiStrings.Health_RowRam, null));

        // One GPU row per adapter (numbered when there are several); the single gpu for older snapshots.
        var gpus = snap.Gpus is { Count: > 0 } list ? list
            : !string.IsNullOrEmpty(snap.Gpu?.Name) ? [snap.Gpu!]
            : [];
        if (gpus.Count == 0)
        {
            hardware.Add(new HealthRow(UiStrings.Health_RowGpu, null));
        }
        else
        {
            for (var i = 0; i < gpus.Count; i++)
            {
                var g = gpus[i];
                hardware.Add(new HealthRow(
                    gpus.Count > 1 ? UiStrings.Health_RowGpuN(i + 1) : UiStrings.Health_RowGpu,
                    g.Name,
                    [Sub(UiStrings.Health_GpuDetails(HealthFormat.Str(g.DriverVersion), HealthFormat.Mb(g.VramTotalMB, c)))]));
            }
        }

        hardware.Add(disk.TotalGB is { } totalGb
            ? new HealthRow(UiStrings.Health_RowDisk, HealthFormat.Str(disk.FriendlyName),
                [Sub(UiStrings.Health_DiskDetails(HealthFormat.Str(disk.Type), HealthFormat.Gb(disk.AvailableGB, c),
                    HealthFormat.Gb(totalGb, c), HealthFormat.Pct(disk.UsedPercent)), SubSev(diskSev))])
            : new HealthRow(UiStrings.Health_RowDisk, null));

        // Junk Files: one row per measured target, then the total; only after a scan measured them.
        if (snap.Cleanup is { Targets.Count: > 0 } cleanup)
        {
            foreach (var t in cleanup.Targets)
            {
                var isIni = t.Kind == RST.Core.Profiles.CleanupTarget.KindIniRecentFiles;
                var count = isIni
                    ? UiStrings.Health_JunkEntries(HealthFormat.Num(t.FileCount, c))
                    : UiStrings.Health_JunkFiles(HealthFormat.Num(t.FileCount, c));
                var first = t.ResolvedPaths is { Count: > 0 } paths && !string.IsNullOrEmpty(paths[0]) ? paths[0] : t.Path ?? "";
                var more = t.ResolvedPaths is { Count: > 1 } ? UiStrings.Health_JunkPaths(t.ResolvedPaths.Count) : "";
                var label = string.IsNullOrEmpty(t.Name) ? UiStrings.Health_Unnamed : t.Name;
                hardware.Add(isIni
                    ? new HealthRow(label, count, [Sub(first + more)])
                    : new HealthRow(label, HealthFormat.Bytes(t.SizeBytes), [Sub(count + " · " + first + more)]));
            }
            hardware.Add(new HealthRow(UiStrings.Health_RowJunkTotal, HealthFormat.Bytes(cleanup.TotalSizeBytes),
                [Sub(UiStrings.Health_JunkTotalDetails(HealthFormat.Num(cleanup.TotalFileCount, c)))], valueBold: true));
        }

        var display = new List<HealthRow>
        {
            new(UiStrings.Health_RowResolution, dsp.PrimaryResolution),
            // A missing snapshot has no monitor count at all ("—"); a scanned one may say 0.
            new(UiStrings.Health_RowMonitors, hasSnap ? HealthFormat.Num(dsp.MonitorCount, c) : HealthFormat.Missing),
            string.IsNullOrEmpty(net.AdapterName)
                ? new HealthRow(UiStrings.Health_RowAdapter, null)
                : new HealthRow(UiStrings.Health_RowAdapter, net.AdapterName, [Sub(HealthFormat.Str(net.Type))]),
            new(UiStrings.Health_RowSpeed, HealthFormat.Mbps(net.SpeedMbps, c)),
        };

        var tooManyWarnings = HealthSeverityRules.TooManyWarnings(rv.WarningsCount);
        var modelSubs = new List<IReadOnlyList<HealthRun>>();
        if (!string.IsNullOrEmpty(mdl.Path)) modelSubs.Add(Sub(mdl.Path));
        if (mdl.SizeMB is { } sizeMb) modelSubs.Add(Sub(UiStrings.Health_ModelSize(HealthFormat.Number(sizeMb, c)), SubSev(modelSev)));
        var breakdown = string.Join(" · ", wbs.Select(kv => kv.Key + ": " + kv.Value.ToString(CultureInfo.InvariantCulture)));

        var revit = new List<HealthRow>
        {
            string.IsNullOrEmpty(rv.Version)
                ? new HealthRow(UiStrings.Health_RowVersion, null)
                : new HealthRow(UiStrings.Health_RowVersion, rv.Version, [Sub(UiStrings.Health_RevitBuild(HealthFormat.Str(rv.Build)))]),
            new(UiStrings.Health_RowUsername, rv.Username),
            new(UiStrings.Health_RowHardwareAccel, Bool(rv.HardwareAcceleration), valueSeverity: rv.HardwareAcceleration switch
            {
                true => HealthSeverity.Ok,
                false => HealthSeverity.Warn,
                null => HealthSeverity.None,
            }),
            string.IsNullOrEmpty(mdl.Name)
                ? new HealthRow(UiStrings.Health_RowModel, null)
                : new HealthRow(UiStrings.Health_RowModel, mdl.Name, modelSubs),
            rv.WarningsCount is { } warnings
                ? new HealthRow(UiStrings.Health_RowWarnings, HealthFormat.Num(warnings, c),
                    breakdown.Length > 0 ? [Sub(breakdown)] : null,
                    tooManyWarnings ? HealthSeverity.Warn : HealthSeverity.None)
                : new HealthRow(UiStrings.Health_RowWarnings, null),
        };

        var osName = string.Join(" ", new[] { os.Name, os.Release }.Where(s => !string.IsNullOrEmpty(s)));
        var osRows = new List<HealthRow>
        {
            new(UiStrings.Health_RowOsName, osName),
            new(UiStrings.Health_RowOsBuild, string.IsNullOrEmpty(os.Build) ? os.Version : os.Build),
        };

        return new HealthReport(
            HealthFormat.Str(id.DeviceName),
            HealthFormat.Time(snap.CaptureTimestamp, c, zone),
            [
                new HealthSection(UiStrings.Health_SectionHardware, hwDot, hardware),
                new HealthSection(UiStrings.Health_SectionDisplayNetwork, HealthSeverity.Info, display),
                new HealthSection(UiStrings.Health_SectionRevit, HealthSeverityRules.RevitDot(rv.HardwareAcceleration, rv.WarningsCount), revit),
                new HealthSection(UiStrings.Health_SectionOs, HealthSeverity.None, osRows),
            ]);
    }

    /// <summary><c>fmt.bool</c>: Enabled, Disabled or Unknown.</summary>
    public static string Bool(bool? value) => value switch
    {
        true => UiStrings.Health_Enabled,
        false => UiStrings.Health_Disabled,
        null => UiStrings.Health_Unknown,
    };

    /// <summary>The JS <c>cores || '?'</c>: zero (not reported) reads "?".</summary>
    private static string CoresOrUnknown(int cores) => cores > 0 ? cores.ToString(CultureInfo.InvariantCulture) : "?";
}
