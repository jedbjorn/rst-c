// rst-c's own (not vendored): the Builder's catalogue index and filter (spec #16, WU4), a port of
// profile_builder.html indexCatalog / buildTabEl / renderCatalog (1769-1928). The grouping, order,
// keys, match rule and counts are unchanged:
//   - origin=Native commands go under the "Autodesk Native" group, every other command under its
//     own top-level tab (sourceTab, the host-tab-promoted group);
//   - tabs and panels are sorted (JS Array.prototype.sort: ordinal), commands keep catalogue order;
//   - a missing tab or panel reads "(unknown)";
//   - the filter is a case-insensitive substring match on the display name or the id, and every
//     tab, panel and group shows how many commands it holds that match.
// The JS rebuilt the DOM on every keystroke. Here the tree is built once and a filter only computes
// one count per node (spec #16, Architecture: the filter must not rebuild the tree).

namespace RST.Core.Builder;

public enum CatalogNodeKind
{
    /// <summary>"Autodesk Native": every origin=Native tab.</summary>
    NativeGroup,
    Tab,
    Panel,
    Command,
}

/// <summary>One node of the catalogue tree. Built once by <see cref="CatalogTree.Build"/>.</summary>
public sealed class CatalogNode
{
    private readonly List<CatalogNode> _children = [];

    internal CatalogNode(CatalogNodeKind kind, string label, string key, CatalogCommand? command, int index)
    {
        Kind = kind;
        Label = label;
        Key = key;
        Command = command;
        Index = index;
        if (command is not null)
        {
            NameLower = string.IsNullOrEmpty(command.DisplayName) ? null : command.DisplayName.ToLowerInvariant();
            IdLower = string.IsNullOrEmpty(command.Id) ? null : command.Id.ToLowerInvariant();
        }
    }

    public CatalogNodeKind Kind { get; }

    /// <summary>The group, tab or panel name, or the command's <c>displayName || id</c>.</summary>
    public string Label { get; }

    /// <summary>The collapse key: <c>tab:&lt;tab&gt;</c>, <c>panel:&lt;tab&gt;/&lt;panel&gt;</c>, <c>native</c>, or the command id.</summary>
    public string Key { get; }

    /// <summary>The command (command nodes only).</summary>
    public CatalogCommand? Command { get; }

    /// <summary>A dense index over every node of the tree (for per-filter arrays).</summary>
    public int Index { get; }

    public IReadOnlyList<CatalogNode> Children => _children;

    internal string? NameLower { get; }

    internal string? IdLower { get; }

    internal void Add(CatalogNode child) => _children.Add(child);

    public override string ToString() => Kind + " " + Label;
}

/// <summary>The catalogue tree: the "Autodesk Native" group (when any command is native), then the other tabs.</summary>
public sealed class CatalogTree
{
    public const string NativeGroupLabel = "Autodesk Native";
    public const string NativeGroupKey = "native";
    public const string Unknown = "(unknown)";

    private readonly List<CatalogNode> _roots = [];
    private int _nodes;

    private CatalogTree()
    {
    }

    /// <summary>Top-level nodes: the native group first (if any), then every other tab, sorted.</summary>
    public IReadOnlyList<CatalogNode> Roots => _roots;

    /// <summary>The native group, or null when no command is native.</summary>
    public CatalogNode? NativeGroup { get; private set; }

    /// <summary>The catalogue's size (the "Commands" count: every command, filtered or not).</summary>
    public int CommandCount { get; private set; }

    /// <summary>Every node in the tree (groups, tabs, panels, commands).</summary>
    public int NodeCount => _nodes;

    /// <summary>indexCatalog: buckets the commands by origin, tab and panel.</summary>
    public static CatalogTree Build(IEnumerable<CatalogCommand>? catalog)
    {
        var tree = new CatalogTree();
        var native = new Dictionary<string, Dictionary<string, List<CatalogCommand>>>(StringComparer.Ordinal);
        var other = new Dictionary<string, Dictionary<string, List<CatalogCommand>>>(StringComparer.Ordinal);
        foreach (var c in catalog ?? [])
        {
            if (c is null) continue;
            tree.CommandCount++;
            var bucket = c.Origin == CatalogCommand.NativeOrigin ? native : other;
            var tab = string.IsNullOrEmpty(c.SourceTab) ? Unknown : c.SourceTab;
            var panel = string.IsNullOrEmpty(c.SourcePanel) ? Unknown : c.SourcePanel;
            if (!bucket.TryGetValue(tab, out var panels)) bucket[tab] = panels = new Dictionary<string, List<CatalogCommand>>(StringComparer.Ordinal);
            if (!panels.TryGetValue(panel, out var commands)) panels[panel] = commands = [];
            commands.Add(c);
        }

        if (native.Count > 0)
        {
            var group = tree.Node(CatalogNodeKind.NativeGroup, NativeGroupLabel, NativeGroupKey, null);
            foreach (var tab in native.Keys.OrderBy(k => k, StringComparer.Ordinal))
                group.Add(tree.TabNode(tab, native[tab]));
            tree.NativeGroup = group;
            tree._roots.Add(group);
        }
        foreach (var tab in other.Keys.OrderBy(k => k, StringComparer.Ordinal))
            tree._roots.Add(tree.TabNode(tab, other[tab]));
        return tree;
    }

    /// <summary>The filter box's text as the JS read it: lower-cased, then trimmed.</summary>
    public static string NormalizeQuery(string? text) => (text ?? "").ToLowerInvariant().Trim();

    /// <summary>The match rule: the display name or the id contains <paramref name="query"/> (both lower case).</summary>
    public static bool Matches(CatalogCommand command, string query)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrEmpty(query)) return true;
        return (!string.IsNullOrEmpty(command.DisplayName) && command.DisplayName.ToLowerInvariant().Contains(query, StringComparison.Ordinal))
            || (!string.IsNullOrEmpty(command.Id) && command.Id.ToLowerInvariant().Contains(query, StringComparison.Ordinal));
    }

    /// <summary>Applies the filter box's text: one match count per node, nothing rebuilt.</summary>
    public CatalogFilter Filter(string? text)
    {
        var query = NormalizeQuery(text);
        var counts = new int[_nodes];
        var any = false;
        foreach (var root in _roots) any |= Count(root, query, counts) > 0;
        return new CatalogFilter(query, counts, any);
    }

    private static int Count(CatalogNode node, string query, int[] counts)
    {
        int n;
        if (node.Kind == CatalogNodeKind.Command)
        {
            n = query.Length == 0
                || (node.NameLower is not null && node.NameLower.Contains(query, StringComparison.Ordinal))
                || (node.IdLower is not null && node.IdLower.Contains(query, StringComparison.Ordinal)) ? 1 : 0;
        }
        else
        {
            n = 0;
            foreach (var child in node.Children) n += Count(child, query, counts);
        }
        counts[node.Index] = n;
        return n;
    }

    private CatalogNode TabNode(string tab, Dictionary<string, List<CatalogCommand>> panels)
    {
        var tabNode = Node(CatalogNodeKind.Tab, tab, "tab:" + tab, null);
        foreach (var panel in panels.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var panelNode = Node(CatalogNodeKind.Panel, panel, "panel:" + tab + "/" + panel, null);
            foreach (var c in panels[panel]) panelNode.Add(Node(CatalogNodeKind.Command, c.Label, c.Id, c));
            tabNode.Add(panelNode);
        }
        return tabNode;
    }

    private CatalogNode Node(CatalogNodeKind kind, string label, string key, CatalogCommand? command) =>
        new(kind, label, key, command, _nodes++);
}

/// <summary>One filter's result: the (normalized) query and how many matching commands each node holds.</summary>
public sealed class CatalogFilter
{
    private readonly int[] _counts;

    internal CatalogFilter(string query, int[] counts, bool anyMatch)
    {
        Query = query;
        _counts = counts;
        AnyMatch = anyMatch;
    }

    /// <summary>The lower-cased, trimmed query ("" when the box is empty).</summary>
    public string Query { get; }

    /// <summary>True while the box holds a query (the native group then opens by itself).</summary>
    public bool IsActive => Query.Length > 0;

    /// <summary>True when any command matches.</summary>
    public bool AnyMatch { get; }

    /// <summary>The node's match count (1 or 0 for a command).</summary>
    public int Count(CatalogNode node) => _counts[node.Index];

    /// <summary>A node with no match is left out of the tree (buildTabEl returned null; the panel was skipped).</summary>
    public bool IsVisible(CatalogNode node) => _counts[node.Index] > 0;
}
