// RetentionPrunerTests.cs — prune windows, closed-only deletion,
// lock-skip, and the corrupt-config guard for startup retention
// (Telemetry v1 spec, Build Plan step 1).

using System;
using System.IO;
using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;
using static RST.Tests.Telemetry.OutboxTestData;

namespace RST.Tests.Telemetry;

public sealed class RetentionPrunerTests : IDisposable
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 18, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;

    public RetentionPrunerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-PruneTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    private string ClosedFile(DateTimeOffset newestTs)
    {
        var s = Guid.NewGuid().ToString();
        return WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, newestTs.AddHours(-1)),
            Event(s, 2, TelemetryEventTypes.SessionEnd, newestTs));
    }

    private string OrphanFile(DateTimeOffset newestTs)
    {
        var s = Guid.NewGuid().ToString();
        return WriteSessionFile(_root, s,
            Event(s, 1, TelemetryEventTypes.SessionStart, newestTs));
    }

    [Fact]
    public void Old_closed_files_delete_recent_and_orphaned_stay()
    {
        var oldClosed = ClosedFile(Now.AddDays(-200));
        var boundaryClosed = ClosedFile(Now.AddDays(-179));
        var recentClosed = ClosedFile(Now.AddDays(-3));
        var oldOrphan = OrphanFile(Now.AddDays(-200));

        var deleted = RetentionPruner.Prune(_root, 180, Now);

        deleted.Should().Equal(oldClosed);
        File.Exists(oldClosed).Should().BeFalse();
        File.Exists(boundaryClosed).Should().BeTrue("newest event is inside the window");
        File.Exists(recentClosed).Should().BeTrue();
        File.Exists(oldOrphan).Should().BeTrue(
            "an un-recovered orphan is never pruned — recovery must close it first");
    }

    [Fact]
    public void Locked_file_is_skipped_even_when_expired()
    {
        var oldClosed = ClosedFile(Now.AddDays(-200));

        using (new FileStream(oldClosed, FileMode.Append, FileAccess.Write, OutboxFiles.LiveFileShare))
        {
            RetentionPruner.Prune(_root, 180, Now).Should().BeEmpty();
        }

        File.Exists(oldClosed).Should().BeTrue("a lock-held file waits until next startup");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(RetentionPruner.MaxRetentionDays + 1)]
    [InlineData(int.MaxValue)] // would overflow AddDays if not bounded
    public void Out_of_range_retention_prunes_nothing_and_never_throws(int retentionDays)
    {
        var oldClosed = ClosedFile(Now.AddDays(-2000));

        RetentionPruner.Prune(_root, retentionDays, Now).Should().BeEmpty();

        File.Exists(oldClosed).Should().BeTrue(
            "corrupt prefs must never translate into mass deletion");
    }

    [Fact]
    public void Envelope_less_session_end_is_not_closed_and_never_prunes()
    {
        // An old file whose only session_end lacks the mandatory envelope
        // is NOT closed — recovery must fix it first; deleting it would
        // erase crash evidence.
        var s = Guid.NewGuid().ToString();
        var path = Path.Combine(_root, OutboxFiles.SessionFileName(InstallId, s));
        File.WriteAllText(path, "{\"event_type\":\"session_end\"}\n");

        RetentionPruner.Prune(_root, 180, Now).Should().BeEmpty();

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Missing_outbox_dir_is_an_empty_prune()
    {
        RetentionPruner.Prune(Path.Combine(_root, "missing"), 180, Now).Should().BeEmpty();
    }
}
