// rst-c's own (not vendored): the rules behind the tray's per-year Clean Junk Files rows (spec #25 Device; decision
// #56; AC7's listing), and the tray snapshot's installed Revit years (decision #59; AC6), which the add-in's
// health_scan.json never carries.

using System.Text.Json;
using RST.Core.Health;
using RST.Core.Profiles;
using Xunit;

namespace RST.Tests.Health;

public class CleanupYearsTests
{
    [Theory]
    [InlineData(@"%LocalAppData%\Autodesk\Revit\{revit-version}\Journals", true)]
    [InlineData(@"%AppData%\Autodesk\Revit\{REVIT-VERSION}\Revit.ini", true)]
    [InlineData(@"%LocalAppData%\Temp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Year_specific_paths_hold_the_fan_out_token(string? path, bool expected) =>
        Assert.Equal(expected, CleanupYears.IsYearSpecific(path));

    [Fact]
    public void Every_preset_that_fans_out_is_year_specific_and_the_others_are_not()
    {
        var specific = CleanupDefaults.Build().Where(t => CleanupYears.IsYearSpecific(t.Path)).Select(t => t.Id);
        Assert.Equal(new[] { CleanupDefaults.IdJournals, CleanupDefaults.IdCollabCache, CleanupDefaults.IdRecentFiles }, specific);
    }

    [Theory]
    [InlineData("Autodesk Revit 2026", 2026)]
    [InlineData("autodesk revit 2027", 2027)]
    [InlineData("Autodesk Revit 2019", 2019)]
    [InlineData("Autodesk Revit 2026 (old)", null)]
    [InlineData("Autodesk Revit 26", null)]
    [InlineData("Autodesk Revit ٢٠٢٦", null)] // non-ASCII digits are not a year
    [InlineData("PacCache", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_year_comes_from_an_Autodesk_Revit_folder_name_only(string? name, int? expected) =>
        Assert.Equal(expected, CleanupYears.YearOfFolder(name));

    [Fact]
    public void The_path_hint_names_that_years_folder()
    {
        Assert.Equal(@"%LocalAppData%\Autodesk\Revit\Autodesk Revit 2027\Journals",
            CleanupYears.DisplayPath(@"%LocalAppData%\Autodesk\Revit\{revit-version}\Journals", 2027));
        Assert.Equal(@"%AppData%\Autodesk\Revit\Autodesk Revit 2026\Revit.ini",
            CleanupYears.DisplayPath(@"%AppData%\Autodesk\Revit\{Revit-Version}\Revit.ini", 2026));
        Assert.Equal(@"%LocalAppData%\Temp", CleanupYears.DisplayPath(@"%LocalAppData%\Temp", 2026));
        Assert.Equal("", CleanupYears.DisplayPath(null, 2026));
    }

    [Fact]
    public void The_tray_snapshot_keeps_its_Revit_years_and_the_add_ins_snapshot_has_no_such_field()
    {
        var tray = new HealthSnapshot
        {
            RevitInstalls =
            [
                new RevitInstallInfo { Year = 2026, Build = "26.0.4.409", InstallDir = @"C:\Program Files\Autodesk\Revit 2026" },
                new RevitInstallInfo { Year = 2027, Build = "27.0.0.123", InstallDir = @"C:\Program Files\Autodesk\Revit 2027" },
            ],
        };
        var json = JsonSerializer.Serialize(tray);
        Assert.Contains("\"revitInstalls\"", json);
        var back = JsonSerializer.Deserialize<HealthSnapshot>(json)!;
        Assert.Equal(new[] { (2026, "26.0.4.409"), (2027, "27.0.0.123") }, back.RevitInstalls!.Select(r => (r.Year, r.Build)));
        Assert.Equal(@"C:\Program Files\Autodesk\Revit 2027", back.RevitInstalls![1].InstallDir);

        // The add-in's snapshot (no installs) is written exactly as before; an older file reads with none.
        Assert.DoesNotContain("revitInstalls", JsonSerializer.Serialize(new HealthSnapshot()));
        Assert.Null(JsonSerializer.Deserialize<HealthSnapshot>("""{"captureTimestamp":"2026-10-30T14:05:12Z"}""")!.RevitInstalls);
    }
}
