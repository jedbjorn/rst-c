// rst-c's own (not vendored): what the Health → Models list binds (spec #25 Models; F19 #25 WU3). The list is one
// virtualizing column of rows (ModelsView): the aggregate cards, then per group a header row and its card rows, each
// card row holding one model card, so 500 models realize only the rows on
// screen. The text comes from TrayStrings; the numbers from RST.Core's
// ModelList (WU1) and HealthFormat.

using System.Globalization;
using System.Windows.Media;
using RST.Core.Health;
using RST.Core.Tray;
using RST.Tray.Resources;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

/// <summary>A pill in a segmented choice (a time chip, a Group by option). Checking it selects its value.</summary>
internal sealed class PillOption<T> : ObservableObject
{
    private readonly Action<T> _selected;
    private bool _isSelected;

    public PillOption(T value, string label, string automationId, string? tip, bool isSelected, Action<T> selected)
    {
        Value = value;
        Label = label;
        AutomationId = automationId;
        Tip = tip;
        _isSelected = isSelected;
        _selected = selected;
    }

    public T Value { get; }

    public string Label { get; }

    public string AutomationId { get; }

    public string? Tip { get; }

    /// <summary>Two-way bound to the pill (a RadioButton); the user checking it selects its value.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetField(ref _isSelected, value) || !value) return;
            UiGuard.Run("Models: choose " + AutomationId, () => _selected(Value));
        }
    }

    /// <summary>Sets the state without selecting (the view-model keeps the others in step).</summary>
    internal void SetSelected(bool value) => SetField(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>A choice in the Revit version filter: All, or one major version. Several majors may be checked.</summary>
internal sealed class VersionOption : ObservableObject
{
    private readonly Action<VersionOption, bool> _changed;
    private bool _isChecked;

    public VersionOption(string? version, bool isChecked, Action<VersionOption, bool> changed)
    {
        Version = version;
        _isChecked = isChecked;
        _changed = changed;
    }

    /// <summary>The major version ("2026"); null for All.</summary>
    public string? Version { get; }

    public bool IsAll => Version is null;

    public string Label => Version ?? TrayStrings.Models_VersionAll;

    public string AutomationId => "Tray.Health.Models.Version." + (Version ?? "All");

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetField(ref _isChecked, value)) return;
            UiGuard.Run("Models: version filter", () => _changed(this, value));
        }
    }

    internal void SetChecked(bool value) => SetField(ref _isChecked, value, nameof(IsChecked));
}

/// <summary>One row of the virtualized list.</summary>
internal abstract class ModelsRow : ObservableObject
{
}

/// <summary>The aggregate cards over the visible cards (spec #25 Aggregate cards).</summary>
internal sealed class ModelsAggregatesRow(IReadOnlyList<RST.UI.Health.StatCard> cards) : ModelsRow
{
    public IReadOnlyList<RST.UI.Health.StatCard> Cards { get; } = cards;
}

/// <summary>A group's header (SectionExpander's header): the version, the model count and the active time.</summary>
internal sealed class ModelsGroupHeaderRow : ModelsRow
{
    private readonly Action<ModelsGroupHeaderRow> _toggled;
    private bool _isExpanded;

    public ModelsGroupHeaderRow(ModelGroup group, bool isExpanded, Action<ModelsGroupHeaderRow> toggled)
    {
        Group = group;
        _isExpanded = isExpanded;
        _toggled = toggled;
        Title = ModelsText.GroupTitle(group);
        Count = ModelsText.ModelCount(group.Count);
        Active = TrayStrings.Models_ActiveFormat(HealthFormat.Dur(group.ActiveSeconds));
    }

    public ModelGroup Group { get; }

    public string Key => Group.Key;

    public string Title { get; }

    public string Count { get; }

    public string Active { get; }

    /// <summary>The header and the screen reader's name: "2026, 12 models, 4h 30m active".</summary>
    public string AutomationName => Title + ", " + Count + ", " + Active;

    /// <summary>Two-way bound to the header's toggle; collapsing hides the group's card rows (kept across reloads).</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(IsClosedBox));
            UiGuard.Run("Models: expand a group", () => _toggled(this));
        }
    }

    /// <summary>Collapsed, or open with no cards: the header closes its box itself.</summary>
    public bool IsClosedBox => !_isExpanded;
}

/// <summary>A row of cards. In a group it draws the sides of the group's box (the last row its bottom); flat, none.</summary>
internal sealed class ModelsCardRow(IReadOnlyList<ModelCardItem> cards, bool inGroup, bool isLast) : ModelsRow
{
    public IReadOnlyList<ModelCardItem> Cards { get; } = cards;

    public bool InGroup { get; } = inGroup;

    public bool IsLastInGroup { get; } = isLast;
}

/// <summary>One model card.</summary>
internal sealed class ModelCardItem : ObservableObject
{
    private ImageSource? _thumbnail;

    public ModelCardItem(ModelCard card)
    {
        Card = card;
        Version = card.LatestVersion is { } v ? TrayStrings.Models_CardVersion(v) : TrayStrings.Models_CardNoVersion;
        LastActive = card.LastActiveDay is { } d ? TrayStrings.Models_CardLastActive(ModelsText.Date(d)) : "";
        Active = TrayStrings.Models_ActiveFormat(HealthFormat.Dur(card.ActiveSeconds));
        HealthName = ModelsText.HealthName(card.Health);
        Marker = card.Marker switch
        {
            ModelMarker.Cloud => TrayStrings.Models_Cloud,
            ModelMarker.Workshared => TrayStrings.Models_Workshared,
            _ => null,
        };
    }

    public ModelCard Card { get; }

    public string ModelId => Card.ModelId;

    public string Title => Card.Title;

    public string? FileName => Card.ShowFileName ? Card.FileName : null;

    public bool ShowFileName => Card.ShowFileName && !string.IsNullOrEmpty(Card.FileName);

    /// <summary>The full title and path, for the tooltip.</summary>
    public string Tip => string.Join(Environment.NewLine,
        new[] { Card.Title, Card.LocalPath, Card.CentralPath }.Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase));

    /// <summary>The health dot's severity (Health's colours; None is the grey "Not enough data"). Named Dot for Health's SeverityDot style.</summary>
    public HealthSeverity Dot => Card.HealthSeverity;

    public string HealthName { get; }

    public string Version { get; }

    public string LastActive { get; }

    public string Active { get; }

    /// <summary>"Workshared" or "Cloud"; null for neither.</summary>
    public string? Marker { get; }

    public bool HasMarker => Marker is not null;

    /// <summary>"Title, Green, Revit 2026.4, Last active Oct 28, 2026, 3h 20m active".</summary>
    public string AutomationName => string.Join(", ",
        new[] { Title, FileName, HealthName, Version, LastActive, Active, Marker }.Where(s => !string.IsNullOrEmpty(s)));

    /// <summary>The Windows thumbnail; null shows the placeholder.</summary>
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (!SetField(ref _thumbnail, value)) return;
            OnPropertyChanged(nameof(HasThumbnail));
        }
    }

    public bool HasThumbnail => _thumbnail is not null;
}

/// <summary>The detail the list asks for (spec #25 Model Detail; WU4 shows it).</summary>
internal sealed class ModelDetailRequestedEventArgs(ModelCard card, ModelDetailRange startRange) : EventArgs
{
    public ModelCard Card { get; } = card;

    public string ModelId => Card.ModelId;

    /// <summary>The range the detail opens on: the shortest pill covering the list's chip (Recent → 1M), 1Y while searching.</summary>
    public ModelDetailRange StartRange { get; } = startRange;
}

/// <summary>The list's words and numbers.</summary>
internal static class ModelsText
{
    public static string ModelCount(int n) => n == 1 ? TrayStrings.Models_CountOne : TrayStrings.Models_CountMany(n);

    public static string GroupTitle(ModelGroup group)
    {
        if (group.IsUnknownVersion) return TrayStrings.Models_UnknownVersion;
        if (group.IsUnknownMinor) return TrayStrings.Models_UnknownMinor(group.Major ?? "");
        return TrayStrings.Models_GroupVersion(group.Version ?? "");
    }

    public static string HealthName(ModelHealthColour colour) => colour switch
    {
        ModelHealthColour.Green => TrayStrings.Models_HealthGreen,
        ModelHealthColour.Yellow => TrayStrings.Models_HealthYellow,
        ModelHealthColour.Amber => TrayStrings.Models_HealthAmber,
        ModelHealthColour.Red => TrayStrings.Models_HealthRed,
        _ => TrayStrings.Models_HealthGrey,
    };

    /// <summary>"Oct 28, 2026" (the tray is English only).</summary>
    public static string Date(DateOnly day) => day.ToString("MMM d, yyyy", CultureInfo.InvariantCulture);

    public static string Count(long n) => n.ToString("N0", CultureInfo.InvariantCulture);
}
