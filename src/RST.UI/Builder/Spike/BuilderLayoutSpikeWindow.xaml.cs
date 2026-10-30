// F18 Builder UX Redesign, Phase 1 fidelity spike (spec #19). THROWAWAY: layout only, static sample
// data, no behaviour; it lives on spike/f18-builder-layout and is never merged. The window draws the
// approved mockup (shared/mockups/builder-ux/builder.html v2.2: Profile pane B·Tabs, Tiles, rename and
// duplicate in the profile menu, header Tint) in real WPF so CI's RST-screenshots run can render it.

using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RST.Core.Profiles;
using RST.UI.Controls;
using RST.UI.Theming;
using ProfilePanel = RST.Core.Profiles.Panel;

namespace RST.UI.Builder.Spike;

/// <summary>The states the spike's screenshot test renders.</summary>
public enum SpikeState
{
    Default,
    SettingsMenu,
    ProfileMenu,
    LoaderDefaults,
    JunkCleanup,
    CreateTool,
    NewStack,
}

public partial class BuilderLayoutSpikeWindow : Window
{
    public BuilderLayoutSpikeWindow()
    {
        InitializeComponent();
        DarkTitleBar.Attach(this);
        DataContext = new BuilderLayoutSpikeModel();
        Preview.Profile = BuilderLayoutSpikeModel.SampleProfile();
    }

    /// <summary>Shows one overlay (menu, dialog) or Profile pane tab; <see cref="SpikeState.Default"/> shows none.</summary>
    public void ShowState(SpikeState state)
    {
        SettingsMenu.Visibility = state == SpikeState.SettingsMenu ? Visibility.Visible : Visibility.Collapsed;
        ProfileMenu.Visibility = state == SpikeState.ProfileMenu ? Visibility.Visible : Visibility.Collapsed;
        CreateToolDialog.Visibility = state == SpikeState.CreateTool ? Visibility.Visible : Visibility.Collapsed;
        NewStackDialog.Visibility = state == SpikeState.NewStack ? Visibility.Visible : Visibility.Collapsed;
        ProfileTabs.SelectedIndex = state switch
        {
            SpikeState.LoaderDefaults => 1,
            SpikeState.JunkCleanup => 2,
            _ => 0,
        };
        Pressed(GearButton, state == SpikeState.SettingsMenu);
        Pressed(ProfileButton, state == SpikeState.ProfileMenu);
    }

    // The button whose menu is open keeps the pressed look.
    private void Pressed(System.Windows.Controls.Button button, bool pressed)
    {
        if (pressed) button.Background = (Brush)FindResource("PressedBrush");
        else button.ClearValue(BackgroundProperty);
    }
}

/// <summary>A tool tile or a stack row.</summary>
public sealed class SpikeTool(string name, string? icon, string source, bool isCustom = false, bool showHover = false)
{
    public string Name { get; } = name;

    public string Source { get; } = source;

    public bool IsCustom { get; } = isCustom;

    public bool ShowHover { get; } = showHover;

    public bool HasIcon => Image is not null;

    public BitmapSource? Image { get; } = BuilderLayoutSpikeModel.Icon(icon);
}

/// <summary>A stack group of 2–3 tools.</summary>
public sealed class SpikeStack(params SpikeTool[] tools)
{
    public IReadOnlyList<SpikeTool> Tools { get; } = tools;

    public string Header => $"STACK · {Tools.Count}/3";
}

/// <summary>A panel card.</summary>
public sealed class SpikePanel(string name, string color, bool isSelected, params object[] items)
{
    public string Name { get; } = name;

    public Brush ColorBrush { get; } = Frozen((Color)ColorConverter.ConvertFromString(color));

    public bool IsSelected { get; } = isSelected;

    public IReadOnlyList<object> Items { get; } = items;

    public string ToolCount
    {
        get
        {
            var n = Items.Sum(i => i is SpikeStack s ? s.Tools.Count : 1);
            return n == 1 ? "1 tool" : $"{n} tools";
        }
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>A row of the Custom Tools group.</summary>
public sealed class SpikeCustomTool(string label, string? icon, string kind, bool showHover = false)
{
    public string Label { get; } = label;

    public string Kind { get; } = kind;

    public bool ShowHover { get; } = showHover;

    public BitmapSource? Image { get; } = BuilderLayoutSpikeModel.Icon(icon);
}

/// <summary>A catalogue tree node: a group (with a count) or a command (with its short id).</summary>
public sealed class SpikeNode(string label, int count, bool isExpanded = false, bool isPanel = false, string? shortId = null, params SpikeNode[] children)
{
    public string Label { get; } = label;

    public int Count { get; } = count;

    public bool IsExpanded { get; } = isExpanded;

    public bool IsPanel { get; } = isPanel;

    public bool IsCommand => ShortId is not null;

    public string? ShortId { get; } = shortId;

    public IReadOnlyList<SpikeNode> Children { get; } = children;

    public static SpikeNode Command(string label, string id) => new(label, 0, shortId: id);
}

/// <summary>A labelled switch (Loader defaults, Junk cleanup).</summary>
public sealed record SpikeSwitch(string Label, bool IsOn);

/// <summary>A profile in the profile menu.</summary>
public sealed record SpikeProfileRow(string Name, string Detail, bool IsCurrent)
{
    public string Check => IsCurrent ? "✓" : "";
}

/// <summary>A row in the New stack dialog.</summary>
public sealed class SpikePick(string name, string? icon, int order)
{
    public string Name { get; } = name;

    public bool IsChecked => Order > 0;

    public int Order { get; } = order;

    public string OrderText => $"#{Order}";

    public BitmapSource? Image { get; } = BuilderLayoutSpikeModel.Icon(icon);
}

/// <summary>The spike's static sample data: the mockup's seed profile "Structure".</summary>
public sealed class BuilderLayoutSpikeModel
{
    public static BitmapSource? Icon(string? name) => name is null ? null : PackIcons.ForIconFile("pack:" + name);

    public BitmapSource? LinkIcon { get; } = Icon("link_external_blue");

    public IReadOnlyList<SpikeCustomTool> CustomTools { get; } =
    [
        new("BIM Standards", "sheets_purple", "URL"),
        new("Help", "question", "EMAIL", showHover: true),
        new("Support", "at_green", "EMAIL"),
        new("Wiki", "link_external_blue", "URL"),
    ];

    public IReadOnlyList<SpikeNode> Catalog { get; } =
    [
        new("Autodesk Native", 15),
        new("Enscape", 3),
        new("Kinship", 2),
        new("pyRevit", 4, isExpanded: true, children:
        [
            new("Analysis", 3, isExpanded: true, isPanel: true, children:
            [
                SpikeNode.Command("Warnings", "PYR_WARNINGS"),
                SpikeNode.Command("Audit", "PYR_AUDIT"),
                SpikeNode.Command("Purge", "PYR_PURGE"),
            ]),
            new("Selection", 1, isPanel: true, children: [SpikeNode.Command("Select Similar", "PYR_SELECT_SIMILAR")]),
        ]),
    ];

    public IReadOnlyList<SpikePanel> Panels { get; } =
    [
        new("Frame", "#e05a5a", true,
            new SpikeTool("Beam", "parallel_red", "Structure · Structure"),
            new SpikeTool("Column", "box_stack_dark_grey", "Structure · Structure"),
            new SpikeTool("Brace", "arrow_45_orange", "Structure · Structure")),
        new("Review", "#14b8a6", false,
            new SpikeTool("Kinship", "box_open_blue", "Kinship · Kinship"),
            new SpikeStack(
                new SpikeTool("Warnings", "checklist_orange", "pyRevit · Analysis"),
                new SpikeTool("Audit", "check_green", "pyRevit · Analysis"),
                new SpikeTool("Purge", "filter_red", "pyRevit · Analysis")),
            new SpikeTool("Notes", null, "Annotate · Text")),
        new("Links", "#f0b44c", false,
            new SpikeTool("Wiki", "link_external_blue", "wiki.studio.example", isCustom: true, showHover: true),
            new SpikeTool("Help", "question", "bim@studio.example", isCustom: true)),
    ];

    public IReadOnlyList<SpikeSwitch> HiddenTabs { get; } =
    [
        new("Enscape", false), new("Kinship", false), new("pyRevit", false), new("Analyze", false), new("Collaborate", false),
        new("Systems", false), new("Massing & Site", true), new("Insert", false), new("Steel", false), new("Precast", true),
    ];

    public IReadOnlyList<SpikeSwitch> Cleanup { get; } =
    [
        new("Temp", true), new("PacCache", true), new("Journals", true),
        new("Collaboration Cache", true), new("Recent File List", false), new("RST Logs", true),
    ];

    public IReadOnlyList<SpikeProfileRow> Profiles { get; } =
    [
        new("Structure", "loaded · tab “Studio Struct”", true),
        new("Architecture", "tab “Studio Arch”", false),
        new("Documentation", "tab “Docs”", false),
    ];

    public IReadOnlyList<SpikePick> StackPicks { get; } =
    [
        new("Beam", "parallel_red", 2),
        new("Column", "box_stack_dark_grey", 1),
        new("Brace", "arrow_45_orange", 0),
    ];

    /// <summary>The Live Preview's profile: the cards above, with the Review stack.</summary>
    public static Profile SampleProfile() => new()
    {
        Id = "f18-spike",
        ProfileName = "Structure",
        Tab = "Studio Struct",
        MinVersion = "2.1.0",
        PanelOpacity = 60,
        Panels =
        [
            new ProfilePanel { Name = "Frame", Color = "#e05a5a", Slots = [Tool("Beam", "parallel_red"), Tool("Column", "box_stack_dark_grey"), Tool("Brace", "arrow_45_orange")] },
            new ProfilePanel { Name = "Review", Color = "#14b8a6", Slots = [Tool("Kinship", "box_open_blue"), new Slot { SlotType = "stack", Name = "Stack 1" }, Tool("Notes", null)] },
            new ProfilePanel { Name = "Links", Color = "#f0b44c", Slots = [Tool("Wiki", "link_external_blue"), Tool("Help", "question")] },
        ],
        Stacks = new()
        {
            ["Stack 1"] = new Stack { Tools = [Tool("Warnings", "checklist_orange"), Tool("Audit", "check_green"), Tool("Purge", "filter_red")] },
        },
    };

    private static Slot Tool(string name, string? icon) => new()
    {
        SlotType = "tool",
        Name = name,
        BaseName = name,
        CommandId = "SPIKE_" + name.ToUpperInvariant(),
        IconFile = icon is null ? null : "pack:" + icon,
    };
}
