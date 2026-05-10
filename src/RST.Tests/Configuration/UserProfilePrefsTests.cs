// UserProfilePrefsTests.cs — round-trip + upsert behavior for the
// per-profile RSTify / disable-unused user prefs file (RST-026).

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using RST.Core.Configuration;
using Xunit;

namespace RST.Tests.Configuration;

public sealed class UserProfilePrefsTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public UserProfilePrefsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-PrefsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "user_profile_prefs.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Read_missing_file_returns_empty_instance()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Profiles.Should().BeEmpty();
    }

    [Fact]
    public void Read_corrupt_file_returns_empty_instance()
    {
        File.WriteAllText(_path, "{ not json");
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Profiles.Should().BeEmpty();
    }

    [Fact]
    public void Set_creates_entry_and_round_trips()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Set("abc", new[] { "Architecture", "Annotate" }, disableUnusedAddins: true,
                  presetsAdopted: true, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        reloaded.Profiles.Should().ContainKey("abc");
        var e = reloaded.Profiles["abc"];
        e.HiddenTabs.Should().BeEquivalentTo(new[] { "Architecture", "Annotate" });
        e.DisableUnusedAddins.Should().BeTrue();
        e.PresetsAdopted.Should().BeTrue();
    }

    [Fact]
    public void Set_overwrites_existing_entry()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Set("abc", new[] { "X" }, false, presetsAdopted: true, path: _path);
        prefs.Set("abc", new[] { "Y", "Z" }, true, presetsAdopted: true, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        reloaded.Profiles["abc"].HiddenTabs.Should().BeEquivalentTo(new[] { "Y", "Z" });
        reloaded.Profiles["abc"].DisableUnusedAddins.Should().BeTrue();
    }

    [Fact]
    public void Set_with_null_presetsAdopted_preserves_existing_flag()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Set("abc", new[] { "X" }, false, presetsAdopted: true, path: _path);

        // Subsequent Set without an explicit presetsAdopted (e.g. a Load
        // Profile call) must not flip the flag back to false.
        prefs.Set("abc", new[] { "X", "Y" }, false, presetsAdopted: null, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        reloaded.Profiles["abc"].PresetsAdopted.Should().BeTrue();
        reloaded.Profiles["abc"].HiddenTabs.Should().BeEquivalentTo(new[] { "X", "Y" });
    }

    [Fact]
    public void Multiple_profiles_coexist()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Set("a", new[] { "Tab1" }, true,  presetsAdopted: true, path: _path);
        prefs.Set("b", new[] { "Tab2" }, false, presetsAdopted: false, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        reloaded.Profiles.Should().HaveCount(2);
        reloaded.Profiles["a"].DisableUnusedAddins.Should().BeTrue();
        reloaded.Profiles["b"].DisableUnusedAddins.Should().BeFalse();
        reloaded.Profiles["b"].PresetsAdopted.Should().BeFalse();
    }

    [Fact]
    public void Get_returns_null_for_unknown_id()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Get("does-not-exist").Should().BeNull();
        prefs.Get("").Should().BeNull();
    }

    [Fact]
    public void Set_with_empty_id_throws()
    {
        var prefs = UserProfilePrefs.Read(_path);
        var act = () => prefs.Set("", new List<string>(), false, path: _path);
        act.Should().Throw<ArgumentException>();
    }

    // RST-046: panel-opacity override
    [Fact]
    public void SetPanelOpacityOverride_creates_entry_and_round_trips()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.SetPanelOpacityOverride("abc", 50, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        reloaded.Profiles.Should().ContainKey("abc");
        reloaded.Profiles["abc"].PanelOpacityOverride.Should().Be(50);
    }

    [Fact]
    public void SetPanelOpacityOverride_null_clears_override()
    {
        var prefs = UserProfilePrefs.Read(_path);
        prefs.SetPanelOpacityOverride("abc", 50, path: _path);
        prefs.SetPanelOpacityOverride("abc", null, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        reloaded.Profiles["abc"].PanelOpacityOverride.Should().BeNull();
    }

    [Fact]
    public void SetPanelOpacityOverride_with_empty_id_throws()
    {
        var prefs = UserProfilePrefs.Read(_path);
        var act = () => prefs.SetPanelOpacityOverride("", 50, path: _path);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void SetPanelOpacityOverride_does_not_disturb_RSTify_state()
    {
        // Slider write path must leave hiddenTabs / disableUnusedAddins
        // / presetsAdopted untouched on the same entry.
        var prefs = UserProfilePrefs.Read(_path);
        prefs.Set("abc", new[] { "Architecture" }, disableUnusedAddins: true,
                  presetsAdopted: true, path: _path);
        prefs.SetPanelOpacityOverride("abc", 60, path: _path);

        var reloaded = UserProfilePrefs.Read(_path);
        var e = reloaded.Profiles["abc"];
        e.HiddenTabs.Should().BeEquivalentTo(new[] { "Architecture" });
        e.DisableUnusedAddins.Should().BeTrue();
        e.PresetsAdopted.Should().BeTrue();
        e.PanelOpacityOverride.Should().Be(60);
    }
}
