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
using RST.Core.Profiles;
using Serilog;

namespace RST.UI.Health;

/// <summary>
/// Per-target counts returned by Run(). Both dictionaries are keyed by
/// <see cref="CleanupTarget.Id"/> when the target carries one, falling
/// back to <see cref="CleanupTarget.Name"/> for legacy/handcrafted
/// entries that didn't get a stable id.
/// </summary>
public sealed class CleanResult
{
    public Dictionary<string, int> Deleted { get; } = new();
    public Dictionary<string, int> Skipped { get; } = new();
}

public static class HealthCleaner
{
    private static readonly Regex RecentFileEntryRe = new(
        @"^\s*File\d+\s*=", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Run the cleanup against <paramref name="selectedTargets"/>. Each
    /// target's <see cref="CleanupTarget.Path"/> is expanded via
    /// <see cref="CleanupPathResolver"/> to 0..N concrete paths and the
    /// kind-appropriate operation is applied to each. Counts roll up
    /// per target id. Disabled targets are skipped silently.
    /// </summary>
    public static CleanResult Run(IEnumerable<CleanupTarget> selectedTargets)
    {
        if (selectedTargets is null) throw new ArgumentNullException(nameof(selectedTargets));

        var result = new CleanResult();
        int targetCount = 0;
        foreach (var target in selectedTargets)
        {
            if (target is null || !target.Enabled) continue;
            // Cleanup is locked to the built-in preset paths. A profile is
            // importable/hand-editable (untrusted), so refuse to recursively
            // delete anything the profile points at that isn't a preset.
            if (!CleanupDefaults.IsPresetPath(target.Path))
            {
                Log.Warning("HealthCleaner: refusing non-preset target {Name} ({Path})", target.Name, target.Path);
                continue;
            }
            targetCount++;
            var key = string.IsNullOrEmpty(target.Id) ? target.Name : target.Id;
            result.Deleted[key] = 0;
            result.Skipped[key] = 0;

            var resolved = CleanupPathResolver.Resolve(target.Path);
            if (resolved.Count == 0)
            {
                Log.Information("HealthCleaner: target {Name} ({Path}) resolved to 0 paths — skipping",
                                target.Name, target.Path);
                continue;
            }

            foreach (var concrete in resolved)
            {
                var label = $"{target.Name}/{Path.GetFileName(concrete) ?? "?"}";
                (int d, int s) outcome = target.Kind switch
                {
                    CleanupTarget.KindIniRecentFiles => PurgeRecentFileList(concrete, label),
                    CleanupTarget.KindDirectory      => PurgeFlat(concrete, label),
                    _ => SkipUnknownKind(target, label),
                };
                result.Deleted[key] += outcome.d;
                result.Skipped[key] += outcome.s;
            }
        }

        Log.Information("HealthCleaner.Run done: targets={Count} deleted={Deleted} skipped={Skipped}",
                        targetCount, result.Deleted, result.Skipped);
        return result;
    }

    private static (int deleted, int skipped) SkipUnknownKind(CleanupTarget target, string label)
    {
        Log.Warning("HealthCleaner: target {Name} has unknown kind={Kind} — skipping {Label}",
                    target.Name, target.Kind, label);
        return (0, 0);
    }

    // ── internal ───────────────────────────────────────────────────────

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
            // Atomic swap: File.Replace keeps the original Revit.ini intact
            // until the rename completes, so a crash mid-operation can't leave
            // the user with no Revit.ini (and thus lose every Revit preference).
            File.Replace(tmp, iniPath, destinationBackupFileName: null);
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

    /// <summary>
    /// True when <paramref name="dir"/> is a junction/symlink (reparse point).
    /// Returns true on error so an unreadable entry is treated as "don't
    /// descend" rather than risking a follow into an unknown target.
    /// </summary>
    internal static bool IsReparsePoint(string dir)
    {
        try { return (File.GetAttributes(dir) & FileAttributes.ReparsePoint) != 0; }
        catch { return true; }
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
            foreach (var s in subs)
            {
                // Never descend a junction/symlink — following a reparse point
                // would delete files in the link's *target* tree, well outside
                // the directory the user asked to clean.
                if (IsReparsePoint(s)) { Log.Debug("EnumerateFilesSafe: skip reparse point {Dir}", s); continue; }
                stack.Push(s);
            }
        }
    }
}
