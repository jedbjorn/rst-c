// CloseClipRekeyTests.cs — SC-053 (PR #177 round-4 review, R9b): a Save As
// that re-keys a doc during a close, followed by a Cancelled or Failed
// doc_closed, undoes the provisional doc_closing clip — the close never
// happened, so the re-keyed doc's later edits count. Checked through the fold
// and the shared reader.

using System;
using System.Linq;
using FluentAssertions;
using RST.Core.Telemetry;
using RST.Core.Telemetry.Rollups;
using Xunit;

namespace RST.Tests.Telemetry.Rollups;

public sealed class CloseClipRekeyTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    private readonly RollupStoreFixture _f = new(Now);

    public void Dispose() => _f.Dispose();

    private static DateTimeOffset D(int hour, int minute = 0, int second = 0) =>
        new(2026, 10, 29, hour, minute, second, TimeSpan.Zero);

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Failed")]
    public void A_cancelled_or_failed_close_after_a_rekeying_save_as_undoes_the_clip(string status)
    {
        var doc = SessionBuilder.Central("Tower");
        var rekeyed = new DocumentIdentity
        {
            CreationGuid = doc.CreationGuid,
            CentralPath = @"\\server\projects\Tower2.rvt",
            LocalPath = @"C:\Users\me\Documents\Tower2_me.rvt",
            Title = "Tower2",
            IsWorkshared = true,
            IsCloud = false,
        };
        new SessionBuilder().Start(D(8)).Open(D(9), doc, 0)
            .ClosingOnly(D(10), doc)                                   // closing_id c1
            .SaveAs(D(10, 0, 10), rekeyed, doc.LocalPath!)            // re-keys the doc
            .ClosedOnly(D(10, 0, 20), SessionBuilder.ClosingIdOf(1), status) // the close did not happen
            .Pulse(D(10, 1), rekeyed)
            .End(D(10, 5)).Write(_f.Outbox);

        // In memory (unfolded), then folded: the same 60 seconds.
        ModelActivityReader.ReadCurrentModel(rekeyed, _f.ReadOptions()).Days.Sum(d => d.ActiveMs).Should().Be(60_000);
        _f.Pass().Folded.Should().Be(1);
        _f.Rollup(rekeyed)!.Days.Single().ActiveMs.Should().Be(60_000);
        ModelActivityReader.ReadCurrentModel(rekeyed, _f.ReadOptions()).Days.Sum(d => d.ActiveMs).Should().Be(60_000);
    }

    [Fact]
    public void Without_a_cancel_the_rekeyed_doc_keeps_the_clip()
    {
        var doc = SessionBuilder.Central("Tower");
        var rekeyed = new DocumentIdentity
        {
            CreationGuid = doc.CreationGuid,
            CentralPath = @"\\server\projects\Tower2.rvt",
            LocalPath = @"C:\Users\me\Documents\Tower2_me.rvt",
            Title = "Tower2",
            IsWorkshared = true,
            IsCloud = false,
        };
        new SessionBuilder().Start(D(8)).Open(D(9), doc, 0)
            .ClosingOnly(D(10), doc)
            .SaveAs(D(10, 0, 10), rekeyed, doc.LocalPath!)
            .Pulse(D(10, 1), rekeyed)
            .End(D(10, 5)).Write(_f.Outbox);
        _f.Pass();
        (_f.Rollup(rekeyed)?.Days.Sum(d => d.ActiveMs) ?? 0).Should().Be(0);
    }
}
