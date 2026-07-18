// TelemetryPrefsTests.cs — defaults, tolerant reads, and round-trip for
// the consent/config prefs file (Telemetry v1 spec, Build Plan step 1).

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

    [Fact]
    public void Corrupt_file_reads_as_defaults()
    {
        File.WriteAllText(_path, "{ not json");
        TelemetryPrefs.Read(_path).Enabled.Should().BeTrue();
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
