// rst-c's own (not vendored): the tray's model detail rules (spec #25, Model Detail; F19 #25 WU4). Pure, so RST.Tests
// covers them on Linux. The detail reads the model through spec #24's shared read path
// (ModelActivityReader.ReadModel) and renders it with RST.UI's ModelActivityPanel, the panel Revit's Activity tab
// uses; what is tray-only lives here: the 1Y-aware range a pill or the list's chip maps to, the Revit versions the
// model was opened in, the "as of" day of the last known Warning Count and File Size, and telling a model whose
// history is gone (its rollup deleted or unreadable, nothing unfolded) from one with no activity in the range.

using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;

namespace RST.Core.Tray;

public static class ModelDetail
{
    /// <summary>The range pills, in order: 7D · 1M · 3M · 6M · 1Y.</summary>
    public static IReadOnlyList<ActivityRange> Ranges => ActivityRanges.PillsWithYear;

    /// <summary>The pill a detail range names (the list hands one over with each opened card).</summary>
    public static ActivityRange RangeFor(ModelDetailRange range) => ActivityRanges.For((int)range);

    /// <summary>
    /// Every Revit version the model was opened in, newest first: each minor version recorded ("2026.4"), and a major
    /// with no minor recorded (data from before spec #24) as the major alone ("2025").
    /// </summary>
    public static IReadOnlyList<string> VersionsOpened(IEnumerable<RollupDay> days)
    {
        var minors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var majors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var day in days)
        {
            foreach (var v in day.RevitSubversions)
                if (!string.IsNullOrWhiteSpace(v)) minors.Add(v.Trim());
            foreach (var v in day.RevitVersions)
                if (!string.IsNullOrWhiteSpace(v)) majors.Add(v.Trim());
        }
        var covered = new HashSet<string>(minors.Select(ModelList.MajorOf), StringComparer.OrdinalIgnoreCase);
        return minors.Concat(majors.Where(m => !covered.Contains(m)))
            .OrderByDescending(v => v, RevitVersionOrder.Instance)
            .ToList();
    }

    /// <summary>The local day the last known model_stats were recorded on (the "as of" date); null with none.</summary>
    public static DateOnly? StatsAsOf(ModelActivity model, TimeZoneInfo zone) =>
        model.LastStats is { } s ? ActiveTime.LocalDay(s.Ts, zone) : null;

    /// <summary>
    /// The read found nothing of the model: no rollup loaded and nothing unfolded (its rollup and index row deleted, or
    /// both unreadable). The detail says so instead of showing an empty model.
    /// </summary>
    public static bool IsGone(ModelActivity model) => model.ModelIds.Count == 0 && !model.HasData;

    /// <summary>The model's current health: its newest day's inputs from the read (folded or not), else the list's.</summary>
    public static ModelHealthColour Health(ModelActivity model, ModelHealthColour listed) =>
        model.CurrentHealth is { } h ? ModelHealth.Evaluate(h).Colour : listed;
}
