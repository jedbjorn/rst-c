// rst-c's own (not vendored): the profile the RST tray last saw loaded (spec #38, Launch with Profile default pick).
// One small per-user file beside active_profile.json under %AppData%\RST, so it survives tray restarts and RST
// updates. It holds the profile's name and id, looked up the way the Loader does (id first, then name). A clean
// launch blanks active_profile.json but never this file.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace RST.Core.Tray;

/// <summary>The last loaded profile: its name and, when it has one, its id.</summary>
public sealed record LastLoadedProfile(
    [property: JsonPropertyName("profile")] string Name,
    [property: JsonPropertyName("profile_id")] string? Id)
{
    public const string FileName = "tray_last_profile.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The remembered profile; null when the file is missing, unreadable or names no profile. Never throws.</summary>
    public static LastLoadedProfile? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var last = JsonSerializer.Deserialize<LastLoadedProfile>(File.ReadAllText(path));
            return string.IsNullOrEmpty(last?.Name) ? null : last;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Writes the file atomically (a temp file moved into place). Throws when it cannot be written.</summary>
    public void Write(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        var tmp = path + ".rsttmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tmp, path, overwrite: true);
    }
}
