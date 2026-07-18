// ConsentToggleTests.cs — the toggle-click rule (SC-034): every
// successful click serializes its value through the shared prefs writer
// AND reconciles this process's collector — the on-disk state says
// nothing about the local collector, so a click whose value the disk
// already holds must still flip the collector another instance's write
// left behind. The regression models exactly that two-instance shape:
// disk already matches the request, local collector state is opposite.

using System;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class ConsentToggleTests : IDisposable
{
    private const string InstallId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    private const string SessionGuid = "dddddddd-dddd-dddd-dddd-dddddddddddd";

    private readonly string _root;
    private readonly string _prefsPath;
    private readonly string _outboxRoot;
    private readonly OutboxWriter _writer;

    public ConsentToggleTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-ConsentToggleTests-" + Guid.NewGuid().ToString("N"));
        _outboxRoot = Path.Combine(_root, "outbox");
        Directory.CreateDirectory(_outboxRoot);
        _prefsPath = Path.Combine(_root, "telemetry_prefs.json");
        _writer = new OutboxWriter(_outboxRoot, InstallId, SessionGuid);
        _writer.Start();
    }

    public void Dispose()
    {
        _writer.CompleteAndJoin(TimeSpan.FromSeconds(5));
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private CollectorLifecycle NewLifecycle(bool enabled) =>
        new(new TelemetrySession(SessionGuid), _writer, enabled,
            enrichSessionStart: _ => { },
            enrichHeartbeat: _ => { },
            log: _ => { });

    // ---- SC-034: disk already matches, local collector is opposite ------

    [Fact]
    public void Disable_click_stops_a_running_collector_even_when_disk_already_says_disabled()
    {
        // Instance B toggled off: the SHARED prefs file already holds
        // enabled=false. This instance (A) never saw that — its collector
        // is still running. The user now clicks off in A: the pre-fix
        // bridge compared against disk, skipped the collector seam, and
        // returned ok while A kept collecting.
        new TelemetryPrefs { Enabled = false }.Write(_prefsPath).Should().BeTrue();
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue("the local collector starts out live");

        ConsentToggle.Apply(false, lifecycle.SetEnabled, _prefsPath).Should().BeTrue();

        lifecycle.IsEnabled.Should().BeFalse(
            "the click must reconcile THIS process's collector — disk state says nothing about it");
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeFalse("collection must actually have stopped");
        TelemetryPrefs.Read(_prefsPath).Enabled.Should().BeFalse();
        lifecycle.Shutdown();
    }

    [Fact]
    public void Enable_click_starts_a_stopped_collector_even_when_disk_already_says_enabled()
    {
        // Symmetric lie: another instance re-enabled on disk while this
        // one's collector sits stopped; the user clicks on here and the
        // pre-fix bridge reported enabled=true without starting anything.
        new TelemetryPrefs { Enabled = true }.Write(_prefsPath).Should().BeTrue();
        var lifecycle = NewLifecycle(enabled: false);

        ConsentToggle.Apply(true, lifecycle.SetEnabled, _prefsPath).Should().BeTrue();

        lifecycle.IsEnabled.Should().BeTrue("the click must start this process's collector");
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue("collection must actually be live");
        lifecycle.Shutdown();
    }

    [Fact]
    public void Reconciling_an_already_matching_collector_emits_no_duplicate_markers()
    {
        // The unconditional reconcile must stay safe in the common case:
        // local state already matches the click — the seam is a no-op,
        // no stray collection_enabled lands in the file.
        var lifecycle = NewLifecycle(enabled: true);
        lifecycle.TryEmit(TelemetryEventTypes.DocOpening).Should().BeTrue();

        ConsentToggle.Apply(true, lifecycle.SetEnabled, _prefsPath).Should().BeTrue();
        lifecycle.Shutdown();

        OutboxFiles.ReadEvents(_writer.SessionFilePath).Select(e => e.EventType)
            .Should().Equal("session_start", "doc_opening", "session_end");
    }

    // ---- persist-failure ordering ---------------------------------------

    [Fact]
    public void Failed_persist_leaves_the_collector_untouched_and_reports_false()
    {
        // Lock held by a wedged peer: the preference cannot persist, so
        // the collector must not flip — a stopped collector with
        // enabled=true still on disk would resurrect next session.
        var originalTimeout = TelemetryPrefs.LockTimeoutMs;
        TelemetryPrefs.LockTimeoutMs = 150;
        try
        {
            new TelemetryPrefs { Enabled = true }.Write(_prefsPath).Should().BeTrue();
            using var held = new FileStream(
                _prefsPath + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

            bool? applied = null;
            ConsentToggle.Apply(false, v => applied = v, _prefsPath).Should().BeFalse();

            applied.Should().BeNull("a click that did not persist must not touch the collector");
            TelemetryPrefs.Read(_prefsPath).Enabled.Should().BeTrue();
        }
        finally
        {
            TelemetryPrefs.LockTimeoutMs = originalTimeout;
        }
    }

    [Fact]
    public void Throwing_collector_seam_is_swallowed_logged_and_still_reports_success()
    {
        string? logged = null;
        var ok = false;
        var act = () => ok = ConsentToggle.Apply(
            false, _ => throw new InvalidOperationException("seam boom"), _prefsPath, m => logged = m);

        act.Should().NotThrow("the toggle rides the WebView2 dispatch path — it must never throw");
        ok.Should().BeTrue("the preference persisted; that is what the click's success reports");
        logged.Should().Contain("seam boom");
        TelemetryPrefs.Read(_prefsPath).Enabled.Should().BeFalse();
    }

    [Fact]
    public void Null_collector_seam_still_persists_the_preference()
    {
        ConsentToggle.Apply(false, applyToCollector: null, _prefsPath).Should().BeTrue();
        TelemetryPrefs.Read(_prefsPath).Enabled.Should().BeFalse();
    }
}
