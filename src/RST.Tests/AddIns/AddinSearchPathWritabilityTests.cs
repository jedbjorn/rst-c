// AddinSearchPathWritabilityTests.cs — flag #15: the confirm modal's
// writability classification must reflect the RUNNING token, not the
// path kind. ProgramData is writable to an elevated console session but
// not to interactive (non-elevated) Revit; classifying it as writable
// made the modal promise disables that every rename then failed, while
// the UI reported all-clear.
//
// Covers:
//   - BuildSearchPaths marks a root ReadOnly when the probe says the
//     token can't write there (and writable when it can).
//   - The Revit install dir is ReadOnly by policy and is NEVER probed
//     (no write attempts inside the install tree).
//   - Probed-ReadOnly roots flow through to the preview (tryDisable
//     bucket) and the disable pass (skippedReadOnly, zero failures) —
//     preview matches commit for a non-elevated token.
//   - DirectoryWritability.CanWrite: true on a writable dir, false on a
//     missing dir and on a dir the token can't create files in, and no
//     probe file left behind.

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

public sealed class AddinSearchPathWritabilityTests : IDisposable
{
    private readonly string _root;

    public AddinSearchPathWritabilityTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "RST-Tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                // Undo any permission clamp so recursive delete succeeds.
                if (!OperatingSystem.IsWindows())
                {
                    foreach (var dir in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories).Append(_root))
                        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                }
                Directory.Delete(_root, recursive: true);
            }
        }
        catch { /* test cleanup is best-effort */ }
    }

    private (string AppData, string ProgramData, string ProgramFiles) MakeLayout(string ver)
    {
        var appData = Path.Combine(_root, "AppData");
        var programData = Path.Combine(_root, "ProgramData");
        var programFiles = Path.Combine(_root, "ProgramFiles");
        Directory.CreateDirectory(Path.Combine(appData, "Autodesk", "Revit", "Addins", ver));
        Directory.CreateDirectory(Path.Combine(programData, "Autodesk", "Revit", "Addins", ver));
        Directory.CreateDirectory(Path.Combine(programFiles, "Autodesk", "Revit " + ver));
        return (appData, programData, programFiles);
    }

    [Fact]
    public void Probe_result_decides_readonly_for_user_and_machine_roots()
    {
        var (appData, programData, programFiles) = MakeLayout("2026");
        var machineRoot = Path.Combine(programData, "Autodesk", "Revit", "Addins", "2026");

        // Simulate a filtered (non-elevated) token: ProgramData not writable.
        var roots = AddinDirectoryScanner.BuildSearchPaths(
            appData, programData, programFiles, "2026",
            canWrite: p => !p.StartsWith(programData, StringComparison.OrdinalIgnoreCase));

        roots.Single(r => r.Kind == AddinPathKind.UserAddins).ReadOnly.Should().BeFalse();
        roots.Single(r => r.Kind == AddinPathKind.MachineAddins).Should().BeEquivalentTo(
            new AddinSearchPath(machineRoot, AddinPathKind.MachineAddins, ReadOnly: true));
    }

    [Fact]
    public void Probe_says_writable_everywhere_for_an_elevated_token()
    {
        var (appData, programData, programFiles) = MakeLayout("2026");

        var roots = AddinDirectoryScanner.BuildSearchPaths(
            appData, programData, programFiles, "2026", canWrite: _ => true);

        roots.Single(r => r.Kind == AddinPathKind.MachineAddins).ReadOnly.Should().BeFalse();
        roots.Single(r => r.Kind == AddinPathKind.UserAddins).ReadOnly.Should().BeFalse();
    }

    [Fact]
    public void Revit_install_dir_is_readonly_by_policy_and_never_probed()
    {
        var (appData, programData, programFiles) = MakeLayout("2026");
        var probed = new List<string>();

        var roots = AddinDirectoryScanner.BuildSearchPaths(
            appData, programData, programFiles, "2026",
            canWrite: p => { probed.Add(p); return true; });

        var install = roots.Single(r => r.Kind == AddinPathKind.RevitInstall);
        install.ReadOnly.Should().BeTrue("shipped add-ins are not ours to rename, whatever the token");
        probed.Should().NotContain(install.Path, "the probe writes a temp file — never inside the install tree");
    }

    [Fact]
    public void Nonwritable_machine_root_lands_in_tryDisable_and_disable_skips_it()
    {
        // The exact VM failure shape: a non-required manifest in a machine
        // path the token can't write. Before the fix it was bucketed
        // "disabling" (promised) and then failed the rename; now the
        // preview says tryDisable and the commit skips — no failures.
        var machineDir = Path.Combine(_root, "MachineAddins");
        Directory.CreateDirectory(machineDir);
        File.WriteAllText(Path.Combine(machineDir, "BatchPrint.addin"),
            "<RevitAddIns><AddIn Type=\"Application\"><Name>BatchPrint</Name>" +
            "<Assembly>BatchPrint.dll</Assembly></AddIn></RevitAddIns>");

        var source = new AddinSearchPath(machineDir, AddinPathKind.MachineAddins, ReadOnly: true);
        var scan = AddinManifestParser.ParseDirectory(machineDir, onSkip: null)
            .Select(m => (m, source)).ToList();

        var preview = DisablePreviewBuilder.BuildFromScan(scan, Array.Empty<RequiredAddin>());
        preview.Disabling.Should().BeEmpty();
        preview.TryDisable.Should().ContainSingle(e => e.FileName == "BatchPrint.addin");

        var result = AddinDisabler.DisableFiltered(scan, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        result.Failed.Should().Be(0);
        result.DisabledCount.Should().Be(0);
        result.SkippedReadOnly.Should().Be(1);
        File.Exists(Path.Combine(machineDir, "BatchPrint.addin")).Should().BeTrue("no rename may be attempted");
    }

    [Fact]
    public void CanWrite_true_on_writable_dir_and_leaves_no_probe_file()
    {
        DirectoryWritability.CanWrite(_root).Should().BeTrue();
        Directory.EnumerateFileSystemEntries(_root).Should().BeEmpty("the probe file must be deleted on close");
    }

    [Fact]
    public void CanWrite_false_on_missing_dir()
    {
        DirectoryWritability.CanWrite(Path.Combine(_root, "does-not-exist")).Should().BeFalse();
        DirectoryWritability.CanWrite("").Should().BeFalse();
    }

    [Fact]
    public void CanWrite_false_when_token_cannot_create_files()
    {
        // Permission clamp is POSIX-only (Windows ACL denial needs admin
        // to set up, and root ignores POSIX modes) — skip where the
        // premise can't be established.
        if (OperatingSystem.IsWindows() || Environment.UserName == "root") return;

        var clamped = Path.Combine(_root, "no-write");
        Directory.CreateDirectory(clamped);
        File.SetUnixFileMode(clamped, UnixFileMode.UserRead | UnixFileMode.UserExecute);

        DirectoryWritability.CanWrite(clamped).Should().BeFalse();
    }
}
