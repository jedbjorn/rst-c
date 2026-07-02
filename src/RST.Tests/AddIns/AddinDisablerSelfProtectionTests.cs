// AddinDisablerSelfProtectionTests.cs — RST must never disable its own
// manifest. Renaming RST.addin removes the Loader and every in-product
// recovery path (self-lockout), so the disable pass and the preview both
// have to treat RST as protected regardless of the profile's required
// list. Uses temp directories with mock .addin files.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.AddIns;
using RST.Core.Profiles;
using RST.Core.Scanning;
using Xunit;

namespace RST.Tests.AddIns;

public sealed class AddinDisablerSelfProtectionTests : IDisposable
{
    private readonly string _root;

    public AddinDisablerSelfProtectionTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }

    private string WriteAddinFile(string name, string? clientId = null)
    {
        var path = Path.Combine(_root, name);
        var idElement = clientId is null ? "" : "<ClientId>" + clientId + "</ClientId>";
        var xml = "<RevitAddIns><AddIn Type=\"Application\"><Name>X</Name>" + idElement +
                  "<Assembly>X.dll</Assembly></AddIn></RevitAddIns>";
        File.WriteAllText(path, xml);
        return path;
    }

    private List<(AddinManifest Manifest, AddinSearchPath Source)> ScanRoot(bool readOnly = false)
    {
        var path = new AddinSearchPath(_root, AddinPathKind.UserAddins, ReadOnly: readOnly);
        var manifests = AddinManifestParser.ParseDirectory(_root, onSkip: null).ToList();
        return manifests.Select(m => (m, path)).ToList();
    }

    private static HashSet<string> NoRequired() =>
        new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Never_disables_rst_manifest_by_file_name()
    {
        WriteAddinFile("RST.addin");
        WriteAddinFile("Other.addin");

        var result = AddinDisabler.DisableFiltered(ScanRoot(), NoRequired());

        result.DisabledCount.Should().Be(1);
        result.DisabledFiles.Should().BeEquivalentTo(new[] { "Other.addin" });
        File.Exists(Path.Combine(_root, "RST.addin")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "RST.addin.RSTdisabled")).Should().BeFalse();
    }

    [Fact]
    public void Never_disables_rst_manifest_matched_by_client_id_when_renamed()
    {
        // A hand-renamed manifest still carries the canonical ClientId —
        // the id tier must protect it when the file-name tier can't.
        WriteAddinFile("CustomRstName.addin", clientId: AddinDisabler.RstClientId);
        WriteAddinFile("Other.addin", clientId: "22222222-2222-2222-2222-222222222222");

        var result = AddinDisabler.DisableFiltered(ScanRoot(), NoRequired());

        result.DisabledCount.Should().Be(1);
        result.DisabledFiles.Should().BeEquivalentTo(new[] { "Other.addin" });
        File.Exists(Path.Combine(_root, "CustomRstName.addin")).Should().BeTrue();
    }

    [Fact]
    public void Rst_file_name_match_is_case_insensitive()
    {
        WriteAddinFile("rst.ADDIN");

        var result = AddinDisabler.DisableFiltered(ScanRoot(), NoRequired());

        result.DisabledCount.Should().Be(0);
        File.Exists(Path.Combine(_root, "rst.ADDIN")).Should().BeTrue();
    }

    [Fact]
    public void Self_skip_counts_in_no_result_bucket()
    {
        // RST is skipped the same way a required add-in is — silently
        // kept, not counted as read-only/already-disabled/failed.
        WriteAddinFile("RST.addin");

        var result = AddinDisabler.DisableFiltered(ScanRoot(), NoRequired());

        result.DisabledCount.Should().Be(0);
        result.SkippedReadOnly.Should().Be(0);
        result.SkippedAlreadyDisabled.Should().Be(0);
        result.Failed.Should().Be(0);
    }

    [Fact]
    public void Preview_lists_rst_as_staying_never_disabling()
    {
        WriteAddinFile("RST.addin");
        WriteAddinFile("Other.addin");

        var preview = DisablePreviewBuilder.BuildFromScan(
            ScanRoot(), new List<RequiredAddin>());

        preview.Staying.Select(e => e.FileName).Should().Contain("RST.addin");
        preview.Disabling.Select(e => e.FileName).Should().BeEquivalentTo(new[] { "Other.addin" });
    }

    [Fact]
    public void Parser_reads_client_id_into_addin_id()
    {
        // RST.addin (and modern Revit manifests generally) declare
        // <ClientId>, not the legacy <AddInId> — the parser must expose
        // either as the entry's id or the GUID tiers never see it.
        WriteAddinFile("RST.addin", clientId: AddinDisabler.RstClientId);

        var manifest = AddinManifestParser.ParseDirectory(_root, onSkip: null).Single();

        manifest.Entries.Single().AddinId.Should().Be(AddinDisabler.RstClientId);
    }

    [Fact]
    public void Disabled_rst_manifest_is_restorable()
    {
        // Self-protection blocks the disable direction only — a manifest
        // locked out by an older build must still be restorable.
        WriteAddinFile("RST.addin.RSTdisabled");

        var result = AddinDisabler.RestoreFiltered(ScanRoot(), m => true);

        result.RestoredCount.Should().Be(1);
        File.Exists(Path.Combine(_root, "RST.addin")).Should().BeTrue();
    }
}
