// ActiveProfile.cs — pointer to the currently-applied profile.
//
// Lives at %AppData%\RST\active_profile.json. RstApplication.OnStartup
// reads this to decide what panels to build on the RST tab; the Loader
// window writes it on Apply, then prompts the user to restart Revit
// (Revit can't tear down ribbon panels mid-session).
//
// On-disk shape mirrors the pyRevit-era file so existing installs migrate
// without conversion. The "blank" form ({"profile": "BlankRST", "blank": true})
// signals "no profile active — show only the Loader button on RST tab".

using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Configuration;

namespace RST.Core.Profiles;

public sealed class ActiveProfile
{
    [JsonPropertyName("profile")]              public string ProfileName { get; set; } = "";
    [JsonPropertyName("profile_id")]           public string? ProfileId { get; set; }
    [JsonPropertyName("profile_file")]         public string? ProfileFile { get; set; }
    [JsonPropertyName("tab")]                  public string Tab { get; set; } = "";
    [JsonPropertyName("loaded_at")]            public string LoadedAt { get; set; } = "";
    [JsonPropertyName("hidden_tabs")]          public string[] HiddenTabs { get; set; } = Array.Empty<string>();
    [JsonPropertyName("disable_non_required")] public bool DisableNonRequired { get; set; }
    [JsonPropertyName("blank")]                public bool Blank { get; set; }

    /// <summary>True when no real profile is applied (file missing or "blank" stub).</summary>
    public bool IsBlank => Blank || string.IsNullOrEmpty(ProfileFile);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Read the active-profile pointer. Returns a blank instance when the
    /// file is missing or unreadable — never throws.
    /// </summary>
    public static ActiveProfile Read(string? path = null)
    {
        path ??= AppDataPaths.ActiveProfileFile;
        if (!File.Exists(path)) return MakeBlank();
        try
        {
            var text = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ActiveProfile>(text) ?? MakeBlank();
        }
        catch
        {
            return MakeBlank();
        }
    }

    public void Write(string? path = null)
    {
        path ??= AppDataPaths.ActiveProfileFile;
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
    }

    /// <summary>Write the "no profile active" stub (matches pyRevit BlankRST shape).</summary>
    public static void WriteBlank(string? path = null) => MakeBlank().Write(path);

    public static ActiveProfile FromProfile(Profile profile, string profileFile, string[]? hiddenTabs = null, bool disableNonRequired = false) =>
        new()
        {
            ProfileName = profile.ProfileName,
            ProfileId = profile.Id,
            ProfileFile = profileFile,
            Tab = profile.Tab,
            LoadedAt = DateTime.Now.ToString("o"),
            HiddenTabs = hiddenTabs ?? Array.Empty<string>(),
            DisableNonRequired = disableNonRequired,
            Blank = false,
        };

    private static ActiveProfile MakeBlank() => new ActiveProfile()
    {
        ProfileName = "BlankRST",
        Blank = true,
    };
}
