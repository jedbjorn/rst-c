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

    /// <summary>The census's label for this tool in the open file and view (spec #35); None when it has none.</summary>
    public SearchToolMark Mark { get; init; }

    /// <summary>For <see cref="SearchToolMark.MustRunIn"/>: the views of this file where it runs, in the name table's order.</summary>
    public IReadOnlyList<string> MustRunIn { get; init; } = Array.Empty<string>();

    /// <summary>False for a <see cref="SearchToolMark.MustRunIn"/> tool: Search-It lists it but never runs it.</summary>
    public bool CanRun => Mark != SearchToolMark.MustRunIn;

    /// <summary>Revit lets add-ins post the id (decision #63): it runs through PostCommand; otherwise through route A (#92).</summary>
    public bool Postable { get; init; } = true;

    /// <summary>
    /// The host's handle of the ribbon item a click runs through route A: the tool's first showing place on a visible
    /// tab, drawn if any. Null when the host has none (fixtures; a list made without the ribbon).
    /// </summary>
    public int? RunHandle { get; init; }

    /// <summary>
    /// Every place a click may run the tool from (decision #127): each live copy of each of its catalogue places, drawn
    /// copies first, then the rest, in catalogue order. The click picks the first one showing at that moment
    /// (<see cref="SearchClick"/>). Empty for a Must Run In tool.
    /// </summary>
    public IReadOnlyList<SearchRunPlace> RunPlaces { get; init; } = Array.Empty<SearchRunPlace>();

    /// <summary>The list was a family's: a project tab's place is then hidden by Revit, not by the user.</summary>
    public bool InFamily { get; init; }
}

/// <summary>One live item a click may run a tool from.</summary>
/// <param name="Handle">The host's handle of the ribbon item.</param>
/// <param name="Drawn">Not marked not-drawn by the census here (always true without a census).</param>
/// <param name="OnFamilyEditorTab">On a family-editor tab (never a tab the user hides).</param>
public readonly record struct SearchRunPlace(int Handle, bool Drawn, bool OnFamilyEditorTab);

/// <summary>What the census says about a listed tool in the open file and view (spec #35, "Runnable Only").</summary>
public enum SearchToolMark
{
    /// <summary>Runs here from a drawn place, or the census has nothing to say: listed as before; a click runs it.</summary>
    None,
    /// <summary>Runs here, but only from places Revit models and does not draw: the amber NOT IN GUI badge; a click runs it.</summary>
    NotInGui,
    /// <summary>Does not run here but runs in another view of this file: listed, dimmed, never run from Search-It.</summary>
    MustRunIn,
}
