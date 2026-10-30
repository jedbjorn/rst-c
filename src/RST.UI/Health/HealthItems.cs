// rst-c's own (not vendored): the Health window's small bindable items (spec #16, WU2): the
// Activity stat cards, range pills and chart cards, and the Junk modal's target rows.

using RST.Core.Health;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Health;

/// <summary>An Activity stat card: label, value and an optional sub-line (only the Session Timer has one).</summary>
public sealed class StatCard(string label) : ObservableObject
{
    private string _value = HealthFormat.Missing;
    private string _sub = "";

    public string Label { get; } = label;

    public string Value
    {
        get => _value;
        set => SetField(ref _value, value);
    }

    public string Sub
    {
        get => _sub;
        set => SetField(ref _sub, value);
    }
}

/// <summary>A range pill (7D, 1M, 3M, 6M). Checking it selects its range.</summary>
public sealed class RangeOption : ObservableObject
{
    private readonly Action<RangeOption> _selected;
    private bool _isSelected;

    public RangeOption(int days, string label, bool isSelected, Action<RangeOption> selected)
    {
        Days = days;
        Label = label;
        _isSelected = isSelected;
        _selected = selected;
    }

    public int Days { get; }

    public string Label { get; }

    /// <summary>Two-way bound to the pill. The user checking it reloads the activity; the pills are mutually exclusive.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!SetField(ref _isSelected, value) || !value) return;
            _selected(this);
        }
    }

    /// <summary>Sets the state without selecting (the view-model keeps the others in step).</summary>
    internal void SetSelected(bool value) => SetField(ref _isSelected, value, nameof(IsSelected));
}

/// <summary>An Activity chart card: its title and either a chart, an empty state, or a single dot with a hint under it.</summary>
public sealed class ChartCard(string title) : ObservableObject
{
    private ActivityChartLayout? _chart;
    private string _emptyTitle = "";
    private string _emptyHint = "";

    public string Title { get; } = title;

    /// <summary>The chart, or null when the card shows only its empty state.</summary>
    public ActivityChartLayout? Chart
    {
        get => _chart;
        private set
        {
            if (SetField(ref _chart, value)) OnPropertyChanged(nameof(HasChart));
        }
    }

    public bool HasChart => Chart is not null;

    /// <summary>The empty state's title ("No opens in this range"); empty under a single-dot chart.</summary>
    public string EmptyTitle
    {
        get => _emptyTitle;
        private set
        {
            if (SetField(ref _emptyTitle, value)) OnPropertyChanged(nameof(HasEmptyTitle));
        }
    }

    public bool HasEmptyTitle => EmptyTitle.Length > 0;

    /// <summary>The empty state's hint, or "not enough history yet" under a single-dot chart.</summary>
    public string EmptyHint
    {
        get => _emptyHint;
        private set
        {
            if (SetField(ref _emptyHint, value)) OnPropertyChanged(nameof(HasEmptyHint));
        }
    }

    public bool HasEmptyHint => EmptyHint.Length > 0;

    /// <summary>Shows <paramref name="chart"/> and/or an empty state (<c>chartEmptyHtml(title, hint)</c>).</summary>
    public void Set(ActivityChartLayout? chart, string emptyTitle = "", string emptyHint = "")
    {
        Chart = chart;
        EmptyTitle = emptyTitle;
        EmptyHint = emptyHint;
    }
}

/// <summary>A cleanup target in the Junk modal: name, path hint and its toggle (on by default).</summary>
public sealed class JunkTargetItem(string id, string name, string pathHint) : ObservableObject
{
    private bool _isSelected = true;

    /// <summary>The target's id, or its name when it has none (the HTML's <c>t.id || t.name</c>).</summary>
    public string Id { get; } = id;

    /// <summary>The target's name as configured (may be empty).</summary>
    public string Name { get; } = name;

    /// <summary>The row's label: the name, or "(unnamed)".</summary>
    public string DisplayName => Name.Length > 0 ? Name : UiStrings.Health_Unnamed;

    public string PathHint { get; } = pathHint;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }
}
