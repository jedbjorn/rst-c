// rst-c's own (not vendored): the Builder's Custom Tools list (feature #18, spec #19, decision #20).
// The list is derived, not stored: every tool slot whose commandId starts "URL:" in every profile
// in the profiles folder (for the open profile, its working copy instead of its file), loose or in
// a stack. Identity is the normalised link; the label and icon are the BaseName (or Name) and icon
// of the first occurrence, ordered by profile name; the kind is EMAIL for mailto:, URL otherwise;
// "Used in" is the names of the profiles that hold it. Edit everywhere and Remove everywhere apply
// to every saved profile file that holds the link, each validated and written through
// ProfileSaveService, and to the working copy in memory. A partial failure keeps what was written
// and reports (and logs) each profile that failed; nothing is skipped silently.

using RST.Core.Loader;
using RST.Core.Profiles;

namespace RST.Core.Builder;

/// <summary>A custom tool's kind tag.</summary>
public enum CustomToolKind
{
    Url,
    Email,
}

/// <summary>One entry of the Custom Tools list.</summary>
/// <param name="Link">The normalised link (the identity).</param>
/// <param name="Label">The BaseName (or Name) of the first occurrence, by profile name.</param>
/// <param name="IconFile">The icon of that first occurrence.</param>
/// <param name="Kind">EMAIL for a mailto: link, URL otherwise.</param>
/// <param name="UsedIn">The names of the profiles that hold it, by profile name.</param>
public sealed record CustomTool(string Link, string Label, string? IconFile, CustomToolKind Kind, IReadOnlyList<string> UsedIn);

/// <summary>What the Create / Edit dialog asks for: the label, the address as typed, and the icon.</summary>
public sealed record CustomToolEdit(string Label, string Address, string? IconFile);

/// <summary>A profile file an edit or remove wrote.</summary>
public sealed record CustomToolWrite(string ProfileName, string? ProfileId, string FileName);

/// <summary>A profile an edit or remove could not write, and why.</summary>
public sealed record CustomToolFailure(string ProfileName, string? ProfileId, string FileName, string Error);

/// <summary>
/// The outcome of an edit or remove everywhere: the files written, the ones that failed, whether the
/// working copy changed, and whether the loaded (active) profile's file was written (the ribbon then
/// reloads).
/// </summary>
public sealed record CustomToolUpdate(
    IReadOnlyList<CustomToolWrite> Written,
    IReadOnlyList<CustomToolFailure> Failed,
    bool WorkingCopyChanged,
    bool ActiveProfileChanged)
{
    public bool Ok => Failed.Count == 0;
}

public static class CustomTools
{
    /// <summary>The note above the list.</summary>
    public const string ListNote = "URL and email tools found in your profiles. Drag onto a panel.";

    /// <summary>True for a URL or email tool slot (<c>commandId</c> starting <c>URL:</c>).</summary>
    public static bool IsCustom(Slot? slot) =>
        PanelStacks.IsTool(slot) && slot!.CommandId is { } id && id.StartsWith(BuilderRules.UrlCommandPrefix, StringComparison.Ordinal);

    /// <summary>A custom tool slot's normalised link ("" for any other slot).</summary>
    public static string LinkOf(Slot? slot) =>
        IsCustom(slot) ? BuilderRules.NormalizeUrl(slot!.CommandId![BuilderRules.UrlCommandPrefix.Length..]) : "";

    /// <summary>EMAIL for a mailto: link, URL otherwise.</summary>
    public static CustomToolKind KindOf(string? link) =>
        (link ?? "").StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? CustomToolKind.Email : CustomToolKind.Url;

    /// <summary>The dialog's "Opens: …" line: the address normalised ("" while it is blank).</summary>
    public static string Opens(string? address) => BuilderRules.NormalizeUrl(address);

    /// <summary>The dialog's checks: a label, then an address. Null when both are given.</summary>
    public static string? Check(CustomToolEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        if (string.IsNullOrWhiteSpace(edit.Label)) return "Label required";
        if (BuilderRules.NormalizeUrl(edit.Address).Length == 0) return "Address required";
        return null;
    }

    /// <summary>The tool slot Create adds: <c>URL:&lt;normalised link&gt;</c>, named and base-named by the label.</summary>
    public static Slot NewSlot(CustomToolEdit edit)
    {
        ArgumentNullException.ThrowIfNull(edit);
        var label = edit.Label.Trim();
        return new Slot
        {
            SlotType = PanelStacks.ToolType,
            Name = label,
            BaseName = label,
            CommandId = BuilderRules.UrlCommandPrefix + BuilderRules.NormalizeUrl(edit.Address),
            IconFile = string.IsNullOrEmpty(edit.IconFile) ? null : edit.IconFile,
        };
    }

    /// <summary>
    /// Create: the tool goes at the end of the selected panel, or the first panel when none is
    /// selected. Returns the panel it went to; null (refused) when the profile has no panels.
    /// </summary>
    public static int? AddToPanel(Profile profile, int? selectedPanel, Slot tool)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tool);
        if (profile.Panels.Count == 0) return null;
        var panel = selectedPanel is { } i && i >= 0 && i < profile.Panels.Count ? i : 0;
        profile.Panels[panel].Slots.Add(tool);
        return panel;
    }

    /// <summary>
    /// The profiles the list is built from: the saved ones, with the one whose id is the working
    /// copy's replaced by the working copy (an unsaved working copy is added), ordered by profile
    /// name (case-insensitive, then ordinal, then id).
    /// </summary>
    public static IReadOnlyList<Profile> Sources(IEnumerable<Profile> saved, Profile? working)
    {
        ArgumentNullException.ThrowIfNull(saved);
        var list = saved.Where(p => p is not null && !(working is not null && !string.IsNullOrEmpty(working.Id) && p.Id == working.Id)).ToList();
        if (working is not null) list.Add(working);
        return list
            .OrderBy(p => p.ProfileName ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.ProfileName ?? "", StringComparer.Ordinal)
            .ThenBy(p => p.Id ?? "", StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The Custom Tools list (decision #20) from <paramref name="saved"/> and the open profile's
    /// <paramref name="working"/> copy, ordered by label (case-insensitive), then link.
    /// </summary>
    public static IReadOnlyList<CustomTool> Derive(IEnumerable<Profile> saved, Profile? working)
    {
        var byLink = new Dictionary<string, (Slot First, List<string> UsedIn)>(StringComparer.Ordinal);
        foreach (var profile in Sources(saved, working))
        {
            foreach (var slot in CustomSlots(profile))
            {
                var link = LinkOf(slot);
                if (link.Length == 0) continue;
                if (!byLink.TryGetValue(link, out var entry)) byLink[link] = entry = (slot, []);
                var name = profile.ProfileName ?? "";
                if (!entry.UsedIn.Contains(name)) entry.UsedIn.Add(name);
            }
        }
        return byLink
            .Select(kv => new CustomTool(kv.Key, LabelOf(kv.Value.First), kv.Value.First.IconFile, KindOf(kv.Key), kv.Value.UsedIn))
            .OrderBy(t => t.Label, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Link, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>True when the profile holds a tool with that normalised link (loose or in a stack).</summary>
    public static bool Holds(Profile profile, string link) => CustomSlots(profile).Any(s => LinkOf(s) == link);

    /// <summary>
    /// Edit, in one profile: every slot with the tool's link gets the new link, BaseName and icon;
    /// a slot's Name changes only where it still equals its old base name (its BaseName, or the
    /// tool's old label when it has none), so names given on a panel are kept. Returns how many
    /// slots changed.
    /// </summary>
    public static int ApplyEdit(Profile profile, CustomTool tool, CustomToolEdit edit)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(edit);
        var label = edit.Label.Trim();
        var commandId = BuilderRules.UrlCommandPrefix + BuilderRules.NormalizeUrl(edit.Address);
        var icon = string.IsNullOrEmpty(edit.IconFile) ? null : edit.IconFile;
        var changed = 0;
        foreach (var slot in CustomSlots(profile).Where(s => LinkOf(s) == tool.Link).ToList())
        {
            var oldBase = string.IsNullOrEmpty(slot.BaseName) ? tool.Label : slot.BaseName;
            if (string.IsNullOrEmpty(slot.Name) || slot.Name == oldBase) slot.Name = label;
            slot.BaseName = label;
            slot.CommandId = commandId;
            slot.IconFile = icon;
            changed++;
        }
        return changed;
    }

    /// <summary>
    /// Remove, in one profile: every slot with the link goes, loose or in a stack; a stack left
    /// with one tool becomes a plain tool (<see cref="PanelStacks.RemoveTools"/>). Returns how many
    /// slots went.
    /// </summary>
    public static int ApplyRemove(Profile profile, string link)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return PanelStacks.RemoveTools(profile, s => LinkOf(s) == link);
    }

    /// <summary>A slot's label for the list: its BaseName, or its Name when it has none.</summary>
    public static string LabelOf(Slot slot) => string.IsNullOrEmpty(slot.BaseName) ? slot.Name ?? "" : slot.BaseName;

    /// <summary>The profile's custom tool slots in panel order, a stack's tools in its place (each stack once).</summary>
    private static IEnumerable<Slot> CustomSlots(Profile profile)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var panel in profile.Panels ?? [])
            foreach (var slot in panel.Slots ?? [])
            {
                if (IsCustom(slot)) yield return slot;
                else if (PanelStacks.IsStack(slot) && seen.Add(slot.Name ?? ""))
                    foreach (var tool in PanelStacks.ToolsOf(profile, slot).Where(IsCustom))
                        yield return tool;
            }
    }
}

/// <summary>
/// Edit everywhere and Remove everywhere over the profiles folder (decision #20): each saved profile
/// that holds the link is changed, validated and written through <see cref="ProfileSaveService"/>
/// in profile-name order, and the working copy is changed in memory. A failure does not stop the
/// rest: the files written stay written, and each failure is logged and reported.
/// </summary>
public sealed class CustomToolEditor
{
    private readonly string _profilesDir;
    private readonly ProfileSaveService _saver;
    private readonly ILoaderLog _log;

    /// <param name="profilesDir">%AppData%\RST\profiles (or a test folder).</param>
    /// <param name="saver">The Builder's save service (writes into the same folder).</param>
    /// <param name="log">Log lines; null drops them.</param>
    public CustomToolEditor(string profilesDir, ProfileSaveService saver, ILoaderLog? log = null)
    {
        _profilesDir = profilesDir ?? throw new ArgumentNullException(nameof(profilesDir));
        _saver = saver ?? throw new ArgumentNullException(nameof(saver));
        _log = log ?? NullLoaderLog.Instance;
    }

    /// <summary>The saved profiles (files that cannot be read are logged and left out, as everywhere in the Builder).</summary>
    public IReadOnlyList<ProfileEntry> Saved() =>
        ProfileStore.List(_profilesDir, (path, ex) => _log.Warning(ex, "Builder.custom_tools: unreadable profile {Path} left out", path));

    /// <summary>The Custom Tools list from the profiles folder and the working copy.</summary>
    public IReadOnlyList<CustomTool> Derive(Profile? working) => CustomTools.Derive(Saved().Select(e => e.Profile), working);

    /// <summary>
    /// The names the confirmation lists ("Update in N profiles", "Remove from N profiles"): every
    /// saved profile that holds the link (by name), and the working copy when it holds it.
    /// </summary>
    public IReadOnlyList<string> Affected(string link, Profile? working)
    {
        var saved = Saved().Select(e => e.Profile).Where(p => CustomTools.Holds(p, link)).ToList();
        var names = CustomTools.Sources(saved, working is not null && CustomTools.Holds(working, link) ? working : null)
            .Select(p => p.ProfileName ?? "");
        return names.Distinct().ToList();
    }

    /// <summary>Edit everywhere: the new label, link and icon for <paramref name="tool"/>.</summary>
    public CustomToolUpdate Edit(CustomTool tool, CustomToolEdit edit, Profile? working, string? activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(edit);
        if (CustomTools.Check(edit) is { } error) throw new ArgumentException(error, nameof(edit));
        _log.Information("Builder.custom_tools: edit {Link} → {NewLink} ({Label})", tool.Link, CustomTools.Opens(edit.Address), edit.Label);
        return Apply(tool.Link, p => CustomTools.ApplyEdit(p, tool, edit) > 0, working, activeProfileId, "edit");
    }

    /// <summary>Remove everywhere: every slot with the link, from every saved profile and the working copy.</summary>
    public CustomToolUpdate Remove(string link, Profile? working, string? activeProfileId)
    {
        ArgumentNullException.ThrowIfNull(link);
        _log.Information("Builder.custom_tools: remove {Link}", link);
        return Apply(link, p => CustomTools.ApplyRemove(p, link) > 0, working, activeProfileId, "remove");
    }

    private CustomToolUpdate Apply(string link, Func<Profile, bool> change, Profile? working, string? activeId, string verb)
    {
        var written = new List<CustomToolWrite>();
        var failed = new List<CustomToolFailure>();
        var holders = Saved()
            .Where(e => CustomTools.Holds(e.Profile, link))
            .OrderBy(e => e.Profile.ProfileName ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.FileName, StringComparer.Ordinal);
        foreach (var entry in holders)
        {
            var profile = entry.Profile;
            var name = profile.ProfileName ?? "";
            if (!change(profile)) continue;
            SaveOutcome outcome;
            try
            {
                outcome = _saver.Save(profile);
            }
            catch (Exception ex)
            {
                _log.Error(ex, "Builder.custom_tools: {Verb} failed for {Name} ({File})", verb, name, entry.FileName);
                failed.Add(new CustomToolFailure(name, profile.Id, entry.FileName, ex.Message));
                continue;
            }
            if (outcome.Ok)
            {
                written.Add(new CustomToolWrite(name, outcome.Saved?.Id ?? profile.Id, outcome.FileName ?? entry.FileName));
                continue;
            }
            _log.Warning(null, "Builder.custom_tools: {Verb} not written for {Name} ({File}): {Error}", verb, name, entry.FileName, outcome.Error);
            failed.Add(new CustomToolFailure(name, profile.Id, entry.FileName, outcome.Error ?? "unknown"));
        }

        var workingChanged = working is not null && change(working);
        var activeChanged = !string.IsNullOrEmpty(activeId) && written.Any(w => w.ProfileId == activeId);
        _log.Information("Builder.custom_tools: {Verb} {Link}: written={Written} failed={Failed} working={Working} active={Active}",
            verb, link, written.Count, failed.Count, workingChanged, activeChanged);
        return new CustomToolUpdate(written, failed, workingChanged, activeChanged);
    }
}
