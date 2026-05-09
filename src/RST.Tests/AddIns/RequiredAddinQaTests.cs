// RequiredAddinQaTests.cs — pure classification (no file IO).

using System.Collections.Generic;
using FluentAssertions;
using RST.Core.AddIns;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.AddIns;

public sealed class RequiredAddinQaTests
{
    private static AddinSearchPath SearchPath() =>
        new("/fake/path", AddinPathKind.UserAddins, ReadOnly: false);

    private static AddinManifest Manifest(string fileName, bool disabled, string? addinId = null) =>
        new(
            FilePath: $"/fake/path/{fileName}{(disabled ? ".RSTdisabled" : "")}",
            FileName: fileName,
            IsDisabled: disabled,
            Entries: addinId is null
                ? new List<AddinEntry>()
                : new List<AddinEntry> { new("Application", null, addinId, "X", null, null) });

    [Fact]
    public void Empty_required_list_yields_empty_results()
    {
        var scan = new[] { (Manifest("Foo.addin", false), SearchPath()) };
        var results = RequiredAddinQa.Classify(scan, new List<RequiredAddin>());
        results.Should().BeEmpty();
    }

    [Fact]
    public void Null_required_list_yields_empty_results()
    {
        var scan = new[] { (Manifest("Foo.addin", false), SearchPath()) };
        var results = RequiredAddinQa.Classify(scan, null);
        results.Should().BeEmpty();
    }

    [Fact]
    public void Active_manifest_classifies_as_InstalledActive()
    {
        var scan = new[] { (Manifest("Kinship.addin", disabled: false), SearchPath()) };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinFile = "Kinship.addin" },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results.Should().HaveCount(1);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledActive);
        results[0].MatchedManifest!.FileName.Should().Be("Kinship.addin");
    }

    [Fact]
    public void Disabled_manifest_classifies_as_InstalledDisabled()
    {
        var scan = new[] { (Manifest("Kinship.addin", disabled: true), SearchPath()) };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinFile = "Kinship.addin" },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results.Should().HaveCount(1);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledDisabled);
        results[0].MatchedManifest!.IsDisabled.Should().BeTrue();
    }

    [Fact]
    public void Missing_manifest_classifies_as_NotInstalled()
    {
        var scan = new[] { (Manifest("Other.addin", false), SearchPath()) };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinFile = "Kinship.addin" },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results.Should().HaveCount(1);
        results[0].Status.Should().Be(RequiredAddinStatus.NotInstalled);
        results[0].MatchedManifest.Should().BeNull();
    }

    [Fact]
    public void AddinId_match_falls_through_when_filename_missing()
    {
        var scan = new[]
        {
            (Manifest("RandomFile.addin", false, addinId: "11111111-1111-1111-1111-111111111111"), SearchPath()),
        };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinId = "11111111-1111-1111-1111-111111111111" },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledActive);
        results[0].MatchedManifest!.FileName.Should().Be("RandomFile.addin");
    }

    private static AddinManifest ManifestWithEntry(string fileName, bool disabled, string entryName) =>
        new(
            FilePath: $"/fake/path/{fileName}{(disabled ? ".RSTdisabled" : "")}",
            FileName: fileName,
            IsDisabled: disabled,
            Entries: new List<AddinEntry> { new("Application", null, null, entryName, null, null) });

    [Fact]
    public void Fuzzy_displayName_match_classifies_as_InstalledActive_when_filename_misses()
    {
        // Registry says "Lumion.addin" but the actual install ships as
        // "RandomCorpLive.addin" with entry Name "Lumion LiveSync".
        // Tier-1 + tier-2 miss, tier-3 hits via entry Name substring.
        var scan = new[]
        {
            (ManifestWithEntry("RandomCorpLive.addin", false, "Lumion LiveSync"), SearchPath()),
        };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Lumion", AddinFile = "Lumion.addin" },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledActive);
        results[0].MatchedManifest!.FileName.Should().Be("RandomCorpLive.addin");
    }

    [Fact]
    public void Fuzzy_filestem_match_classifies_as_InstalledActive()
    {
        // Manifest filename "LumionLiveSync.addin" — strip suffix to
        // "LumionLiveSync", which contains "Lumion".
        var scan = new[] { (Manifest("LumionLiveSync.addin", false), SearchPath()) };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Lumion", AddinFile = "Lumion.addin" },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledActive);
    }

    [Fact]
    public void Live_ribbon_tab_match_classifies_as_InstalledActive_even_when_no_manifest_matches()
    {
        // Pathological case: addin's manifest filename + displayName
        // bear no resemblance to its tab title (set programmatically),
        // but the tab IS on the ribbon — the picker would show Loaded,
        // so QA must agree.
        var scan = new[] { (Manifest("RandomCo.addin", false), SearchPath()) };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Lumion", AddinFile = "Lumion.addin" },
        };
        var loadedTabs = new[] { "Architecture", "Lumion", "View" };

        var results = RequiredAddinQa.Classify(scan, required, loadedTabs);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledActive);
        results[0].MatchedManifest.Should().BeNull();
    }

    [Fact]
    public void NotInstalled_when_neither_manifest_nor_ribbon_matches()
    {
        var scan = new[] { (Manifest("Unrelated.addin", false), SearchPath()) };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Lumion", AddinFile = "Lumion.addin" },
        };
        var loadedTabs = new[] { "Architecture", "View" };

        var results = RequiredAddinQa.Classify(scan, required, loadedTabs);
        results[0].Status.Should().Be(RequiredAddinStatus.NotInstalled);
    }

    [Fact]
    public void Mixed_required_list_returns_one_result_per_input_in_order()
    {
        var scan = new[]
        {
            (Manifest("Kinship.addin", disabled: false), SearchPath()),
            (Manifest("Lumion.addin", disabled: true),  SearchPath()),
        };
        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinFile = "Kinship.addin" },
            new() { TabName = "Lumion",  AddinFile = "Lumion.addin"  },
            new() { TabName = "Ghost",   AddinFile = "Ghost.addin"   },
        };
        var results = RequiredAddinQa.Classify(scan, required);
        results.Should().HaveCount(3);
        results[0].Status.Should().Be(RequiredAddinStatus.InstalledActive);
        results[1].Status.Should().Be(RequiredAddinStatus.InstalledDisabled);
        results[2].Status.Should().Be(RequiredAddinStatus.NotInstalled);
    }
}
