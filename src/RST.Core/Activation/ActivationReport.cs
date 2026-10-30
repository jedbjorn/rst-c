// rst-c's own (not vendored): what the executor did, one outcome per planned change (spec #18,
// In Scope 3 and 5): Done, or NotChanged with a typed reason, with the year and the scope. The
// Loader's report dialog (and later the tray) shows it in plain words; nothing is dropped.

namespace RST.Core.Activation;

public enum OutcomeResult
{
    Done,
    NotChanged,
}

public sealed record ActivationOutcome(
    int Year,
    string Name,
    AddinScope Scope,
    PlanAction Action,
    bool Migrated,
    bool Required,
    OutcomeResult Result,
    ChangeReason? Reason,
    string? Detail,
    string ManifestPath)
{
    public bool IsDone => Result == OutcomeResult.Done;

    /// <summary>The same key as the plan entry it ran (<see cref="PlanEntry.OperationKey"/>).</summary>
    public string OperationKey => Year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Action + "|" + ManifestPath.ToLowerInvariant();

    public static ActivationOutcome For(PlanEntry e, OutcomeResult result, ChangeReason? reason = null, string? detail = null) =>
        new(e.Year, e.Name, e.Scope, e.Action, e.Migrate, e.Required, result, reason, detail, e.ManifestPath);
}

public sealed record ActivationReport(ActivationIntent Intent, IReadOnlyList<ActivationOutcome> Outcomes, IReadOnlyList<string> Notes)
{
    public static ActivationReport Empty(ActivationIntent intent, params string[] notes) => new(intent, [], notes);

    public IReadOnlyList<ActivationOutcome> Done => Outcomes.Where(o => o.IsDone).ToList();

    public IReadOnlyList<ActivationOutcome> NotChanged => Outcomes.Where(o => !o.IsDone).ToList();

    /// <summary>Something changed on disk, so Revit must restart for it to apply.</summary>
    public bool AnyDone => Outcomes.Any(o => o.IsDone);

    public bool HasProblems => Outcomes.Any(o => !o.IsDone);

    /// <summary>The report plus <paramref name="other"/>'s outcomes and notes (the migration's report before a load's).</summary>
    public ActivationReport Merge(ActivationReport? other) =>
        other is null ? this : new ActivationReport(Intent, [.. other.Outcomes, .. Outcomes], [.. other.Notes, .. Notes]);
}
