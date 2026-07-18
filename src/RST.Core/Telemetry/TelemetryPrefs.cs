// TelemetryPrefs.cs — consent + config for telemetry capture.
//
//   %AppData%\RST\telemetry_prefs.json   (Roaming — preferences follow
//                                         the user; the outbox does NOT)
//
// UserProfilePrefs-style single JSON file. `enabled` defaults true (on
// by default + first-run notice + toggle — spec Scope Decision 4);
// `noticeShownUtc` is null until the one-time TaskDialog has been shown;
// `retentionDays` feeds the pruner. Corrupt or missing file → defaults,
// never an exception.

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Configuration;

namespace RST.Core.Telemetry;

public sealed class TelemetryPrefs
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;

    /// <summary>Set (UTC) once the first-run notice has been shown,
    /// regardless of how it was dismissed. Null = not yet shown.</summary>
    [JsonPropertyName("noticeShownUtc")]
    public DateTimeOffset? NoticeShownUtc { get; set; }

    [JsonPropertyName("retentionDays")]
    public int RetentionDays { get; set; } = RetentionPruner.DefaultRetentionDays;

    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "telemetry_prefs.json");

    /// <summary>Read the prefs file. Returns defaults when missing or unreadable.</summary>
    public static TelemetryPrefs Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new TelemetryPrefs();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TelemetryPrefs>(text) ?? new TelemetryPrefs();
        }
        catch
        {
            return new TelemetryPrefs();
        }
    }

    public void Write(string? path = null)
    {
        path ??= DefaultPath;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }
}
