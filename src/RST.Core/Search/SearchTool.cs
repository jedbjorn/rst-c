// rst-c's own (not vendored): one tool Search-It can find and run (v3.4).

namespace RST.Core.Search;

/// <summary>One place a tool sits on the ribbon, and the name the ribbon shows for it there.</summary>
public sealed record SearchPlace(string Label, string? Tab, string? Panel)
{
    /// <summary>The separator between tab and panel: "Architecture › Build".</summary>
    public const string Separator = " › ";

    /// <summary>Where this is: "Tab › Panel", or whichever of the two is known; empty when neither is.</summary>
    public string Location =>
        string.IsNullOrWhiteSpace(Tab) ? (Panel ?? "").Trim()
        : string.IsNullOrWhiteSpace(Panel) ? Tab!.Trim()
        : Tab!.Trim() + Separator + Panel!.Trim();
}

/// <summary>
/// One searchable tool: a command id and every place it sits on the ribbon, in ribbon order. The
/// ribbon can name one command differently in different places ("Tag Room" on Architecture, "Room Tag"
/// on Annotate); every name is searchable, and a result shows the place whose name matched.
/// </summary>
public sealed class SearchTool
{
    public SearchTool(string id, IEnumerable<SearchPlace> places)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        Places = (places ?? throw new ArgumentNullException(nameof(places))).ToArray();
        if (Places.Count == 0) throw new ArgumentException("A tool has at least one place.", nameof(places));
    }

    /// <summary>A tool with one place.</summary>
    public SearchTool(string id, string displayName, string? tab, string? panel)
        : this(id, [new SearchPlace(displayName, tab, panel)])
    {
    }

    public string Id { get; }

    /// <summary>Every place the tool sits, first (primary) first.</summary>
    public IReadOnlyList<SearchPlace> Places { get; }

    /// <summary>The first place: its name is the tool's name when nothing was typed.</summary>
    public SearchPlace Primary => Places[0];

    public string DisplayName => Primary.Label;

    public string Location => Primary.Location;

    /// <summary>How many places the tool has beyond the one a result shows.</summary>
    public int OtherPlaces => Places.Count - 1;
}
