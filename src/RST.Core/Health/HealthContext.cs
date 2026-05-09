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

    public static HealthContext Empty { get; } = new();
}
