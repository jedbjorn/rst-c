// rst-c's own (not vendored): a fake user-folder tree (CleanupFolders) for the junk-file checks, so nothing touches the
// runner's own folders. Shared by the tray's Device page tests (Tray/TrayHealthTests, R25 only) and the add-in's
// running-year junk rows (Health/HealthRunningYearJunkTests, SC-018), so it lives here, built on every leg.

using System.IO;
using RST.Core.Health;
using RST.UI.Health;

namespace RST.RuntimeTests.Support;

/// <summary>A user's folders in a temp tree: <c>%LocalAppData%</c>, <c>%AppData%</c> and <c>%UserProfile%</c>.</summary>
internal sealed class FakeUserFolders : IDisposable
{
    private readonly TempFolder _root = new();

    public FakeUserFolders()
    {
        Folders = new CleanupFolders(
            System.IO.Path.Combine(_root.Path, "Local"),
            System.IO.Path.Combine(_root.Path, "Roaming"),
            System.IO.Path.Combine(_root.Path, "User"));
        Directory.CreateDirectory(Folders.LocalAppData);
        Directory.CreateDirectory(Folders.AppData);
        Directory.CreateDirectory(Folders.UserProfile);
    }

    public CleanupFolders Folders { get; }

    public string Journals(int year) => Dir(Folders.LocalAppData, "Autodesk", "Revit", CleanupYears.FolderName(year), "Journals");

    public string CollabCache(int year) => Dir(Folders.LocalAppData, "Autodesk", "Revit", CleanupYears.FolderName(year), "CollaborationCache");

    public string RevitIni(int year) => System.IO.Path.Combine(Dir(Folders.AppData, "Autodesk", "Revit", CleanupYears.FolderName(year)), "Revit.ini");

    public string Temp => Dir(Folders.LocalAppData, "Temp");

    /// <summary>Two Revit years with journals, a cache and a recent-file list each, and a temp file.</summary>
    public FakeUserFolders WithTwoYears()
    {
        foreach (var year in new[] { 2026, 2027 })
        {
            for (var i = 0; i < 3; i++) File.WriteAllText(System.IO.Path.Combine(Journals(year), $"journal.{i:0000}.txt"), "journal");
            File.WriteAllText(System.IO.Path.Combine(CollabCache(year), "cache.bin"), "cache");
            File.WriteAllText(RevitIni(year), "[Recent File List]\r\nFile1=C:\\a.rvt\r\n", System.Text.Encoding.Unicode);
        }
        File.WriteAllText(System.IO.Path.Combine(Temp, "tmp.txt"), "temp");
        return this;
    }

    public static string Dir(params string[] parts)
    {
        var path = System.IO.Path.Combine(parts);
        Directory.CreateDirectory(path);
        return path;
    }

    public void Dispose() => _root.Dispose();
}
