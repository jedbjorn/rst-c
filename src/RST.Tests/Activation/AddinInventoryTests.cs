// rst-c's own (not vendored): AC1 of spec #18 (feature #16, T-B1). The inventory over a fake
// machine: per-user, machine (ProgramData up to 2026, Program Files from 2027), user and machine
// bundles with PackageContents.xml year ranges, Revit's own install folder, the user's settings
// file and the admin's lock file. One row per add-in per year; no duplicates; locks read.

using System.IO;
using FluentAssertions;
using RST.Core.Activation;
using Xunit;

namespace RST.Tests.Activation;

public sealed class AddinInventoryTests : IDisposable
{
    private const string Kinship = "a47e7259-a459-4fdd-8435-fb0bc0f86268";
    private const string DiRoots = "b4ad7489-2899-46b3-aca6-c0def7135528";
    private const string AlignTag = "5a9e8c41-09d6-4cdf-8556-38f8119137a4";
    private const string BatchPrint = "9d508589-48b6-4ff6-a50d-d16d77c585a1";

    private readonly FakeAddinTree _tree = new();

    public void Dispose() => _tree.Dispose();

    /// <summary>The machine the spike found (2026, 2027) plus a 2025, treated exactly like 2026 (decision #21).</summary>
    private AddinInventory BuildMachine()
    {
        foreach (var y in new[] { 2025, 2026, 2027 })
            _tree.Manifest(_tree.UserAddins(y), "Kinship.addin", "Kinship", Kinship, "ANDK");
        _tree.Manifest(_tree.ProgramDataAddins(2026), "DiRoots.One.addin", "DiRoots.One", DiRoots, "www.diroots.com");
        _tree.Manifest(_tree.MachineAddins(2027), "DiRoots.One.addin", "DiRoots.One", DiRoots, "www.diroots.com");
        // Revit 2027 ignores %ProgramData%\…\Addins\2027 (the spike's journal says so).
        _tree.Manifest(_tree.ProgramDataAddins(2027), "SpikeProbePD.addin", "SpikeProbePD", "5b1ce000-0000-4000-8000-000000000127");
        // Revit reads only the top level of an Addins folder.
        _tree.Manifest(Path.Combine(_tree.UserAddins(2026), "nested"), "Nested.addin", "Nested", "5b1ce000-0000-4000-8000-000000000999");
        _tree.Manifest(_tree.ProgramDataAddins(2026), "Autodesk.BatchPrint.addin", "Batch Print", BatchPrint, "ADSK");
        _tree.Manifest(Path.Combine(_tree.RevitInstallAddins(2026), "AddInsManager"), "Autodesk.AddInsManager.addin", "Add-Ins Manager", "19b8b66a-0000-4000-8000-000000000001", "ADSK");

        // BIM 42 Align: one user bundle, a component per year (SC-006 showed it seven times).
        _tree.Bundle(_tree.UserPlugins, "BIM 42 Align",
            new FakeAddinTree.Component("2025", 2025, 2025, "AlignTag.addin", "Application AlignTag", AlignTag),
            new FakeAddinTree.Component("2026", 2026, 2026, "AlignTag.addin", "Application AlignTag", AlignTag),
            new FakeAddinTree.Component("2027", 2027, 2027, "AlignTag.addin", "Application AlignTag", AlignTag));
        // A machine bundle for 2025–2026 in ProgramData, and one for 2027+ in Program Files.
        _tree.Bundle(_tree.ProgramDataPlugins, "OldTools", new FakeAddinTree.Component("Win", 2025, 2026, "OldTools.addin", "Old Tools", "11111111-0000-4000-8000-000000000001"));
        _tree.Bundle(_tree.ProgramDataPlugins, "Ignored2027", new FakeAddinTree.Component("Win", 2027, null, "Ignored.addin", "Ignored", "11111111-0000-4000-8000-000000000002"));
        _tree.Bundle(_tree.ProgramFilesPlugins, "NewTools", new FakeAddinTree.Component("Win", 2027, null, "NewTools.addin", "New Tools", "11111111-0000-4000-8000-000000000003"));
        // A folder without PackageContents.xml is not a bundle Revit loads.
        _tree.Manifest(Path.Combine(_tree.UserPlugins, "Loose.bundle", "Contents"), "Loose.addin", "Loose", "11111111-0000-4000-8000-000000000004");

        _tree.Settings(2026, "{\"DisableAllAddIns\":false,\"AddInItemSettings\":[{\"Disabled\":true,\"GUID\":\"" + Kinship + "\",\"LoadTime\":10,\"Name\":\"Kinship\",\"Vendor\":\"ANDK\"}]}");
        _tree.Lock(2027, Path.Combine(_tree.MachineAddins(2027), "DiRoots.One.addin"), DiRoots, AdminLock.AlwaysDisabled);

        return _tree.Scan(FakeAddinTree.Year(2025), FakeAddinTree.Year(2026), FakeAddinTree.Year(2027));
    }

    [Fact]
    public void Each_year_gets_exactly_the_add_ins_revit_reads_one_row_each()
    {
        var inventory = BuildMachine();
        string[] Names(int y) => inventory.For(y).Select(i => i.Name).Order(StringComparer.Ordinal).ToArray();

        Names(2025).Should().Equal("Application AlignTag", "Kinship", "Old Tools");
        Names(2026).Should().Equal("Add-Ins Manager", "Application AlignTag", "Batch Print", "DiRoots.One", "Kinship", "Old Tools");
        Names(2027).Should().Equal("Application AlignTag", "DiRoots.One", "Kinship", "New Tools");
        inventory.Items.GroupBy(i => (i.Year, i.ManifestPath)).Should().OnlyContain(g => g.Count() == 1, "no duplicates");
    }

    [Fact]
    public void Scopes_and_the_2027_program_files_paths_are_right()
    {
        var inventory = BuildMachine();
        InventoryItem Row(int y, string name) => inventory.For(y).Single(i => i.Name == name);

        Row(2026, "Kinship").Scope.Should().Be(AddinScope.User);
        Row(2026, "DiRoots.One").Scope.Should().Be(AddinScope.Machine);
        Row(2026, "DiRoots.One").ManifestPath.Should().StartWith(_tree.Roots.ProgramData);
        Row(2027, "DiRoots.One").Scope.Should().Be(AddinScope.Machine);
        Row(2027, "DiRoots.One").ManifestPath.Should().Be(Path.Combine(_tree.Roots.ProgramFiles, "Autodesk", "Revit", "Addins", "2027", "DiRoots.One.addin"));
        Row(2027, "New Tools").Scope.Should().Be(AddinScope.Machine);
        Row(2026, "Add-Ins Manager").Scope.Should().Be(AddinScope.RevitInstall);
        Row(2026, "Batch Print").IsAutodesk.Should().BeTrue();
        Row(2026, "Kinship").AddinIds.Should().Equal(Kinship);
        Row(2026, "Kinship").VendorId.Should().Be("ANDK");
    }

    [Fact]
    public void Bundle_components_count_only_in_their_years_with_their_range()
    {
        var inventory = BuildMachine();
        var align = inventory.Items.Where(i => i.Name == "Application AlignTag").ToList();
        align.Select(i => i.Year).Should().Equal(2025, 2026, 2027);
        align.Should().OnlyContain(i => i.BundlePath!.EndsWith("BIM 42 Align.bundle") && i.Scope == AddinScope.User);
        align.Single(i => i.Year == 2026).ManifestPath.Should().EndWith(Path.Combine("Contents", "2026", "AlignTag.addin"));
        align.Single(i => i.Year == 2026).Bundle!.Range.Should().Be("2026");
        inventory.For(2026).Single(i => i.Name == "Old Tools").Bundle!.Range.Should().Be("2025–2026");
        inventory.For(2027).Single(i => i.Name == "New Tools").Bundle!.Range.Should().Be("2027+");
    }

    [Fact]
    public void The_settings_file_and_the_admin_lock_give_each_row_its_state()
    {
        var inventory = BuildMachine();
        var kinship2026 = inventory.For(2026).Single(i => i.Name == "Kinship");
        kinship2026.SwitchedOffInSettings.Should().BeTrue();
        kinship2026.IsOff.Should().BeTrue();
        inventory.For(2027).Single(i => i.Name == "Kinship").IsOff.Should().BeFalse("each year has its own file");

        var diroots2027 = inventory.For(2027).Single(i => i.Name == "DiRoots.One");
        diroots2027.Lock.Should().Be(AdminLock.AlwaysDisabled);
        diroots2027.IsOff.Should().BeTrue();
        inventory.For(2026).Single(i => i.Name == "DiRoots.One").Lock.Should().Be(AdminLock.None);

        inventory.Year(2026)!.Settings.Should().Be(SettingsFileState.Ok);
        inventory.Year(2027)!.Settings.Should().Be(SettingsFileState.Missing);
        inventory.Year(2027)!.AdminPath.Should().Be(Path.Combine(_tree.Roots.ProgramFiles, "Autodesk", "Revit", "Admin", "2027", "AdminSettings.json"));
        inventory.Year(2026)!.AdminPath.Should().Be(Path.Combine(_tree.Roots.ProgramData, "Autodesk", "RVT 2026", "Admin", "AdminSettings.json"));
        inventory.Year(2026)!.SettingsPath.Should().Be(Path.Combine(_tree.Roots.AppData, "Autodesk", "Revit", "Autodesk Revit 2026", "AddinsData", "AddInsSettings.json"));
    }

    [Fact]
    public void A_lock_on_another_manifest_path_does_not_apply()
    {
        _tree.Manifest(_tree.UserAddins(2026), "DiRoots.One.addin", "DiRoots.One", DiRoots);
        _tree.Lock(2026, @"C:\Elsewhere\DiRoots.One.addin", DiRoots, AdminLock.AlwaysDisabled);
        _tree.Scan(2026).Items.Single().Lock.Should().Be(AdminLock.None);
    }

    [Fact]
    public void A_v3_renamed_manifest_is_one_off_row_and_a_live_copy_wins_over_a_stale_renamed_one()
    {
        _tree.Manifest(_tree.UserAddins(2026), "Twin.addin", "Twin", "22222222-0000-4000-8000-000000000001", renamed: true);
        _tree.Manifest(_tree.UserAddins(2026), "Both.addin", "Both", "22222222-0000-4000-8000-000000000002");
        _tree.Manifest(_tree.UserAddins(2026), "Both.addin", "Both", "22222222-0000-4000-8000-000000000002", renamed: true);
        var items = _tree.Scan(2026).Items;

        var twin = items.Single(i => i.Name == "Twin");
        twin.Renamed.Should().BeTrue();
        twin.IsOff.Should().BeTrue();
        twin.FileName.Should().Be("Twin.addin");
        twin.CurrentPath.Should().EndWith("Twin.addin.RSTdisabled");

        var both = items.Single(i => i.Name == "Both");
        both.Renamed.Should().BeFalse();
        both.StaleRenamedCopy.Should().BeTrue();
        both.IsOff.Should().BeFalse();
    }

    [Fact]
    public void An_unreadable_settings_file_is_reported_and_an_upper_case_guid_still_matches()
    {
        _tree.Manifest(_tree.UserAddins(2026), "DiRoots.One.addin", "DiRoots.One", DiRoots);
        _tree.Settings(2026, FakeAddinTree.Fixture("s9-input-seed-corrupt-2026.json"));
        var corrupt = _tree.Scan(2026);
        corrupt.Year(2026)!.Settings.Should().Be(SettingsFileState.Unreadable);
        corrupt.Year(2026)!.SettingsError.Should().NotBeNullOrEmpty();

        _tree.Manifest(_tree.UserAddins(2027), "AlignTag.addin", "Application AlignTag", AlignTag);
        _tree.Settings(2027, FakeAddinTree.Fixture("s8-input-seed-uppercase-2026.json"));
        _tree.Scan(2027).Items.Single().SwitchedOffInSettings.Should().BeTrue();
    }

    [Fact]
    public void Revit_2025_reads_its_settings_file_like_2026_and_years_before_2025_are_ignored()
    {
        // Decision #21: 2025 is treated exactly like 2026; 2024 is not RST's to switch.
        _tree.Manifest(_tree.UserAddins(2025), "Kinship.addin", "Kinship", Kinship);
        _tree.Settings(2025, "{\"DisableAllAddIns\":false,\"AddInItemSettings\":[{\"Disabled\":true,\"GUID\":\"" + Kinship + "\"}]}");
        _tree.Manifest(_tree.UserAddins(2024), "Kinship.addin", "Kinship", Kinship);
        var inventory = _tree.Scan(FakeAddinTree.Year(2024), FakeAddinTree.Year(2025));
        inventory.Years.Select(y => y.Year).Should().Equal(2025);
        inventory.Items.Single().Should().Match<InventoryItem>(i => i.Year == 2025 && i.IsOff && i.SwitchedOffInSettings);
        inventory.Year(2025)!.Settings.Should().Be(SettingsFileState.Ok);

        _tree.Settings(2026, "{\"DisableAllAddIns\":true,\"AddInItemSettings\":[]}");
        _tree.Scan(2026).Year(2026)!.UserDisableAll.Should().BeTrue();
    }

    [Fact]
    public void A_manifest_or_bundle_that_cannot_be_read_is_skipped_reported_and_remembered()
    {
        Directory.CreateDirectory(_tree.UserAddins(2026));
        File.WriteAllText(Path.Combine(_tree.UserAddins(2026), "Broken.addin"), "<RevitAddIns><AddIn>");
        var bundle = Path.Combine(_tree.UserPlugins, "Bad.bundle");
        Directory.CreateDirectory(bundle);
        File.WriteAllText(Path.Combine(bundle, "PackageContents.xml"), "not xml");
        var skipped = new List<string>();
        var inventory = AddinInventory.Scan(_tree.Roots, [FakeAddinTree.Year(2026)], (p, _) => skipped.Add(Path.GetFileName(p)));
        inventory.Items.Should().BeEmpty();
        skipped.Should().BeEquivalentTo("Broken.addin", "PackageContents.xml");
        // Still installed as far as RST knows (review N5).
        inventory.Year(2026)!.IsUnreadableManifest(Path.Combine(_tree.UserAddins(2026), "Broken.addin")).Should().BeTrue();
    }
}

public sealed class RevitInstallationsTests
{
    private sealed class Probe : IRevitInstallProbe
    {
        public Dictionary<int, string> Registry { get; } = new();

        public HashSet<string> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? InstallLocation(int year) => Registry.GetValueOrDefault(year);

        public bool FileExists(string path) => Files.Contains(path);
    }

    [Fact]
    public void Installed_supported_years_are_found_by_the_uninstall_entry_or_the_default_folder()
    {
        var roots = new AddinRoots("/a", "/pd", "/pf");
        var probe = new Probe();
        // 2024 is installed but not supported (decision #21).
        probe.Files.Add(Path.Combine(roots.DefaultRevitInstall(2024), "Revit.exe"));
        // 2025 in a custom folder (registry).
        probe.Registry[2025] = "/custom/Revit 2025";
        probe.Files.Add(Path.Combine("/custom/Revit 2025", "Revit.exe"));
        // 2026 at the default folder.
        probe.Files.Add(Path.Combine(roots.DefaultRevitInstall(2026), "Revit.exe"));
        // 2027 is not installed (a stale registry entry without Revit.exe).
        probe.Registry[2027] = "/gone";

        var years = RevitInstallations.Detect(roots, probe);

        years.Select(y => y.Year).Should().Equal(2025, 2026);
        years[0].InstallDir.Should().Be("/custom/Revit 2025");
        RevitInstallations.MinimumYear.Should().Be(2025);
    }
}
