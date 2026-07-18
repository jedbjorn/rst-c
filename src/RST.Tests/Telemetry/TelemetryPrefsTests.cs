// TelemetryPrefsTests.cs — defaults, fail-closed damaged-state reads,
// atomic never-throw writes, and round-trip for the consent/config prefs
// file (Telemetry v1 spec, Build Plan step 1 + Threading & Safety).

using System;
using System.IO;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class TelemetryPrefsTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public TelemetryPrefsTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-TelPrefsTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "telemetry_prefs.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void Defaults_are_enabled_no_notice_180_days()
    {
        var prefs = TelemetryPrefs.Read(_path);
        prefs.Enabled.Should().BeTrue("on by default is Scope Decision 4");
        prefs.NoticeShownUtc.Should().BeNull();
        prefs.RetentionDays.Should().Be(180);
    }

    [Theory]
    [InlineData("{ not json")]                 // corrupt
    [InlineData("{\"enabled\": fal")]          // truncated mid-write
    [InlineData("")]                           // zero-byte (crash during create)
    [InlineData("null")]                       // parseable but not an object
    [InlineData("[]")]                         // parseable but wrong shape
    public void Damaged_existing_file_fails_closed(string content)
    {
        File.WriteAllText(_path, content);
        TelemetryPrefs.Read(_path).Enabled.Should().BeFalse(
            "damaged prefs degrade to telemetry OFF — a torn disable-write must never re-enable capture");
    }

    [Fact]
    public void Write_is_atomic_and_leaves_no_temp_files()
    {
        new TelemetryPrefs { Enabled = false }.Write(_path).Should().BeTrue();
        new TelemetryPrefs { Enabled = false, RetentionDays = 90 }.Write(_path).Should().BeTrue(
            "overwriting an existing prefs file must work");

        TelemetryPrefs.Read(_path).RetentionDays.Should().Be(90);
        // The temp file from the atomic write must not linger.
        Directory.GetFiles(_root).Should().Equal(_path);
    }

    [Fact]
    public void Write_failure_returns_false_and_never_throws()
    {
        // A directory component that is actually a FILE makes
        // CreateDirectory fail on every platform.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var unwritable = Path.Combine(blocker, "telemetry_prefs.json");

        string? logged = null;
        new TelemetryPrefs().Write(unwritable, m => logged = m).Should().BeFalse();
        logged.Should().Contain("prefs write failed");
    }

    [Fact]
    public void Round_trips_all_fields()
    {
        var shown = new DateTimeOffset(2026, 7, 18, 8, 0, 0, TimeSpan.Zero);
        new TelemetryPrefs
        {
            Enabled = false,
            NoticeShownUtc = shown,
            RetentionDays = 90,
        }.Write(_path);

        var back = TelemetryPrefs.Read(_path);
        back.Enabled.Should().BeFalse();
        back.NoticeShownUtc.Should().Be(shown);
        back.RetentionDays.Should().Be(90);
    }

    [Fact]
    public void Write_creates_missing_directories()
    {
        var nested = Path.Combine(_root, "a", "b", "telemetry_prefs.json");
        new TelemetryPrefs().Write(nested);
        File.Exists(nested).Should().BeTrue();
    }

    [Fact]
    public void Default_path_sits_in_the_roaming_rst_root()
    {
        AppDataPaths.OverrideRootForTests(_root);
        try
        {
            TelemetryPrefs.DefaultPath.Should().Be(Path.Combine(_root, "telemetry_prefs.json"),
                "prefs roam with the user; only the outbox is machine-scoped");
        }
        finally
        {
            AppDataPaths.OverrideRootForTests(null);
        }
    }
}
