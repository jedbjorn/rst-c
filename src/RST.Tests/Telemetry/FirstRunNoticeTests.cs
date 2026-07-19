// FirstRunNoticeTests.cs — the consent notice's show/mark rule (spec
// Consent & Config, Build Plan step 5): show on the first ENABLED
// session where the notice was never shown; once marked, never again;
// a failed mark write reports false (the notice re-shows next session)
// and never throws.

using System;
using System.IO;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class FirstRunNoticeTests : IDisposable
{
    private readonly string _root;
    private readonly string _path;

    public FirstRunNoticeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-FirstRunTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "telemetry_prefs.json");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void First_run_defaults_are_due_for_the_notice()
    {
        FirstRunNotice.ShouldShow(TelemetryPrefs.Read(_path))
            .Should().BeTrue("enabled by default + never shown is exactly the spec's trigger");
    }

    [Fact]
    public void Disabled_session_is_not_due_even_when_never_shown()
    {
        var prefs = new TelemetryPrefs { Enabled = false };
        FirstRunNotice.ShouldShow(prefs)
            .Should().BeFalse("the notice waits for the first session that actually collects");
    }

    [Fact]
    public void Already_shown_is_never_due_again()
    {
        var prefs = new TelemetryPrefs { NoticeShownUtc = DateTimeOffset.UtcNow };
        FirstRunNotice.ShouldShow(prefs).Should().BeFalse();
    }

    [Fact]
    public void Mark_shown_persists_and_suppresses_the_next_session()
    {
        var shownAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

        FirstRunNotice.MarkShown(shownAt, _path).Should().BeTrue();

        var reread = TelemetryPrefs.Read(_path);
        reread.NoticeShownUtc.Should().Be(shownAt);
        reread.Enabled.Should().BeTrue("marking the notice must not touch the consent state");
        FirstRunNotice.ShouldShow(reread).Should().BeFalse();
    }

    [Fact]
    public void Mark_shown_preserves_concurrent_toggle_and_retention_writes()
    {
        // SC-033: instance B decides the notice is due from a pre-dialog
        // snapshot and holds the TaskDialog open; instance A meanwhile
        // turns collection off and changes retention. B's dismissal must
        // mark the notice WITHOUT resurrecting enabled=true or the old
        // retention — the next session would otherwise silently resume
        // collection the user turned off.
        var shownAt = new DateTimeOffset(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);
        FirstRunNotice.ShouldShow(TelemetryPrefs.Read(_path))
            .Should().BeTrue("B's pre-dialog snapshot says the notice is due");

        TelemetryPrefs.Update(p => p.Enabled = false, _path).Should().BeTrue();
        TelemetryPrefs.Update(p => p.RetentionDays = 30, _path).Should().BeTrue();

        FirstRunNotice.MarkShown(shownAt, _path).Should().BeTrue();

        var final = TelemetryPrefs.Read(_path);
        final.Enabled.Should().BeFalse(
            "a toggle-off landed while the notice sat open must survive its dismissal");
        final.RetentionDays.Should().Be(30);
        final.NoticeShownUtc.Should().Be(shownAt);
    }

    [Fact]
    public void Mark_shown_failure_reports_false_and_never_throws()
    {
        // A path whose parent is a FILE cannot be created — the prefs
        // update's directory creation fails, exercising the IO-failure arm.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var badPath = Path.Combine(blocker, "telemetry_prefs.json");

        var act = () => FirstRunNotice.MarkShown(DateTimeOffset.UtcNow, badPath)
            .Should().BeFalse("a failed write means the notice re-shows next session — never a crash");
        act.Should().NotThrow();
    }
}
