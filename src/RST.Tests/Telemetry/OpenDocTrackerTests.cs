// OpenDocTrackerTests.cs — heartbeat open_doc_count bookkeeping and the
// doc_closing → doc_closed correlation (Telemetry v1 spec): a Cancelled
// or Failed close leaves the document open, an unknown closing id (a
// linked / untracked document) is ignored entirely.

using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class OpenDocTrackerTests
{
    [Fact]
    public void Open_documents_are_counted()
    {
        var tracker = new OpenDocTracker();
        tracker.OpenCount.Should().Be(0);
        tracker.Opened();
        tracker.Opened();
        tracker.OpenCount.Should().Be(2);
    }

    [Fact]
    public void Successful_close_decrements_and_correlates()
    {
        var tracker = new OpenDocTracker();
        tracker.Opened();
        tracker.Closing(7);

        tracker.TryCompleteClosing(7, succeeded: true).Should().BeTrue();
        tracker.OpenCount.Should().Be(0);
    }

    [Fact]
    public void Cancelled_close_correlates_but_keeps_the_document_open()
    {
        // Recovery applies the same reading when replaying (ratified
        // call #3): Cancelled/Failed status ⇒ doc still open.
        var tracker = new OpenDocTracker();
        tracker.Opened();
        tracker.Closing(7);

        tracker.TryCompleteClosing(7, succeeded: false).Should().BeTrue(
            "the collector still emits doc_closed with the Cancelled status");
        tracker.OpenCount.Should().Be(1, "a cancelled close leaves the document open");
    }

    [Fact]
    public void Unknown_closing_id_is_ignored()
    {
        // A linked document's DocumentClosed arrives with an id the
        // collector never saw a trackable doc_closing for.
        var tracker = new OpenDocTracker();
        tracker.Opened();

        tracker.TryCompleteClosing(99, succeeded: true).Should().BeFalse();
        tracker.OpenCount.Should().Be(1, "an untracked close must not touch the count");
    }

    [Fact]
    public void Closing_id_correlates_once_only()
    {
        var tracker = new OpenDocTracker();
        tracker.Opened();
        tracker.Closing(7);

        tracker.TryCompleteClosing(7, succeeded: true).Should().BeTrue();
        tracker.TryCompleteClosing(7, succeeded: true).Should().BeFalse("already consumed");
        tracker.OpenCount.Should().Be(0, "a duplicate completion must not go negative");
    }

    [Fact]
    public void Cancelled_close_can_be_retried_under_the_same_id()
    {
        // Real sequence: user closes (cancelled), later closes for good.
        // The Revit args expose DocumentId — the SAME id both attempts —
        // so a consumed id must be registrable again.
        var tracker = new OpenDocTracker();
        tracker.Opened();

        tracker.Closing(7);
        tracker.TryCompleteClosing(7, succeeded: false).Should().BeTrue();
        tracker.OpenCount.Should().Be(1);

        tracker.Closing(7);
        tracker.TryCompleteClosing(7, succeeded: true).Should().BeTrue();
        tracker.OpenCount.Should().Be(0);
    }

    [Fact]
    public void Count_never_goes_negative()
    {
        // Defensive floor: an Opened the collector missed (e.g. doc open
        // before a mid-session enable path ever existed) must not let a
        // close drive the count below zero.
        var tracker = new OpenDocTracker();
        tracker.Closing(7);
        tracker.TryCompleteClosing(7, succeeded: true).Should().BeTrue();
        tracker.OpenCount.Should().Be(0);
    }

    [Fact]
    public void Multiple_documents_close_independently()
    {
        var tracker = new OpenDocTracker();
        tracker.Opened();
        tracker.Opened();
        tracker.Opened();
        tracker.Closing(1);
        tracker.Closing(2);

        tracker.TryCompleteClosing(2, succeeded: true).Should().BeTrue();
        tracker.OpenCount.Should().Be(2);
        tracker.TryCompleteClosing(1, succeeded: false).Should().BeTrue();
        tracker.OpenCount.Should().Be(2);
    }
}
