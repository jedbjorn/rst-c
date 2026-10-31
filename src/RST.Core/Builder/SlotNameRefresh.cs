// Builder-open repair of old generated labels (SC-030, decision #67). No I/O or source-tab changes.

using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.Core.Builder;

public static class SlotNameRefresh
{
    /// <summary>Refresh exact enum labels in place; return the number changed. Typed labels stay as saved.</summary>
    public static int Apply(Profile profile, IReadOnlyList<ScannedCommand>? catalog)
    {
        var native = new Dictionary<string, ScannedCommand>(StringComparer.Ordinal);
        foreach (var c in catalog ?? [])
            if (c is not null && !string.IsNullOrEmpty(c.Id)
                && c.Origin == CommandOrigin.Native && c.EnumName is not null) native.TryAdd(c.Id, c);
        if (native.Count == 0) return 0;
        var count = 0;
        var tools = profile.Panels.SelectMany(p => p.Slots)
            .Concat(profile.Stacks.Values.SelectMany(s => s.Tools));
        foreach (var tool in tools)
        {
            if (!PanelStacks.IsTool(tool) || CustomTools.IsCustom(tool)
                || tool.CommandId is not { } id || !native.TryGetValue(id, out var command)
                || !string.Equals(tool.Name, command.EnumName, StringComparison.Ordinal)) continue;
            var label = command.Placements?.FirstOrDefault(p =>
                p.Tab == tool.SourceTab && p.Panel == tool.SourcePanel)?.Label ?? command.DisplayName;
            if (tool.Name == label) continue;
            tool.Name = label;
            count++;
        }
        return count;
    }
}
