// rst-c's own (not vendored): the plan the Confirm dialog shows and the executor runs (spec #18,
// In Scope 2): one entry per add-in per year. It is plain data and round-trips through JSON, so the
// v3.1 tray service (feature #17) can be handed a plan the user confirmed in the Loader.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Activation;

/// <summary>Why the plan is being made.</summary>
public enum ActivationIntent
{
    /// <summary>Load Profile: with Disable on, only required and protected add-ins stay on.</summary>
    Load,

    /// <summary>Unload: everything rst-c switched off goes back on.</summary>
    Unload,

    /// <summary>Restore All Add-ins: everything rst-c switched off goes back on.</summary>
    RestoreAll,

    /// <summary>The one-time move of v3.0's renamed manifests into the settings file.</summary>
    MigrateLegacy,
}

public enum PlanAction
{
    Enable,
    Disable,
    NoChange,
    NotManaged,
}

/// <summary>Why an add-in is outside rst-c's switching.</summary>
public enum NotManagedReason
{
    /// <summary>RST itself.</summary>
    RstItself,

    /// <summary>Shipped inside Revit's install folder.</summary>
    RevitInstall,

    /// <summary>Published by Autodesk.</summary>
    Autodesk,

    /// <summary>The manifest has no ClientId, so the settings file cannot name it.</summary>
    NoAddinId,

    /// <summary>The user switched it off in Autodesk's Add-Ins Manager; rst-c never turns it back on.</summary>
    SwitchedOffByUser,

    /// <summary>IT locked it off (AdminSettings.json) and rst-c did not switch it off.</summary>
    LockedOffByAdmin,
}

/// <summary>Why a change could not be made (the report's "could not change" reasons).</summary>
public enum ChangeReason
{
    /// <summary>IT's AdminSettings.json locks the add-in.</summary>
    AdminLocked,

    /// <summary>Renaming a manifest installed for all users needs administrator rights (Revit 2025.0–2025.2).</summary>
    NeedsAdmin,

    /// <summary>Another program had the file open.</summary>
    InUse,

    /// <summary>The add-in is no longer installed.</summary>
    Vanished,

    /// <summary>Revit's add-in settings file could not be read or written.</summary>
    FileError,

    Other,
}

/// <summary>
/// One add-in in one year: what will happen (<see cref="Action"/>), the ClientIds the executor
/// will flip (<see cref="TargetIds"/>; settings-file years), and what is already known to block it
/// (<see cref="ExpectedBlock"/>: an admin lock, an unreadable settings file).
/// </summary>
public sealed record PlanEntry(
    int Year,
    string Name,
    string? VendorId,
    AddinScope Scope,
    SwitchMechanism Mechanism,
    string ManifestPath,
    string FileName,
    IReadOnlyList<string> TargetIds,
    PlanAction Action,
    NotManagedReason? NotManaged,
    bool Required,
    bool Renamed,
    bool Migrate,
    ChangeReason? ExpectedBlock)
{
    [JsonIgnore]
    public bool IsChange => Action is PlanAction.Enable or PlanAction.Disable;

    /// <summary>Renaming a machine-wide manifest (Revit 2025.0–2025.2) usually needs administrator rights.</summary>
    [JsonIgnore]
    public bool MayNeedAdmin => IsChange && Mechanism == SwitchMechanism.Rename && Scope == AddinScope.Machine;

    /// <summary>What identifies the operation in the report ("plan shown = operations executed").</summary>
    [JsonIgnore]
    public string OperationKey => Year.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + Action + "|" + ManifestPath.ToLowerInvariant();
}

/// <summary>A ledger entry to clear; <see cref="ResetSetting"/> also sets its settings entry back to Disabled:false.</summary>
public sealed record LedgerForget(LedgerKey Key, bool ResetSetting);

/// <summary>A note about a whole year (for example "Disable all Add-Ins" is on in Autodesk's manager).</summary>
public sealed record YearNote(int Year, string Text);

public sealed record ActivationPlan(
    ActivationIntent Intent,
    bool DisableUnused,
    IReadOnlyList<PlanEntry> Entries,
    IReadOnlyList<LedgerForget> Forget,
    IReadOnlyList<YearNote> Notes)
{
    public const int FormatVersion = 1;

    public static readonly ActivationPlan Nothing = new(ActivationIntent.Load, false, [], [], []);

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>The entries that change something: the operations the executor will run.</summary>
    [JsonIgnore]
    public IReadOnlyList<PlanEntry> Changes => Entries.Where(e => e.IsChange).ToList();

    [JsonIgnore]
    public bool HasChanges => Entries.Any(e => e.IsChange);

    /// <summary>The years the plan covers.</summary>
    [JsonIgnore]
    public IReadOnlyList<int> Years => Entries.Select(e => e.Year).Distinct().Order().ToList();

    public string ToJson() => JsonSerializer.Serialize(new Envelope(FormatVersion, this), Options);

    /// <exception cref="JsonException">Not a plan, or a plan from a newer rst-c.</exception>
    public static ActivationPlan FromJson(string json)
    {
        var envelope = JsonSerializer.Deserialize<Envelope>(json, Options) ?? throw new JsonException("empty plan");
        if (envelope.Version != FormatVersion) throw new JsonException("unsupported plan version " + envelope.Version);
        return envelope.Plan ?? throw new JsonException("no plan");
    }

    private sealed record Envelope(
        [property: JsonPropertyName("version")] int Version,
        [property: JsonPropertyName("plan")] ActivationPlan? Plan);
}
