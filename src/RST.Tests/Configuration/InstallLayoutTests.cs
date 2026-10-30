// rst-c's own: the two install scopes' paths and the shadow rule (feature #17, spec #20 §7; F17 WU5), plus the tray's
// one-time shadow notice and its settings field.

using System;
using System.Collections.Generic;
using System.IO;
using FluentAssertions;
using RST.Core.Configuration;
using RST.Core.Tray;
using Xunit;

namespace RST.Tests.Configuration;

public sealed class InstallLayoutTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "RST-InstallLayout-" + Guid.NewGuid().ToString("N"));

    public InstallLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private string AppData => Path.Combine(_root, "Roaming");
    private string ProgramData => Path.Combine(_root, "ProgramData");
    private string ProgramFiles => Path.Combine(_root, "Program Files");

    private static void Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "<RevitAddIns />");
    }

    [Fact]
    public void Manifest_paths_match_the_installers()
    {
        InstallLayout.UserManifest(AppData, 2026).Should().Be(Path.Combine(AppData, "Autodesk", "Revit", "Addins", "2026", "RST.addin"));
        // Revit 2025 and 2026 read all-users manifests from %ProgramData%; 2027 from Program Files (Product.wxs).
        InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2025).Should().Be(Path.Combine(ProgramData, "Autodesk", "Revit", "Addins", "2025", "RST.addin"));
        InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2026).Should().Be(Path.Combine(ProgramData, "Autodesk", "Revit", "Addins", "2026", "RST.addin"));
        InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2027).Should().Be(Path.Combine(ProgramFiles, "Autodesk", "Revit", "Addins", "2027", "RST.addin"));
        InstallLayout.MachineTrayDir(ProgramFiles).Should().Be(Path.Combine(ProgramFiles, "RST", "Tray"));
        InstallLayout.ShippedYears.Should().Equal(2025, 2026, 2027);
    }

    [Fact]
    public void A_shadow_is_a_year_with_both_manifests()
    {
        Touch(InstallLayout.UserManifest(AppData, 2026));
        Touch(InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2026));
        Touch(InstallLayout.UserManifest(AppData, 2027));                       // per user only
        Touch(InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2025));  // per machine only

        var shadows = InstallLayout.FindShadows(AppData, ProgramData, ProgramFiles, InstallLayout.ShippedYears, File.Exists);

        shadows.Should().ContainSingle();
        shadows[0].Year.Should().Be(2026);
        shadows[0].UserManifest.Should().Be(InstallLayout.UserManifest(AppData, 2026));
        shadows[0].MachineManifest.Should().Be(InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2026));
        InstallLayout.Describe(shadows[0]).Should().Contain("Revit 2026").And.Contain("your own copy")
            .And.Contain(shadows[0].UserManifest).And.Contain("Settings > Apps");
    }

    [Fact]
    public void Revit_2027_compares_with_the_Program_Files_manifest()
    {
        Touch(InstallLayout.UserManifest(AppData, 2027));
        Touch(Path.Combine(ProgramData, "Autodesk", "Revit", "Addins", "2027", "RST.addin")); // not where 2027 looks

        InstallLayout.FindShadows(AppData, ProgramData, ProgramFiles, new[] { 2027 }, File.Exists).Should().BeEmpty();

        Touch(InstallLayout.MachineManifest(ProgramData, ProgramFiles, 2027));
        InstallLayout.FindShadows(AppData, ProgramData, ProgramFiles, new[] { 2027 }, File.Exists).Should().ContainSingle();
    }

    [Fact]
    public void Missing_roots_or_a_throwing_probe_find_nothing()
    {
        InstallLayout.FindShadows(null, ProgramData, ProgramFiles, InstallLayout.ShippedYears, _ => true).Should().BeEmpty();
        InstallLayout.FindShadows(AppData, "", ProgramFiles, InstallLayout.ShippedYears, _ => true).Should().BeEmpty();
        InstallLayout.FindShadows(AppData, ProgramData, ProgramFiles, InstallLayout.ShippedYears,
            _ => throw new UnauthorizedAccessException()).Should().BeEmpty();
    }

    [Fact]
    public void Scope_of_a_path_is_machine_under_Program_Files_RST_only()
    {
        InstallLayout.ScopeOf(Path.Combine(ProgramFiles, "RST", "R26", "app", "RST.Engine.dll"), ProgramFiles).Should().Be(RstInstallScope.Machine);
        InstallLayout.ScopeOf(Path.Combine(AppData, "RST", "R26", "app", "RST.Engine.dll"), ProgramFiles).Should().Be(RstInstallScope.User);
        InstallLayout.ScopeOf(Path.Combine(ProgramFiles, "RST-Other", "x.dll"), ProgramFiles).Should().Be(RstInstallScope.User);
        InstallLayout.ScopeOf(null, ProgramFiles).Should().Be(RstInstallScope.User);
    }

    [Fact]
    public void The_shadow_notice_shows_once_per_user_and_only_with_a_shadow()
    {
        var path = Path.Combine(_root, "tray_settings.json");
        var settings = TraySettings.Read(path);
        TrayShadowNotice.ShouldShow(settings, shadowCount: 0).Should().BeFalse();
        TrayShadowNotice.ShouldShow(settings, shadowCount: 1).Should().BeTrue();

        var at = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        TrayShadowNotice.MarkShown(at, path).Should().BeTrue();

        var read = TraySettings.Read(path);
        read.ShadowNoticeShownUtc.Should().Be(at);
        read.UpdateNoticeShownUtc.Should().BeNull("the two notices are recorded separately");
        TrayShadowNotice.ShouldShow(read, shadowCount: 2).Should().BeFalse();
        File.ReadAllText(path).Should().Contain("\"shadowNoticeShownUtc\"");
    }

    [Fact]
    public void Per_machine_tray_folder_is_under_Program_Files()
    {
        TrayPaths.PerMachineInstallDir.Should().EndWith(Path.Combine("RST", "Tray"));
    }
}
