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
// telemetry OFF, and a disabled state must persist): only a TRUE
// not-found (file or its directory absent) is first-run → defaults
// (enabled). Anything else — unreadable, corrupt, truncated, or JSON
// without an explicit `enabled` — fails CLOSED (enabled=false): damaged
// or inaccessible state must never resurrect capture the user may have
// turned off. File.Exists is NOT the first-run signal (it returns false
// on permission/IO errors too); absence is detected from the open's
// exception instead. Writes are atomic (temp + move) so a crash
// mid-write can't tear the file, and never throw.

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
    /// Read the prefs file. Truly missing file → first-run defaults
    /// (enabled). Existing but corrupt/truncated/unreadable — or valid
    /// JSON missing the `enabled` field — → fail closed (enabled=false):
    /// damaged state must never turn capture back on.
    /// </summary>
    public static TelemetryPrefs Read(string? path = null)
    {
        path ??= DefaultPath;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new TelemetryPrefs(); // true first run
        }
        catch (DirectoryNotFoundException)
        {
            return new TelemetryPrefs(); // true first run — dir not created yet
        }
        catch
        {
            // Access denied, sharing violation, IO error: the file may
            // exist with enabled=false in it — fail closed.
            return FailClosed();
        }

        try
        {
            var dto = JsonSerializer.Deserialize<PrefsDto>(text);
            if (dto?.Enabled is not bool enabled) return FailClosed();
            return new TelemetryPrefs
            {
                Enabled = enabled,
                NoticeShownUtc = dto.NoticeShownUtc,
                RetentionDays = dto.RetentionDays ?? RetentionPruner.DefaultRetentionDays,
            };
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

    /// <summary>Read-side shape: `enabled` nullable so its ABSENCE is
    /// detectable — {} deserialized straight into TelemetryPrefs would
    /// silently take the enabled=true initializer.</summary>
    private sealed class PrefsDto
    {
        [JsonPropertyName("enabled")]
        public bool? Enabled { get; set; }

        [JsonPropertyName("noticeShownUtc")]
        public DateTimeOffset? NoticeShownUtc { get; set; }

        [JsonPropertyName("retentionDays")]
        public int? RetentionDays { get; set; }
    }
}
