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
    [InlineData("{}")]                         // valid JSON, enabled absent
    [InlineData("{\"retentionDays\":90}")]     // partial object, enabled absent
    [InlineData("{\"enabled\":null}")]         // enabled present but not a bool
    public void Damaged_existing_file_fails_closed(string content)
    {
        File.WriteAllText(_path, content);
        TelemetryPrefs.Read(_path).Enabled.Should().BeFalse(
            "damaged prefs degrade to telemetry OFF — a torn disable-write must never re-enable capture");
    }

    [Fact]
    public void Existing_but_unreadable_path_fails_closed()
    {
        // A directory where the prefs file should be: File.Exists() is
        // false (it is not a *file*) yet the path is occupied and the
        // read errors — exactly the exists-but-inaccessible shape that
        // must never read as first run and re-enable capture.
        Directory.CreateDirectory(_path);
        TelemetryPrefs.Read(_path).Enabled.Should().BeFalse(
            "an inaccessible existing prefs state may hold enabled=false — only true not-found is first run");
    }

    [Fact]
    public void Truly_missing_file_in_missing_directory_is_first_run()
    {
        var never = Path.Combine(_root, "no-such-dir", "telemetry_prefs.json");
        TelemetryPrefs.Read(never).Enabled.Should().BeTrue(
            "true not-found — file and directory absent — is first run, on by default");
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
    public void Update_on_a_missing_file_applies_the_mutation_to_first_run_defaults()
    {
        TelemetryPrefs.Update(p => p.RetentionDays = 90, _path).Should().BeTrue();

        var back = TelemetryPrefs.Read(_path);
        back.RetentionDays.Should().Be(90);
        back.Enabled.Should().BeTrue("the untouched fields keep their first-run defaults");
    }

    [Fact]
    public void Update_mutates_against_the_freshest_state_not_a_cached_one()
    {
        // Two instances each change their own field; neither write may
        // revert the other's — the field update must re-read under the
        // lock, never trust what its caller read earlier.
        TelemetryPrefs.Update(p => p.Enabled = false, _path).Should().BeTrue();
        TelemetryPrefs.Update(p => p.RetentionDays = 30, _path).Should().BeTrue();

        var back = TelemetryPrefs.Read(_path);
        back.Enabled.Should().BeFalse("the retention edit must not resurrect the toggled-off state");
        back.RetentionDays.Should().Be(30);
    }

    [Fact]
    public void Update_failure_returns_false_and_never_throws()
    {
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        var unwritable = Path.Combine(blocker, "telemetry_prefs.json");

        string? logged = null;
        var act = () => TelemetryPrefs.Update(p => p.Enabled = false, unwritable, m => logged = m)
            .Should().BeFalse();
        act.Should().NotThrow();
        logged.Should().Contain("failed");
    }

    [Fact]
    public void Update_reports_false_when_the_writer_lock_stays_held()
    {
        var originalTimeout = TelemetryPrefs.LockTimeoutMs;
        TelemetryPrefs.LockTimeoutMs = 150;
        try
        {
            new TelemetryPrefs().Write(_path).Should().BeTrue();
            using var held = new FileStream(
                _path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            string? logged = null;
            TelemetryPrefs.Update(p => p.Enabled = false, _path, m => logged = m)
                .Should().BeFalse("a wedged peer must produce a reported failure, not a stall or a lockless write");
            logged.Should().Contain("lock");
            TelemetryPrefs.Read(_path).Enabled.Should().BeTrue("nothing may be written without the lock");
        }
        finally
        {
            TelemetryPrefs.LockTimeoutMs = originalTimeout;
        }
    }

    [Fact]
    public void Concurrent_updates_serialize_under_the_lock_and_lose_nothing()
    {
        // Two writers hammering read-modify-write increments: any lost
        // update (stale read overwritten) or torn read (fail-closed reset)
        // breaks the final count. Exercises the real lock-file contention
        // path — each Update opens its own FileShare.None handle.
        const int writers = 2, perWriter = 25;
        new TelemetryPrefs { RetentionDays = 0 }.Write(_path).Should().BeTrue();

        var threads = Enumerable.Range(0, writers).Select(_ => new Thread(() =>
        {
            for (var i = 0; i < perWriter; i++)
                TelemetryPrefs.Update(p => p.RetentionDays++, _path)
                    .Should().BeTrue("no increment may fail under contention");
        })).ToArray();
        foreach (var t in threads) t.Start();
        foreach (var t in threads) t.Join();

        var back = TelemetryPrefs.Read(_path);
        back.RetentionDays.Should().Be(writers * perWriter, "every increment must survive");
        back.Enabled.Should().BeTrue("no interleaving may tear the file into the fail-closed state");
        Directory.GetFiles(_root).Should().BeEquivalentTo(
            new[] { _path, _path + ".lock" },
            "contention must leak no temp files — only the prefs file and its lock remain");
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
