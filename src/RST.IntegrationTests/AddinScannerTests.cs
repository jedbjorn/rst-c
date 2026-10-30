using System.Collections.Generic;
using System.IO;
using System.Linq;
using FluentAssertions;
using RST.Core.Activation;
using RST.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace RST.IntegrationTests;

/// <summary>
/// Integration tests for the add-in inventory (spec #18, feature #16) and AddinManifestParser
/// against real Revit installations. Designed to run on the self-hosted Windows runner
/// (W10C_DOS-ARCH_Testing) where Revit and third-party add-ins are present. Read only: nothing is
/// switched.
///
/// All tests assert that at least one Revit version is installed — a failed
/// assertion here means the runner is misconfigured, not that the code is wrong.
/// </summary>
public sealed class AddinScannerTests
{
    private readonly ITestOutputHelper _output;

    public AddinScannerTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void At_least_one_revit_version_is_installed()
    {
        var versions = RevitEnvironment.InstalledVersions();
        _output.WriteLine($"Detected Revit version(s): {string.Join(", ", versions.Count > 0 ? versions : new[] { "(none)" })}");
        versions.Should().NotBeEmpty("this test suite requires Revit on the integration runner");
    }

    [Fact]
    public void Detection_finds_the_installed_years_with_their_build_and_mechanism()
    {
        var years = RevitInstallations.Detect(AddinRoots.FromEnvironment());
        foreach (var y in years)
            _output.WriteLine($"Revit {y.Year}: dir={y.InstallDir} build={y.Build} mechanism={y.Mechanism}");
        years.Should().NotBeEmpty();
        years.Select(y => y.Year.ToString()).Should().BeSubsetOf(
            RevitEnvironment.InstalledVersions().Where(v => RevitInstallations.SupportedYears.Contains(int.Parse(v))));
    }

    [Fact]
    public void The_inventory_parses_real_manifests_with_one_row_per_add_in_per_year()
    {
        var skipped = new List<string>();
        var inventory = AddinInventory.ScanMachine((path, ex) => skipped.Add($"{path}: {ex.GetType().Name} — {ex.Message}"));
        foreach (var y in inventory.Years)
            _output.WriteLine($"Revit {y.Year}: settings={y.Settings} ({y.SettingsPath}) admin={y.AdminPath}");
        foreach (var i in inventory.Items)
            _output.WriteLine($"  {i.Year} [{i.Scope}] {i.Name} off={i.IsOff} lock={i.Lock} bundle={i.Bundle?.Range} {i.CurrentPath}");
        foreach (var s in skipped)
            _output.WriteLine($"  SKIP {s}");

        inventory.Items.Should().NotBeEmpty("Revit itself ships with add-ins");
        inventory.Items.GroupBy(i => (i.Year, i.ManifestPath.ToLowerInvariant())).Should().OnlyContain(g => g.Count() == 1, "no duplicates");
        inventory.Items.Should().OnlyContain(i => File.Exists(i.CurrentPath), "every manifest exists on disk");
    }

    [Fact]
    public void OriginClassifier_handles_all_scanned_manifests_without_throwing()
    {
        foreach (var item in AddinInventory.ScanMachine().Items)
        foreach (var entry in item.Entries)
        {
            // Classifier must not throw regardless of assembly path content.
            var origin = OriginClassifier.Classify(tabName: entry.Name, assemblyPath: entry.AssemblyPath);
            _output.WriteLine($"  {item.FileName} / {entry.Name} → {origin}");
        }
    }
}
