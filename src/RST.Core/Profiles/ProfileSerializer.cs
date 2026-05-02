// ProfileSerializer.cs — JSON read/write + schema migration.
//
// Public surface:
//   ProfileSerializer.Read(stream)         → Profile          (migrates v0 → current on load)
//   ProfileSerializer.Write(profile, stream)
//   ProfileSerializer.WriteString(profile) → string           (pretty-printed)
//   ProfileSerializer.Validate(profile)    → IList<string>    (path-style locators)

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace RST.Core.Profiles;

public static class ProfileSerializer
{
    /// <summary>Latest schema version this codebase emits.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Read a profile from <paramref name="stream"/> and migrate to the current
    /// schema. Throws <see cref="ProfileLoadException"/> on parse or validation
    /// failure.
    /// </summary>
    public static Profile Read(Stream stream)
    {
        if (stream is null) throw new ArgumentNullException(nameof(stream));

        Profile? profile;
        try
        {
            profile = JsonSerializer.Deserialize<Profile>(stream, ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new ProfileLoadException("Invalid JSON: " + ex.Message, ex);
        }

        if (profile is null)
            throw new ProfileLoadException("Profile JSON deserialized to null.");

        Migrate(profile);

        var errors = Validate(profile);
        if (errors.Count > 0)
            throw new ProfileLoadException(
                "Profile validation failed:\n  " + string.Join("\n  ", errors));

        return profile;
    }

    public static Profile ReadString(string json)
    {
        using var ms = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        return Read(ms);
    }

    public static void Write(Profile profile, Stream stream)
    {
        if (profile is null) throw new ArgumentNullException(nameof(profile));
        if (stream  is null) throw new ArgumentNullException(nameof(stream));

        // Force-stamp current schema version on write — any in-memory mutation
        // since load is now expressed in the current shape.
        profile.SchemaVersion = CurrentSchemaVersion;

        JsonSerializer.Serialize(stream, profile, WriteOptions);
    }

    public static string WriteString(Profile profile)
    {
        using var ms = new MemoryStream();
        Write(profile, ms);
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

    /// <summary>
    /// Validate a profile post-migration. Returns a list of human-readable error
    /// locators; empty when valid. Does NOT throw.
    /// </summary>
    public static IList<string> Validate(Profile profile)
    {
        var errors = new List<string>();
        if (profile is null) { errors.Add("profile: null"); return errors; }

        if (string.IsNullOrWhiteSpace(profile.ProfileName))
            errors.Add("profile.profile: required, non-empty");
        if (string.IsNullOrWhiteSpace(profile.Tab))
            errors.Add("profile.tab: required, non-empty");

        for (int i = 0; i < profile.Panels.Count; i++)
        {
            var panel = profile.Panels[i];
            var panelLoc = $"profile.panels[{i}]";

            if (string.IsNullOrWhiteSpace(panel.Name))
                errors.Add($"{panelLoc}.name: required");
            if (!IsValidHexColor(panel.Color))
                errors.Add($"{panelLoc}.color: invalid hex \"{panel.Color}\"");

            for (int j = 0; j < panel.Slots.Count; j++)
            {
                var slot = panel.Slots[j];
                var slotLoc = $"{panelLoc}.slots[{j}]";

                if (slot.SlotType is not "tool" and not "stack")
                    errors.Add($"{slotLoc}.type: must be 'tool' or 'stack' (got '{slot.SlotType}')");

                if (slot.SlotType == "stack" && !profile.Stacks.ContainsKey(slot.Name))
                    errors.Add($"{slotLoc}.name: stack '{slot.Name}' not found in profile.stacks");

                if (slot.SlotType == "tool" && string.IsNullOrEmpty(slot.CommandId))
                    errors.Add($"{slotLoc}.commandId: required for type=tool");
            }
        }

        foreach (var kvp in profile.Stacks)
        {
            var loc = $"profile.stacks[\"{kvp.Key}\"]";
            var n = kvp.Value.Tools.Count;
            if (n is < 2 or > 3)
                errors.Add($"{loc}.tools: must contain 2-3 tools (got {n})");
        }

        if (profile.PanelOpacity is < 10 or > 100)
            errors.Add($"profile.panelOpacity: must be 10-100 (got {profile.PanelOpacity})");

        return errors;
    }

    /// <summary>
    /// Apply schema upgrades in version order. Each upgrade is idempotent —
    /// safe to invoke against an already-current profile.
    /// </summary>
    internal static void Migrate(Profile profile)
    {
        // v0 → v1: pre-port pyRevit profiles. No field-level breaking changes;
        // we just stamp the version and let validation catch any odd shapes.
        if (profile.SchemaVersion < 1)
        {
            profile.SchemaVersion = 1;
        }

        // Future migrations slot in here, ordered.
    }

    private static bool IsValidHexColor(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        if (s[0] != '#') return false;
        if (s.Length is not 4 and not 7 and not 9) return false;
        for (int i = 1; i < s.Length; i++)
        {
            var c = s[i];
            var ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');
            if (!ok) return false;
        }
        return true;
    }
}

/// <summary>Thrown when a profile cannot be parsed or fails validation.</summary>
public sealed class ProfileLoadException : Exception
{
    public ProfileLoadException(string message) : base(message) { }
    public ProfileLoadException(string message, Exception inner) : base(message, inner) { }
}
