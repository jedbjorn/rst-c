// rst-c's own (not vendored): the RST tray's settings file (spec #20 §1, §4), %AppData%\RST\tray_settings.json.
//
//   { "theme": "system" | "light" | "dark",
//     "checkForUpdates": true,
//     "updateNoticeShownUtc": null,      (unread since v3.2, decision #65: the update-check notice is gone)
//     "shadowNoticeShownUtc": null }      (F17 WU5: "your own copy shadows the shared one", told once)
//
// Read semantics follow TelemetryPrefs: a truly missing file (or folder) is a first run and gives the
// defaults (checks on, as spec #20 §4 sets); a file that exists but cannot be read or parsed fails CLOSED
// for the network setting (checkForUpdates = false, so damaged state never turns network calls back on) and
// keeps the other defaults. A valid file without "checkForUpdates" (a hand-written or older file) takes the
// default. Writes go through TrayAtomicFile (temp file, flush, swap) under a sibling .lock file, so two trays
// of one user (two sessions) never tear or revert each other's change. Nothing here throws.

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace RST.Core.Tray;

/// <summary>The tray window's theme setting (Settings → Theme).</summary>
public enum TrayThemeChoice
{
    /// <summary>Follow the Windows app theme (AppsUseLightTheme).</summary>
    System,
    Light,
    Dark,
}

public sealed class TraySettings
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public TrayThemeChoice Theme { get; set; } = TrayThemeChoice.System;

    /// <summary>"Check for updates automatically" (default on). An IT preset can force it off: <see cref="UpdateChecksPolicy"/>.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>
    /// Unread since v3.2 (decision #65: the tray no longer shows its one-time update-check notice). It recorded when that
    /// notice was shown. Kept so older files still parse and a write keeps the value.
    /// </summary>
    public DateTimeOffset? UpdateNoticeShownUtc { get; set; }

    /// <summary>
    /// Set (UTC) once the tray has told this user that their own RST hides the one installed for everyone
    /// (spec #20 §7; F17 WU5). Null: not yet.
    /// </summary>
    public DateTimeOffset? ShadowNoticeShownUtc { get; set; }

    /// <summary>True when the file existed but could not be read or parsed (the defaults then fail closed).</summary>
    [JsonIgnore]
    public bool WasDamaged { get; private set; }

    /// <summary>Writer lock wait. Writes hold it for milliseconds; a timeout means a wedged peer. Internal for tests.</summary>
    internal static int LockTimeoutMs = 5000;

    /// <summary>Reads <paramref name="path"/> (default <see cref="TrayPaths.SettingsFile"/>). Never throws.</summary>
    public static TraySettings Read(string? path = null, Action<string>? log = null)
    {
        path ??= TrayPaths.SettingsFile;
        string text;
        try
        {
            text = TrayAtomicFile.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return new TraySettings();
        }
        catch (DirectoryNotFoundException)
        {
            return new TraySettings();
        }
        catch (Exception ex)
        {
            Log(log, "tray settings unreadable, update checks off until they are saved again: " + ex.Message);
            return FailClosed();
        }

        try
        {
            var dto = JsonSerializer.Deserialize<SettingsDto>(text);
            if (dto is null)
            {
                Log(log, "tray settings empty (null), update checks off until they are saved again");
                return FailClosed();
            }
            return new TraySettings
            {
                Theme = ParseTheme(dto.Theme),
                CheckForUpdates = dto.CheckForUpdates ?? true,
                UpdateNoticeShownUtc = dto.UpdateNoticeShownUtc,
                ShadowNoticeShownUtc = dto.ShadowNoticeShownUtc,
            };
        }
        catch (Exception ex)
        {
            Log(log, "tray settings damaged, update checks off until they are saved again: " + ex.Message);
            return FailClosed();
        }
    }

    /// <summary>
    /// Applies one change to the freshest on-disk settings and writes them, under the cross-process lock.
    /// Returns the settings as written, or null when nothing was written (logged).
    /// </summary>
    public static TraySettings? Update(Action<TraySettings> mutate, string? path = null, Action<string>? log = null)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        path ??= TrayPaths.SettingsFile;
        try
        {
            using var writerLock = AcquireLock(path, log);
            if (writerLock is null) return null;
            var settings = Read(path, log);
            mutate(settings);
            TrayAtomicFile.WriteAllText(path, settings.ToJson(), log);
            settings.WasDamaged = false;
            return settings;
        }
        catch (Exception ex)
        {
            Log(log, "tray settings not saved: " + ex.Message);
            return null;
        }
    }

    /// <summary>The file's JSON text.</summary>
    public string ToJson() => JsonSerializer.Serialize(new SettingsDto
    {
        Theme = Theme.ToString().ToLowerInvariant(),
        CheckForUpdates = CheckForUpdates,
        UpdateNoticeShownUtc = UpdateNoticeShownUtc,
        ShadowNoticeShownUtc = ShadowNoticeShownUtc,
    }, JsonOptions);

    private static TrayThemeChoice ParseTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => TrayThemeChoice.Light,
        "dark" => TrayThemeChoice.Dark,
        _ => TrayThemeChoice.System,
    };

    private static TraySettings FailClosed() => new() { CheckForUpdates = false, WasDamaged = true };

    private static FileStream? AcquireLock(string path, Action<string>? log)
    {
        var lockPath = path + ".lock";
        var dir = Path.GetDirectoryName(lockPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var deadline = Environment.TickCount64 + LockTimeoutMs;
        while (true)
        {
            try
            {
                return new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    Log(log, "tray settings lock wait timed out: " + lockPath);
                    return null;
                }
                Thread.Sleep(25);
            }
        }
    }

    private static void Log(Action<string>? log, string message)
    {
        try { log?.Invoke(message); }
        catch { /* logging must never throw */ }
    }

    /// <summary>Read and write shape: nullable so an absent field is detectable.</summary>
    private sealed class SettingsDto
    {
        [JsonPropertyName("theme")]
        public string? Theme { get; set; }

        [JsonPropertyName("checkForUpdates")]
        public bool? CheckForUpdates { get; set; }

        [JsonPropertyName("updateNoticeShownUtc")]
        public DateTimeOffset? UpdateNoticeShownUtc { get; set; }

        [JsonPropertyName("shadowNoticeShownUtc")]
        public DateTimeOffset? ShadowNoticeShownUtc { get; set; }
    }
}
