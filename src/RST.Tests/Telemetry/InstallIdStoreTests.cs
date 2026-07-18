// InstallIdStoreTests.cs — first-run minting, stability across reads,
// garbage tolerance, and the machine-scoped telemetry paths
// (Telemetry v1 spec, Build Plan step 1).

using System;
using System.IO;
using System.Linq;
using System.Threading;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Telemetry;
using Xunit;

namespace RST.Tests.Telemetry;

public sealed class InstallIdStoreTests : IDisposable
{
    private readonly string _root;

    public InstallIdStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-InstallIdTests-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort */ }
    }

    [Fact]
    public void First_run_mints_persists_and_stays_stable()
    {
        var first = InstallIdStore.GetOrCreate(_root);
        Guid.TryParse(first, out _).Should().BeTrue();
        File.Exists(Path.Combine(_root, "install_id")).Should().BeTrue();

        InstallIdStore.GetOrCreate(_root).Should().Be(first,
            "the install id is minted once and re-read forever after");
    }

    [Fact]
    public void Concurrent_first_runs_agree_on_one_install_id()
    {
        // Two (here: eight) Revit instances starting simultaneously on a
        // fresh machine race to mint — every session must report the SAME
        // id, and the file must hold it (create-if-absent, loser re-reads).
        const int racers = 8;
        var ids = new string[racers];
        using var barrier = new Barrier(racers);
        var threads = Enumerable.Range(0, racers)
            .Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                ids[i] = InstallIdStore.GetOrCreate(_root);
            }))
            .ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        ids.Distinct().Should().ContainSingle(
            "concurrent first starts must never attribute one install to two ids");
        File.ReadAllText(Path.Combine(_root, "install_id")).Trim().Should().Be(ids[0]);
    }

    [Fact]
    public void Concurrent_corrupt_file_repairs_agree_on_one_install_id()
    {
        // Simultaneous Revit instances all finding the same corrupt file
        // must converge on ONE repaired id — sequential last-writer-wins
        // repair would let each session report a different install.
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "install_id");
        File.WriteAllText(path, "not a guid");

        const int racers = 8;
        var ids = new string[racers];
        using var barrier = new Barrier(racers);
        var threads = Enumerable.Range(0, racers)
            .Select(i => new Thread(() =>
            {
                barrier.SignalAndWait();
                ids[i] = InstallIdStore.GetOrCreate(_root);
            }))
            .ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());

        ids.Distinct().Should().ContainSingle(
            "every simultaneous repairer must return the id the file ends up holding");
        File.ReadAllText(path).Trim().Should().Be(ids[0]);
    }

    [Fact]
    public void Garbage_file_re_mints_instead_of_failing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "install_id"), "not a guid");

        var id = InstallIdStore.GetOrCreate(_root);

        Guid.TryParse(id, out _).Should().BeTrue();
        InstallIdStore.GetOrCreate(_root).Should().Be(id, "the re-mint persists");
    }

    [Fact]
    public void Telemetry_paths_are_overridable_for_tests_and_machine_scoped_by_default()
    {
        AppDataPaths.OverrideTelemetryRootForTests(_root);
        try
        {
            AppDataPaths.TelemetryRoot.Should().Be(_root);
            AppDataPaths.TelemetryOutboxDir.Should().Be(Path.Combine(_root, "outbox"));
        }
        finally
        {
            AppDataPaths.OverrideTelemetryRootForTests(null);
        }

        // Restored resolution points at LocalApplicationData (machine
        // scope), never the roaming Root the rest of AppDataPaths uses.
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        AppDataPaths.TelemetryRoot.Should().Be(Path.Combine(local, "RST", "telemetry"));
    }
}
