// rst-c's own (not vendored): the Builder's two Configure modals (spec #16, WU5): RST Presets
// (profile_builder.html 1261-1289, 2640-2696) and Junk Cleanup Targets (1294-1316, 2736-2827), as
// view-models over a copy of the profile's values, behind a seam the view-model tests fake.
// Planner default 4: Cancel on the Cleanup modal reverts its edits (the page edited the profile
// live), and its empty state no longer mentions the nonexistent "+ Add Target" button.

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.UI.Mvvm;

namespace RST.UI.Builder;

/// <summary>The Builder's own modal dialogs.</summary>
public interface IBuilderDialogs
{
    /// <summary>The RST Presets modal: true for Apply.</summary>
    bool EditPresets(PresetsViewModel model);

    /// <summary>The Junk Cleanup Targets modal: true for Apply.</summary>
    bool EditCleanup(CleanupViewModel model);
}

/// <summary>Opens the Builder's modals, owned by the Builder window.</summary>
public sealed class BuilderDialogs : IBuilderDialogs
{
    public Window? Owner { get; set; }

    public bool EditPresets(PresetsViewModel model) => new PresetsWindow(model) { Owner = Owner }.ShowDialog() == true;

    public bool EditCleanup(CleanupViewModel model) => new CleanupWindow(model) { Owner = Owner }.ShowDialog() == true;
}

/// <summary>One RST Presets row: a tab and its hide switch.</summary>
public sealed class PresetTabViewModel(string tab, bool hidden) : ObservableObject
{
    private bool _hidden = hidden;

    public string Tab { get; } = tab;

    public bool IsHidden
    {
        get => _hidden;
        set => SetField(ref _hidden, value);
    }
}

/// <summary>
/// The RST Presets modal: a switch per tab the profile may hide (the ribbon minus the core tabs and
/// the profile's own tab, plus preset tabs that are not on this ribbon), and "Disable Unused Add-ins".
/// </summary>
public sealed class PresetsViewModel : ObservableObject
{
    private bool _disableUnused;

    public PresetsViewModel(IReadOnlyList<string>? allTabs, ProfilePresets? presets, string? profileTab)
    {
        var hidden = new HashSet<string>(presets?.HiddenTabs ?? [], StringComparer.Ordinal);
        Tabs = BuilderModals.PresetTabs(allTabs, presets, profileTab).Select(t => new PresetTabViewModel(t, hidden.Contains(t))).ToList();
        _disableUnused = presets?.DisableUnusedAddins ?? false;
    }

    public IReadOnlyList<PresetTabViewModel> Tabs { get; }

    public bool HasTabs => Tabs.Count > 0;

    public bool DisableUnused
    {
        get => _disableUnused;
        set => SetField(ref _disableUnused, value);
    }

    /// <summary>Apply: the switched-on tabs, in list order, and the flag.</summary>
    public ProfilePresets Result() => BuilderModals.ApplyPresets(Tabs.Select(t => (t.Tab, t.IsHidden)), DisableUnused);
}

/// <summary>One cleanup row: enable switch, editable name, read-only path and kind, ×.</summary>
public sealed class CleanupRowViewModel : ObservableObject
{
    private readonly CleanupViewModel _owner;

    internal CleanupRowViewModel(CleanupTarget target, CleanupViewModel owner)
    {
        Target = target;
        _owner = owner;
        RemoveCommand = new RelayCommand(() => _owner.Remove(this));
    }

    public CleanupTarget Target { get; }

    public bool Enabled
    {
        get => Target.Enabled;
        set
        {
            if (Target.Enabled == value) return;
            Target.Enabled = value;
            OnPropertyChanged();
        }
    }

    public string Name
    {
        get => Target.Name ?? "";
        set
        {
            if (Target.Name == (value ?? "")) return;
            Target.Name = value ?? "";
            OnPropertyChanged();
        }
    }

    /// <summary>The preset path, read only ("Preset path — fixed for safety").</summary>
    public string Path => Target.Path ?? "";

    /// <summary>The kind, read only (<c>target.kind || 'directory'</c>).</summary>
    public string Kind => string.IsNullOrEmpty(Target.Kind) ? CleanupTarget.KindDirectory : Target.Kind;

    public ICommand RemoveCommand { get; }
}

/// <summary>
/// The Junk Cleanup Targets modal over a copy of the profile's targets (the defaults when it has
/// none): toggle, rename, remove, Restore Defaults; Apply drops blank rows; Cancel changes nothing.
/// </summary>
public sealed class CleanupViewModel : ObservableObject
{
    public CleanupViewModel(IReadOnlyList<CleanupTarget>? targets)
    {
        Reset(BuilderModals.CleanupRowsFor(targets));
        RestoreDefaultsCommand = new RelayCommand(() => Reset(CleanupDefaults.Build()));
    }

    public ObservableCollection<CleanupRowViewModel> Rows { get; } = [];

    public bool IsEmpty => Rows.Count == 0;

    public ICommand RestoreDefaultsCommand { get; }

    internal void Remove(CleanupRowViewModel row)
    {
        Rows.Remove(row);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Reset(IEnumerable<CleanupTarget> targets)
    {
        Rows.Clear();
        foreach (var t in targets) Rows.Add(new CleanupRowViewModel(t, this));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>Apply: the rows without the blank ones.</summary>
    public List<CleanupTarget> Result() => BuilderModals.ApplyCleanup(Rows.Select(r => r.Target));
}
