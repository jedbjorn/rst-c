// HealthContext.cs — Revit-side context captured by HealthCommand and
// handed to the in-process scanner. Replaces the
// data/health_scan_context.json handoff used by the upstream Python
// tool (where IronPython wrote JSON for a CPython subprocess).

namespace RST.Core.Health;

public sealed class HealthContext
{
    public string  RevitVersion  { get; init; } = "";
    public string  RevitBuild    { get; init; } = "";
    public string  RevitUsername { get; init; } = "";
    public string  ModelName     { get; init; } = "";
    public string  ModelPath     { get; init; } = "";
    public double? ModelSizeMb   { get; init; }
    public int?    WarningsCount { get; init; }

    // Active doc identity keys for the Activity tab's current-file
    // matching (telemetry spec: cloud GUID pair → central GUID →
    // central path → creation GUID; the path covers file-share centrals,
    // where WorksharingCentralGUID does not exist — SC-032). Null = not
    // captured / not applicable — an identity gap is data, never an error.
    public string? CreationGuid     { get; init; }
    public string? CloudProjectGuid { get; init; }
    public string? CloudModelGuid   { get; init; }
    public string? CentralGuid      { get; init; }
    public string? CentralPath      { get; init; }
    public bool?   IsWorkshared     { get; init; }
    public bool?   IsCloud          { get; init; }

    // Live telemetry session, when the collector is running: the
    // Activity read extends this session's file to now, which gives
    // Activity (Session) (spec #24). Null when the collector is not
    // running — Activity (Session) then reads "—".
    public string? TelemetrySessionGuid { get; init; }

    public static HealthContext Empty { get; } = new();
}
