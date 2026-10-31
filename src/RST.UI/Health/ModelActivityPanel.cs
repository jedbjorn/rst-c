// rst-c's own (not vendored): one model's activity cards and charts (spec #24, Revit Health and
// Model Health), shared by Revit's Health → Activity tab and the tray's model detail (spec #25,
// WU4). It renders a ModelActivity (ModelActivityReader.ReadCurrentModel in Revit, ReadModel in
// the tray) for a range: the stat cards, and four charts in order — Active time, Opening time,
// Sync history and the Health trend — per day or per event at 7D / 1M / 3M, per ISO week at 6M
// and 1Y. Nothing here knows Revit: what Revit (or the tray) adds about the model comes in as
// ModelActivityFacts. Templates: StatCardTemplate and ChartCardTemplate in HealthResources.xaml.

using System.Globalization;
using RST.Core.Health;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Health;

/// <summary>
/// What the host knows about the model beyond its activity: whether it syncs (Sync history applies
/// only to workshared and cloud models), and the Warning Count and File Size cards' values with an
/// optional sub-line under both (the tray's "as of" date; empty in Revit, which reads them live).
/// </summary>
public sealed record ModelActivityFacts(
    bool? IsWorkshared,
    bool? IsCloud,
    int? WarningsCount,
    double? FileSizeBytes,
    string StatsSub = "")
{
    /// <summary>The facts the rollups themselves hold: the latest identity's flags and the last
    /// recorded model_stats ("last known"), for a host that cannot ask Revit (the tray).</summary>
    public static ModelActivityFacts FromRollup(ModelActivity model, string statsSub = "") => new(
        model.Identity.IsWorkshared, model.Identity.IsCloud,
        model.LastStats?.WarningsCount, model.LastStats?.FileSizeBytes, statsSub);
}

/// <summary>A model's activity cards and charts for one range. Call one of the Render methods after each read.</summary>
public sealed class ModelActivityPanel : ObservableObject
{
    private readonly CultureInfo _culture;
    private readonly TimeZoneInfo _zone;

    /// <param name="includeSession">Revit shows Activity (Session) first; the tray has no live session and leaves it out.</param>
    /// <param name="culture">Formats dates and counts.</param>
    /// <param name="zone">The zone dates are shown in (the user's local zone in production; the zone the model's days are in).</param>
    public ModelActivityPanel(bool includeSession, CultureInfo culture, TimeZoneInfo zone)
    {
        _culture = culture;
        _zone = zone;

        ActivitySession = includeSession ? new StatCard(UiStrings.Health_StatActivitySession, "Health.Stat.ActivitySession") : null;
        ActivityTotal = new StatCard(UiStrings.Health_StatActivityTotal, "Health.Stat.ActivityTotal");
        LongestSync = new StatCard(UiStrings.Health_StatLongestSync, "Health.Stat.LongestSync");
        LongestOpen = new StatCard(UiStrings.Health_StatLongestOpen, "Health.Stat.LongestOpen");
        WarningCount = new StatCard(UiStrings.Health_StatWarnings, "Health.Stat.Warnings");
        FileSize = new StatCard(UiStrings.Health_StatFileSize, "Health.Stat.FileSize");
        StatCards = ActivitySession is { } s
            ? [s, ActivityTotal, LongestSync, LongestOpen, WarningCount, FileSize]
            : [ActivityTotal, LongestSync, LongestOpen, WarningCount, FileSize];

        ActiveChart = new ChartCard(UiStrings.Health_ChartActive, "Health.Chart.Active");
        OpenChart = new ChartCard(UiStrings.Health_ChartOpen, "Health.Chart.Open");
        SyncChart = new ChartCard(UiStrings.Health_ChartSync, "Health.Chart.Sync");
        HealthChart = new ChartCard(UiStrings.Health_ChartHealth, "Health.Chart.Health");
        Charts = [ActiveChart, OpenChart, SyncChart, HealthChart];

        RenderNoModel();
    }

    /// <summary>Activity (Session): Revit only; null in the tray.</summary>
    public StatCard? ActivitySession { get; }

    public StatCard ActivityTotal { get; }

    public StatCard LongestSync { get; }

    public StatCard LongestOpen { get; }

    public StatCard WarningCount { get; }

    public StatCard FileSize { get; }

    /// <summary>In order: Activity (Session) (Revit only), Activity (Total), Longest Sync, Longest Open Time, Warning Count, File Size.</summary>
    public IReadOnlyList<StatCard> StatCards { get; }

    public ChartCard ActiveChart { get; }

    public ChartCard OpenChart { get; }

    public ChartCard SyncChart { get; }

    public ChartCard HealthChart { get; }

    /// <summary>In order: Active time, Opening time, Sync history, Health.</summary>
    public IReadOnlyList<ChartCard> Charts { get; }

    /// <summary>No model open: every card "—", every chart "No model open".</summary>
    public void RenderNoModel()
    {
        ClearCards();
        foreach (var chart in Charts) chart.Set(null, UiStrings.Health_NoModelTitle, UiStrings.Health_NoModelHint);
    }

    /// <summary>The read failed: every card "—", every chart "Could not load activity" and why.</summary>
    public void RenderError(string message)
    {
        ClearCards();
        foreach (var chart in Charts) chart.Set(null, UiStrings.Health_ActivityFailedTitle, message);
    }

    /// <summary>Renders <paramref name="model"/> over <paramref name="range"/> ending <paramref name="today"/> (a local calendar day).</summary>
    public void Render(ModelActivity model, ActivityRange range, DateOnly today, ModelActivityFacts facts)
    {
        RenderCards(model, range, today, facts);
        RenderActiveChart(model, range, today);
        RenderOpenChart(model, range, today);
        if (facts.IsWorkshared != true && facts.IsCloud != true)
            SyncChart.Set(null, UiStrings.Health_NotWorksharedTitle, UiStrings.Health_NotWorksharedHint);
        else
            RenderSyncChart(model, range, today);
        RenderHealthChart(model, range, today);
    }

    // ── cards ───────────────────────────────────────────────────────────

    private void ClearCards()
    {
        foreach (var card in StatCards)
        {
            card.Value = HealthFormat.Missing;
            card.Sub = "";
        }
    }

    private void RenderCards(ModelActivity model, ActivityRange range, DateOnly today, ModelActivityFacts facts)
    {
        if (ActivitySession is { } session)
        {
            // Not ticking (spec #24, Out of Scope): it refreshes with the tab.
            session.Value = model.SessionActiveSeconds is { } s ? HealthFormat.Active(s) : HealthFormat.Missing;
            session.Sub = model.LiveOpenSinceUtc is { } since ? UiStrings.Health_OpenedAt(OpenedLabel(since, today)) : "";
        }
        var totals = ModelActivityCharts.Totals(model, range, today);
        ActivityTotal.Value = HealthFormat.Active(totals.ActiveSeconds);
        LongestSync.Value = HealthFormat.Dur(totals.LongestSyncSeconds);
        LongestOpen.Value = HealthFormat.Dur(totals.LongestOpenSeconds);
        WarningCount.Value = HealthFormat.Num(facts.WarningsCount, _culture);
        FileSize.Value = HealthFormat.Bytes(facts.FileSizeBytes);
        WarningCount.Sub = facts.StatsSub;
        FileSize.Sub = facts.StatsSub;
    }

    /// <summary>The open time, with the date when the model was opened before today (a session left open overnight).</summary>
    private string OpenedLabel(DateTimeOffset since, DateOnly today)
    {
        var local = LocalTime(since);
        return DateOnly.FromDateTime(local.DateTime) == today ? local.ToString("t", _culture) : local.ToString("g", _culture);
    }

    // ── charts ──────────────────────────────────────────────────────────

    private void RenderActiveChart(ModelActivity model, ActivityRange range, DateOnly today)
    {
        // One point per day or week, zeros included; a range with no active time has no history.
        var points = ModelActivityCharts.ActiveTime(model, range, today).Select(p => new ActivityChartPoint(
            LocalMidnightMs(p.Date), p.Value / 3600,
            PeriodLabel(p.Date, range, HealthFormat.Active(p.Value)))).ToList();
        if (points.Any(p => p.Y > 0))
            ActiveChart.Set(ActivityChartLayout.Build(points, HealthFormat.HoursAxis, TickLabel));
        else
            ActiveChart.Set(null, UiStrings.Health_NoTimeTitle, model.MatchedKeyKind is null ? UiStrings.Health_NoKeyHint : "");
    }

    private void RenderOpenChart(ModelActivity model, ActivityRange range, DateOnly today) =>
        SetDurationChart(OpenChart, range.IsWeekly
            ? WeeklyPoints(ModelActivityCharts.WeeklyOpens(model, range, today))
            : EventPoints(ModelActivityCharts.Opens(model, range, today)), UiStrings.Health_NoOpensTitle);

    private void RenderSyncChart(ModelActivity model, ActivityRange range, DateOnly today) =>
        SetDurationChart(SyncChart, range.IsWeekly
            ? WeeklyPoints(ModelActivityCharts.WeeklySyncs(model, range, today))
            : EventPoints(ModelActivityCharts.Syncs(model, range, today)), UiStrings.Health_NoSyncsTitle);

    private void RenderHealthChart(ModelActivity model, ActivityRange range, DateOnly today)
    {
        // One dot per day used (or per week), at its score's height, coloured by its band; the
        // tooltip names the date and the colour only — never the score (decision #55).
        var dots = ModelActivityCharts.HealthTrend(model, range, today).Select(p => new HealthTrendDot(
            LocalMidnightMs(p.Date), p.Health.Score, ModelHealth.ToSeverity(p.Health.Colour),
            UiStrings.Health_ChartPoint(Iso(p.Date), ColourName(p.Health.Colour)))).ToList();
        switch (dots.Count)
        {
            case 0:
                HealthChart.Set(null, UiStrings.Health_NoTimeTitle, model.MatchedKeyKind is null ? UiStrings.Health_NoKeyHint : "");
                break;
            case 1:
                HealthChart.Set(ActivityChartLayout.BuildHealthTrend(dots, TickLabel), "", UiStrings.Health_NotEnoughHistory);
                break;
            default:
                HealthChart.Set(ActivityChartLayout.BuildHealthTrend(dots, TickLabel));
                break;
        }
    }

    /// <summary>A model health colour's name: Green, Yellow, Amber, Red, or "Not enough data".</summary>
    public static string ColourName(ModelHealthColour colour) => colour switch
    {
        ModelHealthColour.Green => UiStrings.Health_ColourGreen,
        ModelHealthColour.Yellow => UiStrings.Health_ColourYellow,
        ModelHealthColour.Amber => UiStrings.Health_ColourAmber,
        ModelHealthColour.Red => UiStrings.Health_ColourRed,
        _ => UiStrings.Health_ColourGrey,
    };

    private List<ActivityChartPoint> EventPoints(IReadOnlyList<DurationPoint> events) =>
        events.Select(e => new ActivityChartPoint(
            e.Ts.ToUnixTimeMilliseconds(), e.Seconds,
            UiStrings.Health_ChartPoint(LocalTime(e.Ts).ToString("G", _culture), HealthFormat.Dur(e.Seconds)))).ToList();

    private List<ActivityChartPoint> WeeklyPoints(IReadOnlyList<ActivityPeriodValue> weeks) =>
        weeks.Select(w => new ActivityChartPoint(
            LocalMidnightMs(w.Date), w.Value,
            UiStrings.Health_ChartWeekAverage(Iso(w.Date), HealthFormat.Dur(w.Value), HealthFormat.Number(w.Count, _culture)))).ToList();

    private void SetDurationChart(ChartCard card, IReadOnlyList<ActivityChartPoint> points, string emptyTitle)
    {
        switch (ActivityChartMath.Mode(points.Count))
        {
            case ChartMode.Empty:
                card.Set(null, emptyTitle);
                break;
            case ChartMode.Dots:
                card.Set(ActivityChartLayout.Build(points, HealthFormat.DurAxis, TickLabel), "", UiStrings.Health_NotEnoughHistory);
                break;
            default:
                card.Set(ActivityChartLayout.Build(points, HealthFormat.DurAxis, TickLabel));
                break;
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    private string PeriodLabel(DateOnly date, ActivityRange range, string value) => range.IsWeekly
        ? UiStrings.Health_ChartWeekPoint(Iso(date), value)
        : UiStrings.Health_ChartPoint(Iso(date), value);

    private static string Iso(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private DateTimeOffset LocalTime(DateTimeOffset ts) => TimeZoneInfo.ConvertTime(ts, _zone);

    /// <summary>The day's local midnight, in epoch milliseconds (JS <c>Date.parse(date + 'T00:00:00')</c>).</summary>
    private double LocalMidnightMs(DateOnly date)
    {
        var local = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return new DateTimeOffset(local, _zone.GetUtcOffset(local)).ToUnixTimeMilliseconds();
    }

    /// <summary>A date tick: JS <c>toLocaleDateString(undefined, { month: 'short', day: 'numeric' })</c>.</summary>
    private string TickLabel(double ms) =>
        HealthFormat.ShortMonthDay(LocalTime(DateTimeOffset.FromUnixTimeMilliseconds((long)ms)), _culture);
}
