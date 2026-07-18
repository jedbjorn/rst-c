// TelemetryEvent.cs — the envelope every outbox line carries, plus the
// event-type / source / payload-field vocabulary shared by the collector
// (RST.Engine), the recovery scanner, and the aggregator.
//
// The wire shape is flat JSON: envelope fields + payload fields at the
// top level of one JSON Lines record. Payload fields ride in
// [JsonExtensionData] so the envelope stays strongly typed while each
// event_type contributes its own fields without a class per event.
//
// schema_version is mandatory from day one; bump on any breaking shape
// change. `synthetic` is only present (true) on recovery-written records.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Telemetry;

public static class TelemetrySchema
{
    public const int Version = 1;
}

public static class TelemetrySources
{
    public const string Addin = "addin";
    public const string Recovery = "recovery";
}

public static class TelemetryEventTypes
{
    public const string SessionStart = "session_start";
    public const string Heartbeat = "heartbeat";
    public const string DocOpening = "doc_opening";
    public const string DocOpened = "doc_opened";
    public const string DocClosing = "doc_closing";
    public const string DocClosed = "doc_closed";
    public const string DocSaved = "doc_saved";
    public const string DocSavedAs = "doc_saved_as";
    public const string SyncStart = "sync_start";
    public const string SyncEnd = "sync_end";
    public const string DocChangedPulse = "doc_changed_pulse";
    public const string ViewActivated = "view_activated";
    public const string SessionEnd = "session_end";
    public const string CollectionDisabled = "collection_disabled";
    public const string CollectionEnabled = "collection_enabled";
}

/// <summary>
/// Payload field names — one vocabulary so collector, recovery, and
/// aggregator never drift on spelling. Identity fields are captured raw;
/// linkage resolution is a server concern, never done on the client.
/// </summary>
public static class TelemetryFields
{
    // Model identity block.
    public const string CreationGuid = "creation_guid";
    public const string VersionGuid = "version_guid";
    public const string SaveCount = "save_count";
    public const string CloudProjectGuid = "cloud_project_guid";
    public const string CloudModelGuid = "cloud_model_guid";
    public const string CentralGuid = "central_guid";
    public const string CentralPath = "central_path";
    public const string LocalPath = "local_path";
    public const string Title = "title";
    public const string IsWorkshared = "is_workshared";
    public const string IsCloud = "is_cloud";
    public const string IsFamilyDoc = "is_family_doc";
    public const string IsDetached = "is_detached";

    // session_start.
    public const string MachineName = "machine_name";
    public const string InstallId = "install_id";
    public const string OsUser = "os_user";
    public const string AutodeskUser = "autodesk_user";
    public const string RevitVersion = "revit_version";
    public const string RevitBuild = "revit_build";
    public const string AddinVersion = "addin_version";

    // heartbeat.
    public const string OpenDocCount = "open_doc_count";

    // doc_closing / doc_closed correlation.
    public const string ClosingId = "closing_id";
    public const string Status = "status";

    // doc_saved_as.
    public const string PreviousLocalPath = "previous_local_path";

    // sync_start.
    public const string Comment = "comment";
}

public sealed class TelemetryEvent
{
    [JsonPropertyName("event_id")]
    public string EventId { get; set; } = "";

    [JsonPropertyName("session_guid")]
    public string SessionGuid { get; set; } = "";

    /// <summary>Per-session monotonic counter — total order within a
    /// session independent of clock changes.</summary>
    [JsonPropertyName("seq")]
    public long Seq { get; set; }

    /// <summary>Always UTC; serialized as ISO-8601 with milliseconds.</summary>
    [JsonPropertyName("ts")]
    public DateTimeOffset Ts { get; set; }

    [JsonPropertyName("event_type")]
    public string EventType { get; set; } = "";

    [JsonPropertyName("schema_version")]
    public int SchemaVersion { get; set; } = TelemetrySchema.Version;

    [JsonPropertyName("source")]
    public string Source { get; set; } = TelemetrySources.Addin;

    /// <summary>True on recovery-written records; omitted otherwise.</summary>
    [JsonPropertyName("synthetic")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Synthetic { get; set; }

    /// <summary>
    /// Event-type-specific payload, flattened to the top level of the
    /// JSON line. Values are raw CLR values when built in-process and
    /// <see cref="JsonElement"/> after a round-trip — read through
    /// <see cref="GetString"/> / <see cref="GetBool"/> / <see cref="GetInt64"/>.
    /// </summary>
    [JsonExtensionData]
    public Dictionary<string, object?>? Payload { get; set; }

    public void SetField(string name, object? value)
    {
        Payload ??= new Dictionary<string, object?>();
        Payload[name] = value;
    }

    public string? GetString(string name)
    {
        var v = GetRaw(name);
        return v switch
        {
            null => null,
            string s => s,
            JsonElement { ValueKind: JsonValueKind.String } je => je.GetString(),
            JsonElement { ValueKind: JsonValueKind.Null } => null,
            JsonElement je => je.ToString(),
            _ => v.ToString(),
        };
    }

    public bool? GetBool(string name)
    {
        var v = GetRaw(name);
        return v switch
        {
            null => null,
            bool b => b,
            JsonElement { ValueKind: JsonValueKind.True } => true,
            JsonElement { ValueKind: JsonValueKind.False } => false,
            _ => null,
        };
    }

    public long? GetInt64(string name)
    {
        var v = GetRaw(name);
        return v switch
        {
            null => null,
            long l => l,
            int i => i,
            JsonElement { ValueKind: JsonValueKind.Number } je when je.TryGetInt64(out var l) => l,
            _ => null,
        };
    }

    private object? GetRaw(string name) =>
        Payload is not null && Payload.TryGetValue(name, out var v) ? v : null;
}
