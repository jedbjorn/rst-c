// TelemetryPrefs.cs — consent + config for telemetry capture.
//
//   %AppData%\RST\telemetry_prefs.json   (Roaming — preferences follow
//                                         the user; the outbox does NOT)
//
// UserProfilePrefs-style single JSON file. `enabled` defaults true (on
// by default + first-run notice + toggle — spec Scope Decision 4);
// `noticeShownUtc` is null until the one-time TaskDialog has been shown;
// `retentionDays` feeds the pruner.
//
// Failure semantics (spec Threading & Safety: corrupt prefs degrade to
// telemetry OFF, and a disabled state must persist): a MISSING file is
// first-run → defaults (enabled); an EXISTING file that is corrupt,
// truncated, or unreadable fails CLOSED (enabled=false) — damaged state
// must never resurrect capture the user may have turned off. Writes are
// atomic (temp + move) so a crash mid-write can't tear the file, and
// never throw.

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

    /// <summary>
    /// Read the prefs file. Missing file → first-run defaults (enabled).
    /// Existing but corrupt/truncated/unreadable file → fail closed
    /// (enabled=false): damaged state must never turn capture back on.
    /// </summary>
    public static TelemetryPrefs Read(string? path = null)
    {
        path ??= DefaultPath;
        if (!File.Exists(path)) return new TelemetryPrefs();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<TelemetryPrefs>(text) ?? FailClosed();
        }
        catch
        {
            return FailClosed();
        }
    }

    /// <summary>
    /// Persist atomically (temp file + move, same directory) so a crash
    /// mid-write leaves the previous state, never a torn file. Returns
    /// false instead of throwing on IO failure — telemetry must never
    /// take Revit down.
    /// </summary>
    public bool Write(string? path = null, Action<string>? log = null)
    {
        path ??= DefaultPath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            try { log?.Invoke("telemetry prefs write failed: " + ex.Message); }
            catch { /* logging must never throw */ }
            return false;
        }
    }

    private static TelemetryPrefs FailClosed() => new() { Enabled = false };
}
