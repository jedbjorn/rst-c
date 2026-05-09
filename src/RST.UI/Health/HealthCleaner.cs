// HealthCleaner.cs — port of clean_junk from
// upstream RST/app/health_viewer.py.
//
// Five operations the viewer can invoke:
//   temp         — purge %LocalAppData%\Temp
//   pacCache     — purge %LocalAppData%\Autodesk\Revit\PacCache
//   journals     — purge Journals\ under every Revit YYYY install
//   collabCache  — purge CollaborationCache\ under every Revit YYYY install
//   recentFiles  — strip [Recent File List] FileN= entries from each
//                  Revit YYYY's Revit.ini under %AppData%\Autodesk\Revit\
//
// Locked files (in-use by a running Revit) skip individually — one
// locked file never aborts the rest.
//
// Encoding note: Revit.ini is UTF-16 LE with BOM on modern Revit. .NET's
// File.ReadAllBytes + StreamReader-with-BOM-detection handles that
// automatically; we preserve the original encoding on rewrite so Revit
// keeps parsing the file.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Serilog;

namespace RST.UI.Health;

/// <summary>Per-category counts returned by Run().</summary>
public sealed class CleanResult
{
    public Dictionary<string, int> Deleted { get; } = new()
    {
        ["temp"] = 0, ["pacCache"] = 0, ["journals"] = 0, ["collabCache"] = 0, ["recentFiles"] = 0,
    };
    public Dictionary<string, int> Skipped { get; } = new()
    {
        ["temp"] = 0, ["pacCache"] = 0, ["journals"] = 0, ["collabCache"] = 0, ["recentFiles"] = 0,
    };
}

/// <summary>Selectable cleanup categories.</summary>
public sealed class CleanCategories
{
    public bool Temp        { get; set; }
    public bool PacCache    { get; set; }
    public bool Journals    { get; set; }
    public bool CollabCache { get; set; }
    public bool RecentFiles { get; set; }
}

public static class HealthCleaner
{
    private static readonly Regex RevitAppDataDirRe = new(
        @"^Autodesk Revit \d{4}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RecentFileEntryRe = new(
        @"^\s*File\d+\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static CleanResult Run(CleanCategories cats)
    {
        if (cats is null) throw new ArgumentNullException(nameof(cats));
        Log.Information("HealthCleaner.Run: temp={T} pacCache={P} journals={J} collab={C} recent={R}",
                        cats.Temp, cats.PacCache, cats.Journals, cats.CollabCache, cats.RecentFiles);

        var result = new CleanResult();
        var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        var tempDir = Path.Combine(localAppData, "Temp");
        var pacDir = Path.Combine(localAppData, "Autodesk", "Revit", "PacCache");
        var revitLocalRoot = Path.Combine(localAppData, "Autodesk", "Revit");
        var revitRoaming = Path.Combine(roaming, "Autodesk", "Revit");

        if (cats.Temp)
        {
            var (d, s) = PurgeFlat(tempDir, "temp");
            result.Deleted["temp"] = d; result.Skipped["temp"] = s;
        }

        if (cats.PacCache)
        {
            var (d, s) = PurgeFlat(pacDir, "pacCache");
            result.Deleted["pacCache"] = d; result.Skipped["pacCache"] = s;
        }

        if (cats.Journals || cats.CollabCache)
        {
            foreach (var entry in SafeListDir(revitLocalRoot))
            {
                if (!RevitAppDataDirRe.IsMatch(entry)) continue;
                var vDir = Path.Combine(revitLocalRoot, entry);
                if (!Directory.Exists(vDir)) continue;
                if (cats.Journals)
                {
                    var (d, s) = PurgeFlat(Path.Combine(vDir, "Journals"), $"journals/{entry}");
                    result.Deleted["journals"] += d; result.Skipped["journals"] += s;
                }
                if (cats.CollabCache)
                {
                    var (d, s) = PurgeCollabCache(Path.Combine(vDir, "CollaborationCache"), $"collabCache/{entry}");
                    result.Deleted["collabCache"] += d; result.Skipped["collabCache"] += s;
                }
            }
        }

        if (cats.RecentFiles)
        {
            foreach (var entry in SafeListDir(revitRoaming))
            {
                if (!RevitAppDataDirRe.IsMatch(entry)) continue;
                var iniPath = Path.Combine(revitRoaming, entry, "Revit.ini");
                var (d, s) = PurgeRecentFileList(iniPath, $"recentFiles/{entry}");
                result.Deleted["recentFiles"] += d; result.Skipped["recentFiles"] += s;
            }
        }

        Log.Information("HealthCleaner.Run done: deleted={Deleted} skipped={Skipped}", result.Deleted, result.Skipped);
        return result;
    }

    // ── internal ───────────────────────────────────────────────────────

    private static IEnumerable<string> SafeListDir(string root)
    {
        if (!Directory.Exists(root))
        {
            Log.Information("HealthCleaner: missing {Root}", root);
            return Array.Empty<string>();
        }
        try
        {
            var names = new List<string>();
            foreach (var path in Directory.GetDirectories(root))
                names.Add(Path.GetFileName(path));
            return names;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "HealthCleaner.SafeListDir failed for {Root}", root);
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Walk <paramref name="path"/> recursively and try to delete every
    /// file. Locked files skip individually. Directories are left in place.
    /// </summary>
    public static (int deleted, int skipped) PurgeFlat(string path, string label)
    {
        if (!Directory.Exists(path))
        {
            Log.Information("[{Label}] missing, skipping: {Path}", label, path);
            return (0, 0);
        }
        int deleted = 0, skipped = 0;
        foreach (var file in EnumerateFilesSafe(path))
        {
            try
            {
                File.Delete(file);
                deleted++;
            }
            catch (IOException ex)
            {
                skipped++;
                Log.Debug("[{Label}] skipped {File}: {Msg}", label, Path.GetFileName(file), ex.Message);
            }
            catch (UnauthorizedAccessException ex)
            {
                skipped++;
                Log.Debug("[{Label}] skipped {File}: {Msg}", label, Path.GetFileName(file), ex.Message);
            }
        }
        Log.Information("[{Label}] {Path}: deleted={Deleted} skipped={Skipped}", label, path, deleted, skipped);
        return (deleted, skipped);
    }

    /// <summary>
    /// Same shape as PurgeFlat — separate method so future tweaks
    /// (date filters, GUID-targeted sweeps) don't have to fork.
    /// </summary>
    public static (int deleted, int skipped) PurgeCollabCache(string path, string label)
        => PurgeFlat(path, label);

    /// <summary>
    /// Strip <c>FileN=</c> entries inside <c>[Recent File List]</c> while
    /// preserving every other line and the section header. Atomic via
    /// temp + replace. Encoding is preserved across the rewrite.
    /// Returns (deleted, skipped) where skipped is 1 on any IO error
    /// (typically Revit running and holding the file).
    /// </summary>
    public static (int deleted, int skipped) PurgeRecentFileList(string iniPath, string label)
    {
        if (!File.Exists(iniPath))
        {
            Log.Information("[{Label}] ini missing, skipping: {Ini}", label, iniPath);
            return (0, 0);
        }

        byte[] data;
        try { data = File.ReadAllBytes(iniPath); }
        catch (IOException ex)
        {
            Log.Warning(ex, "[{Label}] could not read {Ini}", label, iniPath);
            return (0, 1);
        }

        var (text, encoding) = DecodeIniBytes(data);
        Log.Information("[{Label}] {Ini}: encoding={Encoding}", label, iniPath, encoding.WebName);

        var outBuilder = new StringBuilder(text.Length);
        bool inSection = false;
        bool sectionSeen = false;
        int deleted = 0;

        // Split keeping line endings so we round-trip the file faithfully.
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            int end = i + 1;
            string line = text.Substring(start, end - start);
            HandleLine(line);
            start = end;
        }
        if (start < text.Length) HandleLine(text.Substring(start));

        void HandleLine(string raw)
        {
            string stripped = raw.TrimEnd('\r', '\n').Trim();
            if (stripped.StartsWith("[", StringComparison.Ordinal) &&
                stripped.EndsWith("]", StringComparison.Ordinal))
            {
                inSection = string.Equals(stripped, "[Recent File List]", StringComparison.OrdinalIgnoreCase);
                if (inSection) sectionSeen = true;
                outBuilder.Append(raw);
                return;
            }
            if (inSection && RecentFileEntryRe.IsMatch(raw))
            {
                deleted++;
                return;
            }
            outBuilder.Append(raw);
        }

        if (deleted == 0)
        {
            Log.Information("[{Label}] {Ini}: nothing to remove (sectionMatched={SectionSeen})",
                            label, iniPath, sectionSeen);
            return (0, 0);
        }

        string tmp = iniPath + ".rsttmp";
        try
        {
            // Preserve BOM by writing through an explicit Encoding instance
            // chosen from DecodeIniBytes — UTF-16 LE writes its own FFFE
            // header, UTF-8 with BOM keeps the EFBBBF prefix.
            File.WriteAllText(tmp, outBuilder.ToString(), encoding);
            if (File.Exists(iniPath)) File.Delete(iniPath);
            File.Move(tmp, iniPath);
            Log.Information("[{Label}] {Ini}: deleted={Deleted}", label, iniPath, deleted);
            return (deleted, 0);
        }
        catch (IOException ex)
        {
            Log.Warning(ex, "[{Label}] could not write {Ini} (Revit running?)", label, iniPath);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* swallow */ }
            return (0, 1);
        }
        catch (UnauthorizedAccessException ex)
        {
            Log.Warning(ex, "[{Label}] denied writing {Ini}", label, iniPath);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* swallow */ }
            return (0, 1);
        }
    }

    /// <summary>
    /// Sniff the BOM at <paramref name="data"/>[0..3] and return decoded
    /// text + the matching write-back encoding. Mirrors the upstream
    /// _decode_ini_bytes helper.
    /// </summary>
    public static (string text, Encoding encoding) DecodeIniBytes(byte[] data)
    {
        if (data.Length >= 2 && data[0] == 0xFF && data[1] == 0xFE)
            return (Encoding.Unicode.GetString(data, 2, data.Length - 2), new UnicodeEncoding(bigEndian: false, byteOrderMark: true));
        if (data.Length >= 2 && data[0] == 0xFE && data[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(data, 2, data.Length - 2), new UnicodeEncoding(bigEndian: true, byteOrderMark: true));
        if (data.Length >= 3 && data[0] == 0xEF && data[1] == 0xBB && data[2] == 0xBF)
            return (Encoding.UTF8.GetString(data, 3, data.Length - 3), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        return (Encoding.UTF8.GetString(data), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static IEnumerable<string> EnumerateFilesSafe(string root)
    {
        // Manual stack so a single inaccessible subdir doesn't kill the walk.
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(current); }
            catch (Exception ex) { Log.Debug(ex, "EnumerateFilesSafe: skip {Dir}", current); continue; }
            foreach (var f in files) yield return f;

            string[] subs;
            try { subs = Directory.GetDirectories(current); }
            catch (Exception ex) { Log.Debug(ex, "EnumerateFilesSafe: subdir enum failed for {Dir}", current); continue; }
            foreach (var s in subs) stack.Push(s);
        }
    }
}
