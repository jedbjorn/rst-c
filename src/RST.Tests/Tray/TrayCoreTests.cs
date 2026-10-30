// rst-c's own: the RST tray's Core rules (spec #20 §1, §4; F17 WU1): the settings file (defaults, fail-closed
// damaged reads, merged atomic writes), theme resolution, the UpdateChecks preset, the first-run notice rule,
// the command line, and the names and log glob the tray shares with the installer and the engine.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Theming;
using RST.Core.Tray;
using Xunit;

namespace RST.Tests.Tray;

public sealed class TraySettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-TraySettings-" + Guid.NewGuid().ToString("N"));
    private string PathIn => Path.Combine(_root, "tray_settings.json");

    public TraySettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Missing_file_or_folder_gives_the_defaults_with_checks_on()
    {
        foreach (var path in new[] { PathIn, Path.Combine(_root, "no", "such", "tray_settings.json") })
        {
            var s = TraySettings.Read(path);
            s.Theme.Should().Be(TrayThemeChoice.System);
            s.CheckForUpdates.Should().BeTrue("spec #20 §4: the setting defaults on");
            s.UpdateNoticeShownUtc.Should().BeNull();
            s.WasDamaged.Should().BeFalse();
        }
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[1,2]")]
    [InlineData("{\"checkForUpdates\": \"yes\"}")]
    public void Damaged_file_fails_closed_for_update_checks(string text)
    {
        File.WriteAllText(PathIn, text);
        string? logged = null;
        var s = TraySettings.Read(PathIn, m => logged = m);
        s.CheckForUpdates.Should().BeFalse("damaged state must never turn network calls on");
        s.WasDamaged.Should().BeTrue();
        s.Theme.Should().Be(TrayThemeChoice.System);
        logged.Should().NotBeNull();
    }

    [Fact]
    public void Valid_file_without_the_field_takes_the_default_and_unknown_theme_is_system()
    {
        File.WriteAllText(PathIn, "{\"theme\": \"purple\"}");
        var s = TraySettings.Read(PathIn);
        s.CheckForUpdates.Should().BeTrue();
        s.Theme.Should().Be(TrayThemeChoice.System);
        s.WasDamaged.Should().BeFalse();
    }

    [Fact]
    public void Update_round_trips_and_merges_into_the_freshest_file()
    {
        var shown = new DateTimeOffset(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);
        TraySettings.Update(s => s.Theme = TrayThemeChoice.Dark, PathIn).Should().NotBeNull();
        TraySettings.Update(s => s.CheckForUpdates = false, PathIn).Should().NotBeNull();
        TrayUpdateNotice.MarkShown(shown, PathIn).Should().BeTrue();

        var s = TraySettings.Read(PathIn);
        s.Theme.Should().Be(TrayThemeChoice.Dark, "a later update must not revert an earlier one");
        s.CheckForUpdates.Should().BeFalse();
        s.UpdateNoticeShownUtc.Should().Be(shown);

        var json = File.ReadAllText(PathIn);
        json.Should().Contain("\"theme\": \"dark\"").And.Contain("\"checkForUpdates\": false");
        Directory.GetFiles(_root, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty("the atomic write leaves no temp file");
    }

    [Fact]
    public void Update_over_a_damaged_file_writes_a_clean_one()
    {
        File.WriteAllText(PathIn, "garbage");
        var written = TraySettings.Update(s => s.Theme = TrayThemeChoice.Light, PathIn);
        written.Should().NotBeNull();
        written!.WasDamaged.Should().BeFalse();
        var s = TraySettings.Read(PathIn);
        s.WasDamaged.Should().BeFalse();
        s.Theme.Should().Be(TrayThemeChoice.Light);
        s.CheckForUpdates.Should().BeFalse("the fail-closed value is what gets saved");
    }

    [Fact]
    public void Update_returns_null_when_the_lock_is_held()
    {
        var saved = TraySettings.LockTimeoutMs;
        TraySettings.LockTimeoutMs = 100;
        try
        {
            using var held = new FileStream(PathIn + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            string? logged = null;
            TraySettings.Update(s => s.Theme = TrayThemeChoice.Dark, PathIn, m => logged = m).Should().BeNull();
            logged.Should().Contain("lock");
            File.Exists(PathIn).Should().BeFalse();
        }
        finally
        {
            TraySettings.LockTimeoutMs = saved;
        }
    }
}

public sealed class TrayRulesTests
{
    [Theory]
    [InlineData(TrayThemeChoice.System, true, false, UiTheme.Light)]
    [InlineData(TrayThemeChoice.System, false, false, UiTheme.Dark)]
    [InlineData(TrayThemeChoice.System, null, false, UiTheme.Light)]
    [InlineData(TrayThemeChoice.Light, false, false, UiTheme.Light)]
    [InlineData(TrayThemeChoice.Dark, true, false, UiTheme.Dark)]
    [InlineData(TrayThemeChoice.Dark, false, true, UiTheme.Light)]
    [InlineData(TrayThemeChoice.System, false, true, UiTheme.Light)]
    public void Theme_follows_the_setting_then_windows_and_high_contrast_is_light(
        TrayThemeChoice choice, bool? appsLight, bool highContrast, UiTheme expected) =>
        TrayTheme.Resolve(choice, appsLight, highContrast).Should().Be(expected);

    [Fact]
    public void UpdateChecks_zero_in_either_hive_forces_checks_off_and_locks_them()
    {
        UpdateChecksPolicy.Resolve(true, new int?[] { null, null }).Should().Be(new UpdateChecksState(true, false));
        UpdateChecksPolicy.Resolve(false, new int?[] { null, null }).Should().Be(new UpdateChecksState(false, false));
        UpdateChecksPolicy.Resolve(true, new int?[] { 0, null }).Should().Be(new UpdateChecksState(false, true));
        UpdateChecksPolicy.Resolve(true, new int?[] { null, 0 }).Should().Be(new UpdateChecksState(false, true));
        UpdateChecksPolicy.Resolve(true, new int?[] { 1, null }).Should().Be(new UpdateChecksState(true, false),
            "only 0 is defined; another value leaves the user in charge");
    }

    [Fact]
    public void Notice_shows_only_when_checks_are_on_and_it_was_never_shown()
    {
        var fresh = new TraySettings();
        TrayUpdateNotice.ShouldShow(fresh, new UpdateChecksState(true, false)).Should().BeTrue();
        TrayUpdateNotice.ShouldShow(fresh, new UpdateChecksState(false, false)).Should().BeFalse();
        TrayUpdateNotice.ShouldShow(fresh, new UpdateChecksState(false, true)).Should().BeFalse("a preset-off tray never announces checks");
        var shown = new TraySettings { UpdateNoticeShownUtc = DateTimeOffset.UnixEpoch };
        TrayUpdateNotice.ShouldShow(shown, new UpdateChecksState(true, false)).Should().BeFalse();
    }

    [Fact]
    public void Icon_retry_backs_off_for_about_two_minutes_then_stops()
    {
        TrayIconRetry.NextDelay(0).Should().BeNull("there is nothing to retry before a failure");
        var delays = Enumerable.Range(1, 10).Select(TrayIconRetry.NextDelay).TakeWhile(d => d is not null).Select(d => d!.Value).ToList();
        delays.Should().HaveCount(TrayIconRetry.Delays.Count).And.HaveCountGreaterThanOrEqualTo(3);
        delays.Should().BeInAscendingOrder();
        delays[0].Should().BeGreaterThanOrEqualTo(TimeSpan.FromSeconds(1));
        var total = delays.Aggregate(TimeSpan.Zero, (a, b) => a + b);
        total.Should().BeGreaterThan(TimeSpan.FromSeconds(90)).And.BeLessThanOrEqualTo(TimeSpan.FromMinutes(2.5));
        TrayIconRetry.NextDelay(TrayIconRetry.Delays.Count + 1).Should().BeNull("then only TaskbarCreated re-adds the icon");
    }

    [Fact]
    public void Arguments_startup_is_case_insensitive_and_unknowns_are_kept_for_the_log()
    {
        TrayArguments.Parse(Array.Empty<string>()).StartHidden.Should().BeFalse();
        TrayArguments.Parse(new[] { "--startup" }).StartHidden.Should().BeTrue();
        TrayArguments.Parse(new[] { "--STARTUP" }).StartHidden.Should().BeTrue();
        var parsed = TrayArguments.Parse(new[] { "--bogus", "--startup", " " });
        parsed.StartHidden.Should().BeTrue();
        parsed.Unknown.Should().Equal("--bogus");
    }
}

public sealed class TrayPathsTests : IDisposable
{
    private readonly string _logs = Path.Combine(Path.GetTempPath(), "RST-TrayLogs-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_logs, recursive: true); }
        catch { /* best effort */ }
    }

    [Fact]
    public void Names_are_keyed_by_the_user_sid_in_the_session_namespace()
    {
        const string sid = "S-1-5-21-1004336348-1177238915-682003330-1001";
        TrayPaths.MutexName(sid).Should().Be(@"Local\RST.Tray." + sid);
        TrayPaths.ShowEventName(sid).Should().Be(@"Local\RST.Tray.Show." + sid);
        FluentActions.Invoking(() => TrayPaths.MutexName(@"S-1\..\Global")).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => TrayPaths.MutexName(" ")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Settings_and_logs_live_under_the_roaming_rst_root()
    {
        TrayPaths.SettingsFile.Should().Be(Path.Combine(AppDataPaths.Root, "tray_settings.json"));
        TrayPaths.LogFileBase.Should().Be(Path.Combine(AppDataPaths.Root, "logs", "tray_.log"));
        TrayPaths.LogFilesKept.Should().Be(10);
        TrayPaths.CacheDir.Should().EndWith(Path.Combine("RST", "Tray", "cache"));
    }

    [Fact]
    public void Engine_prune_glob_never_matches_a_tray_log_and_the_tray_glob_never_matches_an_engine_log()
    {
        Directory.CreateDirectory(_logs);
        string[] tray = { "tray_20261030.log", "tray_20261030_001.log" };
        string[] engine = { "rst_2026-10-30_101500.log", "rst_2026-10-30_101500_boot.log" };
        foreach (var f in tray.Concat(engine)) File.WriteAllText(Path.Combine(_logs, f), "x");

        Directory.GetFiles(_logs, AppDataPaths.EngineSessionLogGlob).Select(Path.GetFileName)
            .Should().BeEquivalentTo(engine, "the engine's boot-time prune deletes only its own files");
        Directory.GetFiles(_logs, TrayPaths.LogFileGlob).Select(Path.GetFileName)
            .Should().BeEquivalentTo(tray);
        // The file sink's daily roll names the files after the base name's stem ("tray_") plus the date.
        Path.GetFileNameWithoutExtension(TrayPaths.LogFileBaseName).Should().Be("tray_");
    }
}
