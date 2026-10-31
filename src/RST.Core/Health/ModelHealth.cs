// rst-c's own (not vendored): the per-model health score and colour, v1
// (decision #55, superseding #51; spec #24, Model Health). Pure.
//
// Four inputs, 25 points each, each penalty linear between its no-penalty
// and full-penalty points:
//   model size            500 MB → 0,      4 GB → 25
//   warning density       0.5 / MB → 0,    2 / MB → 25   (warnings ÷ size in MB)
//   open time (median of the last 10 opens)  30 s → 0,  5 min → 25
//   sync time (median of the last 10 syncs)  30 s → 0,  5 min → 25
// Score = 100 − the penalties. An input with no data is left out and the
// remaining inputs are scaled to 100 (FnB 2026-10-31); no inputs at all is
// Grey ("not enough data"). The score is internal — never shown, logged
// only at debug level; callers show the colour.

namespace RST.Core.Health;

/// <summary>The four health inputs as they stood at one moment (the end of a day, or now).
/// Null = no data for that input.</summary>
public sealed record HealthInputs(
    long? FileSizeBytes,
    int? WarningsCount,
    double? OpenMedianSeconds,
    double? SyncMedianSeconds)
{
    public static readonly HealthInputs None = new(null, null, null, null);
}

/// <summary>The four bands plus Grey for "not enough data".</summary>
public enum ModelHealthColour
{
    Grey,
    Green,
    Yellow,
    Amber,
    Red,
}

/// <summary>A score (0–100, null when Grey) and its colour.</summary>
public readonly record struct ModelHealthResult(double? Score, ModelHealthColour Colour);

public static class ModelHealth
{
    public const double BytesPerMb = 1024d * 1024d;

    public const double SizeNoPenaltyMb = 500;
    public const double SizeFullPenaltyMb = 4096;
    public const double DensityNoPenaltyPerMb = 0.5;
    public const double DensityFullPenaltyPerMb = 2;
    public const double TimeNoPenaltySeconds = 30;
    public const double TimeFullPenaltySeconds = 300;

    /// <summary>The medians use this many of the model's most recent opens and syncs.</summary>
    public const int MedianWindow = 10;

    public const double GreenAtOrAbove = 75;
    public const double YellowAtOrAbove = 50;
    public const double AmberAtOrAbove = 25;

    /// <summary>Score and colour for <paramref name="inputs"/>; Grey with a null score when no input has data.</summary>
    public static ModelHealthResult Evaluate(HealthInputs? inputs)
    {
        if (inputs is null) return new ModelHealthResult(null, ModelHealthColour.Grey);

        // Each present input's penalty as a fraction of its full 25 points.
        var fractions = new List<double>(4);
        double? sizeMb = inputs.FileSizeBytes is { } bytes && bytes > 0 ? bytes / BytesPerMb : null;
        if (sizeMb is { } mb)
        {
            fractions.Add(Linear(mb, SizeNoPenaltyMb, SizeFullPenaltyMb));
            if (inputs.WarningsCount is { } w && w >= 0)
                fractions.Add(Linear(w / mb, DensityNoPenaltyPerMb, DensityFullPenaltyPerMb));
        }
        if (inputs.OpenMedianSeconds is { } open && open >= 0)
            fractions.Add(Linear(open, TimeNoPenaltySeconds, TimeFullPenaltySeconds));
        if (inputs.SyncMedianSeconds is { } sync && sync >= 0)
            fractions.Add(Linear(sync, TimeNoPenaltySeconds, TimeFullPenaltySeconds));

        if (fractions.Count == 0) return new ModelHealthResult(null, ModelHealthColour.Grey);

        // Missing inputs are left out; the present ones share the 100 points.
        var perInput = 100.0 / fractions.Count;
        var score = 100.0 - fractions.Sum(f => f * perInput);
        score = Math.Clamp(score, 0, 100);
        return new ModelHealthResult(score, ColourFor(score));
    }

    /// <summary>The band for a score: green 75–100, yellow 50–74, amber 25–49, red 0–24
    /// (a fractional score belongs to the band whose lower edge it reaches).</summary>
    public static ModelHealthColour ColourFor(double score) => score switch
    {
        >= GreenAtOrAbove => ModelHealthColour.Green,
        >= YellowAtOrAbove => ModelHealthColour.Yellow,
        >= AmberAtOrAbove => ModelHealthColour.Amber,
        _ => ModelHealthColour.Red,
    };

    /// <summary>Health's four existing severity levels, so the tray and add-in colours match;
    /// Grey draws no colour.</summary>
    public static HealthSeverity ToSeverity(ModelHealthColour colour) => colour switch
    {
        ModelHealthColour.Green => HealthSeverity.Ok,
        ModelHealthColour.Yellow => HealthSeverity.WarnSoft,
        ModelHealthColour.Amber => HealthSeverity.Warn,
        ModelHealthColour.Red => HealthSeverity.Danger,
        _ => HealthSeverity.None,
    };

    /// <summary>Median of <paramref name="seconds"/> (already the last N); null when empty.
    /// An even count averages the middle two.</summary>
    public static double? Median(IReadOnlyList<double> seconds)
    {
        if (seconds.Count == 0) return null;
        var sorted = seconds.ToArray();
        Array.Sort(sorted);
        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }

    /// <summary>0 at or below <paramref name="none"/>, 1 at or above <paramref name="full"/>, linear between.</summary>
    private static double Linear(double value, double none, double full)
    {
        if (value <= none) return 0;
        if (value >= full) return 1;
        return (value - none) / (full - none);
    }
}
