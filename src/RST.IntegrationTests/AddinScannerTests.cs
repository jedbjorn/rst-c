using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using RST.Core.AddIns;
using RST.Core.Scanning;
using Xunit;
using Xunit.Abstractions;

namespace RST.IntegrationTests;

/// <summary>
/// Integration tests for AddinDirectoryScanner and AddinManifestParser against
/// real Revit installations. Designed to run on the self-hosted Windows runner
/// (W10C_DOS-ARCH_Testing) where Revit and third-party add-ins are present.
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
    public void GetSearchPaths_returns_only_directories_that_exist()
    {
        foreach (var ver in RevitEnvironment.InstalledVersions())
        {
            var paths = AddinDirectoryScanner.GetSearchPaths(ver);
            _output.WriteLine($"Revit {ver}: {paths.Count} search path(s)");
            foreach (var sp in paths)
            {
                _output.WriteLine($"  [{sp.Kind}, readonly={sp.ReadOnly}] {sp.Path}");
                Directory.Exists(sp.Path).Should().BeTrue($"search path {sp.Path} must exist");
            }
            paths.Should().NotBeEmpty($"Revit {ver} is installed so at least one addin path must be found");
        }
    }

    [Fact]
    public void Scan_parses_real_manifests_without_unhandled_exceptions()
    {
        foreach (var ver in RevitEnvironment.InstalledVersions())
        {
            var skipped = new List<string>();
            var manifests = AddinDirectoryScanner.Scan(ver,
                onSkip: (path, ex) => skipped.Add($"{path}: {ex.GetType().Name} — {ex.Message}"));

            _output.WriteLine($"Revit {ver}: {manifests.Count} manifest(s) parsed, {skipped.Count} skipped");
            foreach (var s in skipped)
                _output.WriteLine($"  SKIP {s}");

            // We expect at least some manifests — Revit itself ships with add-ins.
            manifests.Should().NotBeEmpty($"Revit {ver} should have at least its own built-in add-ins");
        }
    }

    [Fact]
    public void Scanned_manifests_have_valid_non_empty_file_paths()
    {
        foreach (var ver in RevitEnvironment.InstalledVersions())
        {
            var manifests = AddinDirectoryScanner.Scan(ver);
            foreach (var m in manifests)
            {
                m.FilePath.Should().NotBeNullOrEmpty();
                m.FileName.Should().NotBeNullOrEmpty();
                File.Exists(m.FilePath).Should().BeTrue($"manifest file {m.FilePath} must exist on disk");
            }
        }
    }

    [Fact]
    public void Scan_deduplicates_when_same_manifest_appears_in_multiple_paths()
    {
        foreach (var ver in RevitEnvironment.InstalledVersions())
        {
            var withSource = AddinDirectoryScanner.ScanWithSource(ver);
            var plain      = AddinDirectoryScanner.Scan(ver);

            // ScanWithSource and Scan must agree on count (same dedup logic).
            withSource.Count.Should().Be(plain.Count,
                "ScanWithSource and Scan must apply the same deduplication");

            // No two entries in the tagged result should share the same FilePath.
            var paths = new System.Collections.Generic.HashSet<string>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var (manifest, _) in withSource)
                paths.Add(manifest.FilePath).Should().BeTrue(
                    $"duplicate path {manifest.FilePath} found — dedup is broken");
        }
    }

    [Fact]
    public void OriginClassifier_handles_all_scanned_manifests_without_throwing()
    {
        foreach (var ver in RevitEnvironment.InstalledVersions())
        {
            var manifests = AddinDirectoryScanner.Scan(ver);
            foreach (var m in manifests)
            foreach (var entry in m.Entries)
            {
                // Classifier must not throw regardless of assembly path content.
                var origin = OriginClassifier.Classify(
                    tabName: entry.Name,
                    assemblyPath: entry.AssemblyPath);
                _output.WriteLine($"  {m.FileName} / {entry.Name} → {origin}");
            }
        }
    }

    [Fact]
    public void ScanWithSource_tags_every_manifest_with_an_existing_source_path()
    {
        foreach (var ver in RevitEnvironment.InstalledVersions())
        {
            var results = AddinDirectoryScanner.ScanWithSource(ver);
            foreach (var (manifest, source) in results)
            {
                source.Path.Should().NotBeNullOrEmpty();
                Directory.Exists(source.Path).Should().BeTrue(
                    $"source path {source.Path} for {manifest.FileName} must exist");
                manifest.FilePath.Should().StartWith(source.Path,
                    System.StringComparison.OrdinalIgnoreCase,
                    $"{manifest.FileName} must be under its tagged source {source.Path}");
            }
        }
    }
}
