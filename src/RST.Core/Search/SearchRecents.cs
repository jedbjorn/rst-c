// rst-c's own (not vendored): Search-It's recently used tools. Up to five chips under the count line, the tools
// Search-It itself ran most recently (never search text, never a ribbon or profile-button click), newest first; a
// click on one runs it as a click on its result card does.
//
// What is recorded: a tool, once SearchItCommand has dispatched it (PostCommand accepted it, or the ribbon's command
// path ran it); never a click that could not run. Re-running a tool moves it to the front; at most five are kept.
// The list is per Windows user and survives a restart of Revit: %AppData%\RST\search_it_recents.json, next to RST's
// other per-user state (AppDataPaths.Root). A missing file is no recents; an unreadable or invalid one is no recents
// and a Warning; a write that fails is a Warning and nothing else: running the tool never depends on it.
//
// What shows: the stored ids that are in the list the window loaded for the open file, and can run there (a Must
// Run In tool is hidden), in the stored order. An id the open file does not list is hidden, not forgotten.

using System.Text.Json;
using System.Text.Json.Serialization;
using RST.Core.Configuration;
using RST.Core.Logging;

namespace RST.Core.Search;

/// <summary>One tool Search-It ran: its command id, and the name it had then (for the log and a reader of the file).</summary>
public sealed record SearchRecent(string Id, string Name);

/// <summary>The recently used list's rules, on plain values.</summary>
public static class SearchRecents
{
    /// <summary>How many recent tools are kept, and shown.</summary>
    public const int Max = 5;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>
    /// <paramref name="used"/> first, then the rest of <paramref name="recents"/> without it (by id), at most
    /// <see cref="Max"/>. A blank id is never kept.
    /// </summary>
    public static IReadOnlyList<SearchRecent> Push(IReadOnlyList<SearchRecent>? recents, SearchRecent used)
    {
        ArgumentNullException.ThrowIfNull(used);
        return Normalize([used, .. (recents ?? Array.Empty<SearchRecent>())]);
    }

    /// <summary>
    /// The tools of <paramref name="tools"/> (the list the window loaded) that <paramref name="recents"/> names, in its
    /// order: ids not in the list, and tools that can't run here (Must Run In), are left out; at most <see cref="Max"/>.
    /// </summary>
    public static IReadOnlyList<SearchTool> Resolve(IReadOnlyList<SearchRecent>? recents, IEnumerable<SearchTool>? tools)
    {
        if (recents is null || recents.Count == 0 || tools is null) return Array.Empty<SearchTool>();
        var byId = new Dictionary<string, SearchTool>(StringComparer.Ordinal);
        foreach (var tool in tools) byId.TryAdd(tool.Id, tool);
        var shown = new List<SearchTool>(Max);
        foreach (var recent in Normalize(recents))
        {
            if (!byId.TryGetValue(recent.Id, out var tool) || !tool.CanRun) continue;
            shown.Add(tool);
            if (shown.Count == Max) break;
        }
        return shown;
    }

    /// <summary>The file's text for <paramref name="recents"/>.</summary>
    public static string Serialize(IReadOnlyList<SearchRecent>? recents) =>
        JsonSerializer.Serialize(new RecentsDto
        {
            Recent = Normalize(recents ?? Array.Empty<SearchRecent>()).Select(r => (RecentDto?)new RecentDto { Id = r.Id, Name = r.Name }).ToList(),
        }, JsonOptions);

    /// <summary>
    /// Reads the file's text. False, with no recents, when it is empty or not the file's shape (invalid JSON, no
    /// "recent" list); entries without an id are skipped, repeats dropped, and the list cut to <see cref="Max"/>.
    /// </summary>
    public static bool TryParse(string? text, out IReadOnlyList<SearchRecent> recents)
    {
        recents = Array.Empty<SearchRecent>();
        if (string.IsNullOrWhiteSpace(text)) return false;
        RecentsDto? dto;
        try
        {
            dto = JsonSerializer.Deserialize<RecentsDto>(text);
        }
        catch (JsonException)
        {
            return false;
        }
        if (dto?.Recent is not { } list) return false;
        recents = Normalize(list.Where(r => r is not null).Select(r => new SearchRecent(r!.Id ?? "", r.Name ?? "")).ToList());
        return true;
    }

    /// <summary>Blank ids out, the first of each id kept, at most <see cref="Max"/>.</summary>
    private static IReadOnlyList<SearchRecent> Normalize(IEnumerable<SearchRecent> recents)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var kept = new List<SearchRecent>(Max);
        foreach (var recent in recents)
        {
            if (recent is null || string.IsNullOrWhiteSpace(recent.Id) || !seen.Add(recent.Id)) continue;
            kept.Add(recent with { Name = recent.Name ?? "" });
            if (kept.Count == Max) break;
        }
        return kept;
    }

    private sealed class RecentsDto
    {
        [JsonPropertyName("recent")]
        public List<RecentDto?>? Recent { get; set; }
    }

    private sealed class RecentDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }
    }
}

/// <summary>The recently used list on disk (the file header): read when Search-It opens, written after a run.</summary>
public static class SearchRecentsStore
{
    /// <summary>%AppData%\RST\search_it_recents.json.</summary>
    public static string DefaultPath => Path.Combine(AppDataPaths.Root, "search_it_recents.json");

    /// <summary>The stored list, newest first. Never throws: a missing file is none; an unreadable or invalid one is none and a Warning.</summary>
    public static IReadOnlyList<SearchRecent> Read(string? path = null)
    {
        path ??= DefaultPath;
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch (FileNotFoundException)
        {
            return Array.Empty<SearchRecent>();
        }
        catch (DirectoryNotFoundException)
        {
            return Array.Empty<SearchRecent>();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Search-It: the recently used tools could not be read from {Path}; showing none", path);
            return Array.Empty<SearchRecent>();
        }
        if (SearchRecents.TryParse(text, out var recents)) return recents;
        Log.Warning("Search-It: the recently used tools file {Path} is not valid; showing none (it is rewritten on the next run)", path);
        return Array.Empty<SearchRecent>();
    }

    /// <summary>
    /// Puts <paramref name="tool"/> first in the stored list and writes it (temp file and move: a crash mid-write
    /// leaves the previous list). Never throws: a failure is a Warning, and false.
    /// </summary>
    public static bool Record(SearchTool tool, string? path = null)
    {
        path ??= DefaultPath;
        var tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            ArgumentNullException.ThrowIfNull(tool);
            var recents = SearchRecents.Push(Read(path), new SearchRecent(tool.Id, tool.DisplayName));
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(tmp, SearchRecents.Serialize(recents));
            File.Move(tmp, path, overwrite: true);
            Log.Debug("Search-It: {CommandId} recorded as recently used ({Count} kept)", tool.Id, recents.Count);
            return true;
        }
        catch (Exception ex)
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            Log.Warning(ex, "Search-It: could not record {CommandId} as recently used in {Path}", tool?.Id, path);
            return false;
        }
    }
}
