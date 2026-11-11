// rst-c's own (not vendored): one tool Search-It can find and run (v3.4).

namespace RST.Core.Search;

/// <summary>
/// One searchable tool: a command id, the name the ribbon shows for it, and where it lives. A tool
/// that sits in several places keeps its first (<see cref="Tab"/>, <see cref="Panel"/>) and counts
/// the rest in <see cref="OtherPlaces"/>.
/// </summary>
public sealed record SearchTool(string Id, string DisplayName, string? Tab, string? Panel, int OtherPlaces = 0)
{
    /// <summary>The separator between tab and panel: "Architecture › Build".</summary>
    public const string Separator = " › ";

    /// <summary>Where the tool lives: "Tab › Panel", or whichever of the two is known; empty when neither is.</summary>
    public string Location =>
        string.IsNullOrWhiteSpace(Tab) ? (Panel ?? "").Trim()
        : string.IsNullOrWhiteSpace(Panel) ? Tab!.Trim()
        : Tab!.Trim() + Separator + Panel!.Trim();

    /// <summary>"+2 more places", "+1 more place", or empty when the tool has one home.</summary>
    public string MoreText => OtherPlaces switch
    {
        <= 0 => "",
        1 => "+1 more place",
        _ => $"+{OtherPlaces} more places",
    };
}
