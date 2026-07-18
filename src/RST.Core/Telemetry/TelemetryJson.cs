// TelemetryJson.cs — one JSON Lines record per event: compact JSON, a
// fixed UTC ISO-8601 timestamp shape, and a tolerant line parser.
//
// Readers (recovery scanner, retention pruner, aggregator, future
// shipper) all parse through TryParseLine: any unparseable line — the
// partial trailing line a crash leaves behind, or garbage from a corrupt
// disk — is skipped, never thrown.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Telemetry;

public static class TelemetryJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new UtcMillisecondConverter() },
    };

    /// <summary>Serialize one event to a single JSON line (no trailing newline).</summary>
    public static string SerializeLine(TelemetryEvent e) =>
        JsonSerializer.Serialize(e, Options);

    /// <summary>
    /// Parse one outbox line. Returns null for blank, truncated, or
    /// otherwise unparseable input — readers skip and move on. A line
    /// only counts as an event when the full mandatory envelope is
    /// PRESENT and VALID: event_id + session_guid are GUIDs, seq ≥ 1,
    /// ts parses, event_type is nonblank, schema_version == 1, source is
    /// addin|recovery. Presence is checked on the raw JSON — missing
    /// schema_version/source would otherwise be silently filled by
    /// TelemetryEvent's property initializers. Recovery and prune decide
    /// "closed" from parsed events, so a structurally invalid
    /// session_end must never masquerade as a close.
    /// </summary>
    public static TelemetryEvent? TryParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            using (var doc = JsonDocument.Parse(line))
            {
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !HasGuid(root, "event_id") ||
                    !HasGuid(root, "session_guid") ||
                    !root.TryGetProperty("seq", out var seq) ||
                    seq.ValueKind != JsonValueKind.Number ||
                    !seq.TryGetInt64(out var seqValue) || seqValue < 1 ||
                    !root.TryGetProperty("ts", out var ts) ||
                    ts.ValueKind != JsonValueKind.String ||
                    !root.TryGetProperty("event_type", out var eventType) ||
                    eventType.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(eventType.GetString()) ||
                    !root.TryGetProperty("schema_version", out var schemaVersion) ||
                    schemaVersion.ValueKind != JsonValueKind.Number ||
                    !schemaVersion.TryGetInt32(out var schemaValue) ||
                    schemaValue != TelemetrySchema.Version ||
                    !root.TryGetProperty("source", out var source) ||
                    source.ValueKind != JsonValueKind.String ||
                    source.GetString() is not (TelemetrySources.Addin or TelemetrySources.Recovery))
                    return null;
            }

            // An unparseable ts throws in the converter → caught → null.
            var e = JsonSerializer.Deserialize<TelemetryEvent>(line, Options);
            return e is not null && e.Ts != default ? e : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool HasGuid(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) &&
        v.ValueKind == JsonValueKind.String &&
        Guid.TryParse(v.GetString(), out _);

    /// <summary>
    /// "2026-07-18T14:03:07.123Z" — always UTC, always milliseconds. The
    /// dashboard converts to local for display; the wire stays UTC.
    /// </summary>
    private sealed class UtcMillisecondConverter : JsonConverter<DateTimeOffset>
    {
        private const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

        public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            DateTimeOffset.Parse(
                reader.GetString() ?? "",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

        public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
    }
}
