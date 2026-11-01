// rst-c's own (not vendored). SC-018: inside Revit 2027 the Health tab's Journals, Collaboration Cache and Recent File
// List rows showed a 2025 or 2026 path. The scan fanned each {revit-version} target out across every "Autodesk Revit
// YYYY" folder, oldest first, summed them, and the row showed the first path. In Revit the rows now describe the
// running Revit's year only: its path, size and count. The tray's Device page (no running Revit) still measures every
// year (decision #56), and Clean Junk Files is unchanged. Over a fake user-folder tree with 2025, 2026 and 2027 folders.

using System.Globalization;
using System.IO;
using RST.Core.Health;
using RST.Core.Profiles;
using RST.RuntimeTests.Support;
using RST.UI.Health;
using Xunit;

namespace RST.RuntimeTests.Health;

public sealed class HealthRunningYearJunkTests : IDisposable
{
    private static readonly int[] Years = [2025, 2026, 2027];

    private readonly FakeUserFolders _user = new();

    public void Dispose() => _user.Dispose();

    /// <summary>Per year: (year − 2024) journals of (year − 2024) KB each, one cache file and as many recent files.</summary>
    private void Seed()
    {
        foreach (var year in Years)
        {
            var n = year - 2024;
            for (var i = 0; i < n; i++)
                File.WriteAllBytes(Path.Combine(_user.Journals(year), $"journal.{i:0000}.txt"), new byte[n * 1024]);
            File.WriteAllBytes(Path.Combine(_user.CollabCache(year), "cache.bin"), new byte[n * 10_000]);
            var entries = string.Concat(Enumerable.Range(1, n).Select(i => $"File{i}=C:\\m{i}.rvt\r\n"));
            File.WriteAllText(_user.RevitIni(year), "[Recent File List]\r\n" + entries, System.Text.Encoding.Unicode);
        }
    }

    private static CleanupTargetSummary Target(CleanupSummary summary, string id) => summary.Targets.Single(t => t.Id == id);

    private static string SubText(HealthRow row) => string.Concat(row.Subs[0].Select(r => r.Text));

    [Fact]
    public void Running_2027_measures_and_shows_the_2027_folder_only()
    {
        Seed();
        var summary = HealthScanner.MeasureCleanup(CleanupDefaults.Build(), revitYear: 2027, folders: _user.Folders);

        var journals = Target(summary, CleanupDefaults.IdJournals);
        Assert.Equal([_user.Journals(2027)], journals.ResolvedPaths);
        Assert.Equal(3, journals.FileCount);
        Assert.Equal(3 * 3 * 1024, journals.SizeBytes);

        var cache = Target(summary, CleanupDefaults.IdCollabCache);
        Assert.Equal([_user.CollabCache(2027)], cache.ResolvedPaths);
        Assert.Equal(1, cache.FileCount);
        Assert.Equal(30_000, cache.SizeBytes);

        var recent = Target(summary, CleanupDefaults.IdRecentFiles);
        Assert.Equal([_user.RevitIni(2027)], recent.ResolvedPaths);
        Assert.Equal(3, recent.FileCount);

        // The rows the Health tab draws name the 2027 folder, once, and no other year.
        var hardware = HealthReport.Build(new HealthSnapshot { Cleanup = summary }, CultureInfo.GetCultureInfo("en-US"), TimeZoneInfo.Utc)
            .Sections.Single(s => s.Title == "Hardware");
        foreach (var (label, path) in new[]
                 {
                     ("Journals", _user.Journals(2027)),
                     ("Collaboration Cache", _user.CollabCache(2027)),
                     ("Recent File List", _user.RevitIni(2027)),
                 })
        {
            var text = SubText(hardware.Rows.Single(r => r.Label == label));
            Assert.EndsWith(path, text, StringComparison.Ordinal);
            Assert.DoesNotContain("2025", text, StringComparison.Ordinal);
            Assert.DoesNotContain("2026", text, StringComparison.Ordinal);
            Assert.DoesNotContain("paths", text, StringComparison.Ordinal);
        }
        Assert.Equal(HealthFormat.Bytes(3 * 3 * 1024), hardware.Rows.Single(r => r.Label == "Journals").Value);
    }

    [Fact]
    public void Running_2026_measures_2026_whatever_newer_year_is_installed()
    {
        Seed();
        var summary = HealthScanner.MeasureCleanup(CleanupDefaults.Build(), revitYear: 2026, folders: _user.Folders);
        var journals = Target(summary, CleanupDefaults.IdJournals);
        Assert.Equal([_user.Journals(2026)], journals.ResolvedPaths);
        Assert.Equal(2, journals.FileCount);
        Assert.Equal(2 * 2 * 1024, journals.SizeBytes);
    }

    [Fact]
    public void A_running_year_with_no_folder_yet_shows_that_years_path_with_nothing_in_it()
    {
        // Only 2025 and 2026 folders: Revit 2027 has not written its own yet.
        File.WriteAllText(Path.Combine(_user.Journals(2025), "journal.0000.txt"), "j");
        File.WriteAllText(Path.Combine(_user.Journals(2026), "journal.0000.txt"), "j");
        var summary = HealthScanner.MeasureCleanup(CleanupDefaults.Build(), revitYear: 2027, folders: _user.Folders);
        var journals = Target(summary, CleanupDefaults.IdJournals);
        Assert.Equal([Path.Combine(_user.Folders.LocalAppData, "Autodesk", "Revit", "Autodesk Revit 2027", "Journals")],
            journals.ResolvedPaths);
        Assert.Equal(0, journals.FileCount);
        Assert.Equal(0, journals.SizeBytes);
    }

    [Fact]
    public void Without_a_running_year_the_tray_still_measures_every_year_oldest_first()
    {
        Seed();
        var summary = HealthScanner.MeasureCleanup(CleanupDefaults.Build(), folders: _user.Folders);
        var journals = Target(summary, CleanupDefaults.IdJournals);
        Assert.Equal(Years.Select(_user.Journals), journals.ResolvedPaths);
        Assert.Equal(1 + 2 + 3, journals.FileCount);
        Assert.Equal((1 + 4 + 9) * 1024, journals.SizeBytes);
        Assert.Equal(1 + 2 + 3, Target(summary, CleanupDefaults.IdRecentFiles).FileCount);
    }

    [Fact]
    public void Targets_that_are_not_year_specific_measure_the_same_with_or_without_a_year()
    {
        File.WriteAllBytes(Path.Combine(_user.Temp, "a.tmp"), new byte[700]);
        var withYear = Target(HealthScanner.MeasureCleanup(CleanupDefaults.Build(), 2027, _user.Folders), CleanupDefaults.IdTemp);
        var without = Target(HealthScanner.MeasureCleanup(CleanupDefaults.Build(), null, _user.Folders), CleanupDefaults.IdTemp);
        Assert.Equal([_user.Temp], withYear.ResolvedPaths);
        Assert.Equal(without.ResolvedPaths, withYear.ResolvedPaths);
        Assert.Equal((1, 700L), (withYear.FileCount, withYear.SizeBytes));
        Assert.Equal((without.FileCount, without.SizeBytes), (withYear.FileCount, withYear.SizeBytes));
    }

    [Fact]
    public void Clean_Junk_Files_in_Revit_still_empties_every_years_folder()
    {
        // The row shows 2027 only; the clean's scope is unchanged (every Autodesk Revit YYYY folder found).
        Seed();
        var journals = CleanupDefaults.Build().Single(t => t.Id == CleanupDefaults.IdJournals);
        var result = HealthCleaner.Run([new CleanupPick(journals, null, journals.Id)], _user.Folders);
        Assert.Equal(1 + 2 + 3, result.Deleted[journals.Id]);
        Assert.All(Years, y => Assert.Empty(Directory.GetFiles(_user.Journals(y))));
    }
}
