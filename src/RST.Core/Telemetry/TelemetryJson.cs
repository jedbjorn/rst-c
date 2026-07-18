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
    /// otherwise unparseable input — readers skip and move on.
    /// </summary>
    public static TelemetryEvent? TryParseLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var e = JsonSerializer.Deserialize<TelemetryEvent>(line, Options);
            if (e is null || string.IsNullOrEmpty(e.EventType)) return null;
            return e;
        }
        catch
        {
            return null;
        }
    }

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
