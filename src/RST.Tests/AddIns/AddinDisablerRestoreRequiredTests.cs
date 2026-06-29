// AddinDisablerRestoreRequiredTests.cs — file-rename round-trip for the
// auto-restore-on-Load path. Uses temp directories with mock .addin files.

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

public sealed class AddinDisablerRestoreRequiredTests : IDisposable
{
    private readonly string _root;

    public AddinDisablerRestoreRequiredTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* test cleanup is best-effort */ }
    }

    private string WriteAddinFile(string name, bool disabled, string? addinId = null)
    {
        var fileName = name + (disabled ? ".RSTdisabled" : "");
        var path = Path.Combine(_root, fileName);
        var idElement = addinId is null
            ? ""
            : "<AddInId>" + addinId + "</AddInId>";
        var xml = "<RevitAddIns><AddIn Type=\"Application\"><Name>X</Name>" + idElement +
                  "<Assembly>X.dll</Assembly></AddIn></RevitAddIns>";
        File.WriteAllText(path, xml);
        return path;
    }

    private IReadOnlyList<(AddinManifest Manifest, AddinSearchPath Source)> ScanRoot()
    {
        var path = new AddinSearchPath(_root, AddinPathKind.UserAddins, ReadOnly: false);
        var manifests = AddinManifestParser.ParseDirectory(_root, onSkip: null).ToList();
        return manifests.Select(m => (m, path)).ToList();
    }

    [Fact]
    public void Restores_only_required_disabled_manifests()
    {
        WriteAddinFile("Kinship.addin", disabled: true);
        WriteAddinFile("Lumion.addin",  disabled: true);
        WriteAddinFile("Other.addin",   disabled: true);   // not required → must stay disabled
        WriteAddinFile("Active.addin",  disabled: false);  // active → no-op

        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinFile = "Kinship.addin" },
            new() { TabName = "Lumion",  AddinFile = "Lumion.addin"  },
        };

        var requiredFiles = AddinDisabler.BuildRequiredFileSet(required);
        var requiredIds   = AddinDisabler.BuildRequiredIdSet(required);
        var result = AddinDisabler.RestoreFiltered(
            ScanRoot(),
            m => AddinDisabler.IsRequired(m, requiredFiles, requiredIds));

        result.RestoredCount.Should().Be(2);
        result.Failed.Should().Be(0);
        result.RestoredFiles.Should().BeEquivalentTo(new[] { "Kinship.addin", "Lumion.addin" });

        File.Exists(Path.Combine(_root, "Kinship.addin")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "Kinship.addin.RSTdisabled")).Should().BeFalse();
        File.Exists(Path.Combine(_root, "Lumion.addin")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "Other.addin.RSTdisabled")).Should().BeTrue();   // still disabled
        File.Exists(Path.Combine(_root, "Active.addin")).Should().BeTrue();              // untouched
    }

    [Fact]
    public void Idempotent_when_required_addins_are_already_active()
    {
        WriteAddinFile("Kinship.addin", disabled: false);

        var required = new List<RequiredAddin>
        {
            new() { TabName = "Kinship", AddinFile = "Kinship.addin" },
        };

        var requiredFiles = AddinDisabler.BuildRequiredFileSet(required);
        var requiredIds   = AddinDisabler.BuildRequiredIdSet(required);
        var result = AddinDisabler.RestoreFiltered(
            ScanRoot(),
            m => AddinDisabler.IsRequired(m, requiredFiles, requiredIds));

        result.RestoredCount.Should().Be(0);
        result.RestoredFiles.Should().BeEmpty();
        File.Exists(Path.Combine(_root, "Kinship.addin")).Should().BeTrue();
    }

    [Fact]
    public void Restores_required_disabled_manifest_matched_only_by_fuzzy_tab()
    {
        // Registry hint filename ("Lumion.addin") differs from the installed
        // file ("LumionLiveSync.addin"); only the fuzzy tab/file-stem tier
        // links them. The old 2-tier restore predicate missed this and left
        // the dependency disabled while the UI reported it restored.
        WriteAddinFile("LumionLiveSync.addin", disabled: true);

        var required = new List<RequiredAddin>
        {
            new() { TabName = "Lumion", AddinFile = "Lumion.addin" },
        };

        var manifests = ScanRoot().Select(s => s.Manifest).ToList();
        var requiredNames = RequiredAddinQa.RequiredManifestFileNames(manifests, required);
        var result = AddinDisabler.RestoreFiltered(
            ScanRoot(), m => requiredNames.Contains(m.FileName));

        result.RestoredCount.Should().Be(1);
        File.Exists(Path.Combine(_root, "LumionLiveSync.addin")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "LumionLiveSync.addin.RSTdisabled")).Should().BeFalse();
    }

    [Fact]
    public void Matches_by_AddinId_when_filename_is_unrelated()
    {
        WriteAddinFile("Renamed.addin", disabled: true,
                       addinId: "22222222-2222-2222-2222-222222222222");

        var required = new List<RequiredAddin>
        {
            new() { TabName = "Whatever", AddinId = "22222222-2222-2222-2222-222222222222" },
        };

        var requiredFiles = AddinDisabler.BuildRequiredFileSet(required);
        var requiredIds   = AddinDisabler.BuildRequiredIdSet(required);
        var result = AddinDisabler.RestoreFiltered(
            ScanRoot(),
            m => AddinDisabler.IsRequired(m, requiredFiles, requiredIds));

        result.RestoredCount.Should().Be(1);
        File.Exists(Path.Combine(_root, "Renamed.addin")).Should().BeTrue();
        File.Exists(Path.Combine(_root, "Renamed.addin.RSTdisabled")).Should().BeFalse();
    }
}
