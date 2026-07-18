// DocumentTrackingKeyTests.cs — the SC-027 stable throttle/gate key:
// Save As must not re-key a document (throttle continuity, no phantom
// active-document change); independent documents must not collide.

using FluentAssertions;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class DocumentTrackingKeyTests
{
    private const string GuidA = "11111111-1111-1111-1111-111111111111";
    private const string GuidB = "22222222-2222-2222-2222-222222222222";

    [Fact]
    public void Save_as_keeps_the_same_key()
    {
        // Same document, re-pathed and re-titled by Save As — the
        // creation GUID survives, so the key must too.
        var before = DocumentTrackingKey.Derive(GuidA, @"C:\a\old.rvt", "old");
        var after = DocumentTrackingKey.Derive(GuidA, @"C:\b\new.rvt", "new");
        after.Should().Be(before);
    }

    [Fact]
    public void Independent_documents_get_distinct_keys()
    {
        var a = DocumentTrackingKey.Derive(GuidA, @"C:\a.rvt", "a");
        var b = DocumentTrackingKey.Derive(GuidB, @"C:\b.rvt", "b");
        a.Should().NotBe(b);
    }

    [Fact]
    public void Missing_guid_falls_back_to_path_then_title()
    {
        DocumentTrackingKey.Derive(null, @"C:\a.rvt", "a")
            .Should().NotBe(DocumentTrackingKey.Derive(null, @"C:\b.rvt", "a"));
        DocumentTrackingKey.Derive(null, null, "a")
            .Should().NotBe(DocumentTrackingKey.Derive(null, null, "b"));
        DocumentTrackingKey.Derive("", "", "")
            .Should().Be(DocumentTrackingKey.Untitled);
    }

    [Fact]
    public void Key_namespaces_cannot_alias_each_other()
    {
        // A path that textually equals a guid (or a title that equals a
        // path) must not collide across fallback tiers.
        DocumentTrackingKey.Derive(GuidA, null, null)
            .Should().NotBe(DocumentTrackingKey.Derive(null, GuidA, null));
        DocumentTrackingKey.Derive(null, "x", null)
            .Should().NotBe(DocumentTrackingKey.Derive(null, null, "x"));
    }
}
