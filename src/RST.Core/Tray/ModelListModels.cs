// rst-c's own (not vendored): the shapes of the tray Health page's Models list (spec #25,
// Models; F19 #25 WU1). A ModelListQuery (the search box, time chip, Revit-version filter and
// group-by) goes in; a ModelListResult (aggregate cards, groups of model cards, the empty
// state, "Searching all time") comes out. No UI text here: RST.Tray turns the flags into
// TrayStrings ("unknown minor", "Searching all time", the empty-state messages).

using RST.Core.Health;

namespace RST.Core.Tray;

/// <summary>The time chips. Recent is the last 14 calendar days; 3M, 6M and 1Y are the last 13,
/// 26 and 52 ISO weeks including the current one (the weekly buckets the long-range charts plot).</summary>
public enum ModelTimeChip
{
    Recent,
    ThreeMonths,
    SixMonths,
    OneYear,
}

/// <summary>Group by: major version (2026, the default), minor version (2026.4), or one flat list.</summary>
public enum ModelGroupBy
{
    MajorVersion,
    MinorVersion,
    None,
}

/// <summary>Which empty-state message the list shows, if any.</summary>
public enum ModelListEmptyState
{
    /// <summary>The list has cards.</summary>
    None,

    /// <summary>The index has no models: "No model activity yet…".</summary>
    NoModels,

    /// <summary>The chip and version filter leave nothing: "No models in this time frame" (with the
    /// switch-to-1Y link when <see cref="ModelListResult.OfferOneYear"/>).</summary>
    NothingInTimeFrame,

    /// <summary>The search finds nothing: "No models match".</summary>
    NoMatches,
}

/// <summary>The model detail's range pills. The value is the range in days, as
/// <see cref="ActivityRanges"/> counts them (1Y is new in the tray).</summary>
public enum ModelDetailRange
{
    SevenDays = 7,
    OneMonth = 30,
    ThreeMonths = 90,
    SixMonths = 180,
    OneYear = 365,
}

/// <summary>The card's model-kind marker. A cloud model is also workshared; it shows Cloud.</summary>
public enum ModelMarker
{
    None,
    Workshared,
    Cloud,
}

/// <summary>What the user has chosen on the Models page.</summary>
public sealed record ModelListQuery
{
    public static readonly ModelListQuery Default = new();

    /// <summary>The search box. Any non-blank text searches every model, all time, and the chip
    /// and version filter are ignored until it is cleared.</summary>
    public string? Search { get; init; }

    public ModelTimeChip Chip { get; init; } = ModelTimeChip.Recent;

    /// <summary>Selected major versions ("2026"); empty = All.</summary>
    public IReadOnlyCollection<string> Versions { get; init; } = Array.Empty<string>();

    public ModelGroupBy GroupBy { get; init; } = ModelGroupBy.MajorVersion;

    /// <summary>The trimmed search text; null when the box is blank.</summary>
    public string? SearchText => string.IsNullOrWhiteSpace(Search) ? null : Search.Trim();

    /// <summary>True while the box has text: the filter row shows "Searching all time".</summary>
    public bool IsSearching => SearchText is not null;
}

/// <summary>A model's or a selection's totals over the window (all time when searching),
/// implausible values already clamped.</summary>
public sealed record ModelListTotals(
    double ActiveSeconds,
    double OpenSeconds,
    long Opens,
    double OpenSecondsSum,
    long Syncs,
    double SyncSecondsSum)
{
    public static readonly ModelListTotals Zero = new(0, 0, 0, 0, 0, 0);

    /// <summary>Open seconds ÷ opens; null with no opens.</summary>
    public double? AverageOpenSeconds => Opens > 0 ? OpenSecondsSum / Opens : null;

    /// <summary>Sync seconds ÷ syncs; null with no syncs ("—").</summary>
    public double? AverageSyncSeconds => Syncs > 0 ? SyncSecondsSum / Syncs : null;
}

/// <summary>The aggregate cards over the visible cards (decision #59: averages, not medians).</summary>
public sealed record ModelListAggregates(
    int Models,
    double ActiveSeconds,
    long Opens,
    double? AverageOpenSeconds,
    long Syncs,
    double? AverageSyncSeconds)
{
    public static readonly ModelListAggregates Zero = new(0, 0, 0, null, 0, null);
}

/// <summary>One model card.</summary>
public sealed class ModelCard
{
    public string ModelId { get; init; } = "";

    /// <summary>The model's title; its file name (without extension) when it has none.</summary>
    public string Title { get; init; } = "";

    /// <summary>The model's file name (local, else central); null when unknown.</summary>
    public string? FileName { get; init; }

    /// <summary>True when the card shows <see cref="FileName"/> under the title (it differs from the
    /// title, with or without its extension; never for cloud models, whose local file is a cache).</summary>
    public bool ShowFileName { get; init; }

    public string? LocalPath { get; init; }
    public string? CentralPath { get; init; }
    public bool IsWorkshared { get; init; }
    public bool IsCloud { get; init; }
    public ModelMarker Marker { get; init; }

    /// <summary>From the model's current health inputs (decision #55); Grey = "Not enough data".</summary>
    public ModelHealthColour Health { get; init; }

    /// <summary>Health's severity for <see cref="Health"/> (the dot's colour); None for Grey.</summary>
    public HealthSeverity HealthSeverity { get; init; }

    /// <summary>The newest major version the model was opened in within the window (all time when
    /// searching), whatever the version filter; null when none is recorded.</summary>
    public string? LatestMajor { get; init; }

    /// <summary>That major's newest minor version in the window; null when unknown.</summary>
    public string? LatestMinor { get; init; }

    /// <summary>What the card shows: the minor where known, else the major; null when none.</summary>
    public string? LatestVersion => LatestMinor ?? LatestMajor;

    /// <summary>The major the card is grouped under: <see cref="LatestMajor"/>, or under a version
    /// filter the newest selected major in the window (so it can be older than the card's version).</summary>
    public string? GroupMajor { get; init; }

    /// <summary>That group major's newest minor in the window; null when unknown.</summary>
    public string? GroupMinor { get; init; }

    public DateOnly? LastActiveDay { get; init; }
    public DateOnly? FirstActiveDay { get; init; }

    /// <summary>The card's totals over the window (all time when searching).</summary>
    public ModelListTotals Totals { get; init; } = ModelListTotals.Zero;

    /// <summary>Active time in the window, shown on the card.</summary>
    public double ActiveSeconds => Totals.ActiveSeconds;
}

/// <summary>One collapsible group (or the one flat list with Group by None).</summary>
public sealed class ModelGroup
{
    /// <summary>Stable across queries ("2026", "2026.4", "2026/unknown", "unknown", "all"), for
    /// keeping a group's expanded state.</summary>
    public string Key { get; init; } = "";

    public ModelGroupBy GroupBy { get; init; }

    /// <summary>The group's major version; null for the flat list and for models with no version recorded.</summary>
    public string? Major { get; init; }

    /// <summary>The minor version (Group by minor); null otherwise or when unknown.</summary>
    public string? Minor { get; init; }

    /// <summary>Group by minor, the major is known but no minor is ("2026 — unknown minor").</summary>
    public bool IsUnknownMinor { get; init; }

    /// <summary>Grouped, but the models carry no Revit version at all.</summary>
    public bool IsUnknownVersion { get; init; }

    /// <summary>The header's version text without the "unknown" wording: the minor, else the major.</summary>
    public string? Version => Minor ?? Major;

    public IReadOnlyList<ModelCard> Cards { get; init; } = Array.Empty<ModelCard>();

    public int Count => Cards.Count;

    /// <summary>The header's active time: the sum over the group's cards.</summary>
    public double ActiveSeconds { get; init; }
}

/// <summary>Everything the Models page binds for one query.</summary>
public sealed class ModelListResult
{
    public ModelListQuery Query { get; init; } = ModelListQuery.Default;

    /// <summary>The filter row shows "Searching all time"; every number is all-time.</summary>
    public bool IsSearching { get; init; }

    /// <summary>The window's first and last day; both null while searching (all time).</summary>
    public DateOnly? WindowFirst { get; init; }
    public DateOnly? WindowLast { get; init; }

    public ModelListAggregates Aggregates { get; init; } = ModelListAggregates.Zero;

    /// <summary>Newest version first; with Group by None, one group holding every card.</summary>
    public IReadOnlyList<ModelGroup> Groups { get; init; } = Array.Empty<ModelGroup>();

    /// <summary>Every visible card in display order (the groups' cards, concatenated).</summary>
    public IReadOnlyList<ModelCard> Cards { get; init; } = Array.Empty<ModelCard>();

    public ModelListEmptyState EmptyState { get; init; }

    /// <summary>The "No models in this time frame" message offers the switch to 1Y (it is not on 1Y already).</summary>
    public bool OfferOneYear { get; init; }

    /// <summary>The range the model detail opens on when a card is opened from this list.</summary>
    public ModelDetailRange DetailRange { get; init; }

    /// <summary>The Revit version filter's choices: every major in the index, newest first.</summary>
    public IReadOnlyList<string> AvailableVersions { get; init; } = Array.Empty<string>();

    /// <summary>Models in the index, visible or not.</summary>
    public int IndexedModels { get; init; }
}
