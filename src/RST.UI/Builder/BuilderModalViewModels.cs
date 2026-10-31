// rst-c's own (not vendored): the Builder's dialogs and the Profile pane's rows (feature #18, spec
// #19, WU2). The Rename… and Duplicate… dialogs share one name dialog (a required name, unique among
// the saved profiles, RST.Core ProfileNaming); ⚙ → Branding… holds the machine-wide logo and link
// that the Branding ▾ popover held. Both sit behind a seam the view-model tests fake.
// The RST Presets and Junk Cleanup modals (spec #16, WU5) are retired: their content is inline on the
// Profile pane's Loader defaults and Junk cleanup tabs, whose rows here write straight into the
// profile (an unsaved change, saved with Save) instead of into a copy applied by the modal.

using System.Windows;
using RST.Core.Builder;
using RST.Core.Profiles;
using RST.UI.Dialogs;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Builder;

/// <summary>The Builder's own modal dialogs.</summary>
public interface IBuilderDialogs
{
    /// <summary>The Rename… or Duplicate… dialog: true when the user confirmed a name that passed its check.</summary>
    bool EditName(ProfileNameDialogModel model);

    /// <summary>⚙ → Branding…: the machine-wide logo and link, saved as they change; returns when it closes.</summary>
    void EditBranding(ProfileBuilderViewModel model);

    /// <summary>+ New Stack, or ✎ on a stack (feature #18, WU3): true when the user confirmed the picks.</summary>
    bool EditStack(StackDialogModel model);

    /// <summary>+ Create new, or ✎ on a custom tool (feature #18, WU4): true when the user confirmed a label and an address.</summary>
    bool EditCustomTool(CustomToolDialogModel model);
}

/// <summary>Opens the Builder's dialogs, owned by the Builder window.</summary>
public sealed class BuilderDialogs : IBuilderDialogs
{
    /// <param name="dialogs">
    /// The window's family dialogs. While Branding is open its Pick… file dialog is owned by the
    /// Branding dialog (a file dialog owned by the disabled Builder would re-enable it on close).
    /// </param>
    public BuilderDialogs(DialogService? dialogs = null) => _dialogs = dialogs;

    private readonly DialogService? _dialogs;

    public Window? Owner { get; set; }

    public bool EditName(ProfileNameDialogModel model) => new ProfileNameDialog(model) { Owner = Owner }.ShowDialog() == true;

    public bool EditStack(StackDialogModel model) => new StackDialog(model) { Owner = Owner }.ShowDialog() == true;

    public bool EditCustomTool(CustomToolDialogModel model) => new CustomToolDialog(model) { Owner = Owner }.ShowDialog() == true;

    public void EditBranding(ProfileBuilderViewModel model)
    {
        var dialog = new BrandingDialog(model) { Owner = Owner };
        var previous = _dialogs?.Owner;
        if (_dialogs is not null) _dialogs.Owner = dialog;
        try
        {
            dialog.ShowDialog();
        }
        finally
        {
            if (_dialogs is not null) _dialogs.Owner = previous;
        }
    }
}

/// <summary>
/// The Rename… / Duplicate… dialog: the name box, the help line, and the refusal under the box when
/// the name is missing or another saved profile has it. Confirm checks; only a passing name closes it.
/// </summary>
public sealed class ProfileNameDialogModel : ObservableObject
{
    private readonly Func<string?, NameCheck> _check;
    private string _name;
    private string? _error;

    public ProfileNameDialogModel(string title, string help, string confirmText, string name, Func<string?, NameCheck> check)
    {
        Title = title;
        Help = help;
        ConfirmText = confirmText;
        _name = name ?? "";
        _check = check ?? throw new ArgumentNullException(nameof(check));
    }

    /// <summary>Rename…: the current name; unique among the other saved profiles.</summary>
    public static ProfileNameDialogModel ForRename(string current, Func<string?, NameCheck> check) =>
        new(UiStrings.Builder_RenameTitle, UiStrings.Builder_RenameHelp, UiStrings.Builder_RenameConfirm, current, check);

    /// <summary>Duplicate…: "&lt;name&gt; copy" (or the next free one); unique among every saved profile.</summary>
    public static ProfileNameDialogModel ForDuplicate(string suggested, Func<string?, NameCheck> check) =>
        new(UiStrings.Builder_DuplicateTitle, UiStrings.Builder_DuplicateHelp, UiStrings.Builder_DuplicateConfirm, suggested, check);

    public string Title { get; }

    public string Help { get; }

    public string ConfirmText { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (!SetField(ref _name, value ?? "")) return;
            Error = null; // typing clears the refusal
        }
    }

    /// <summary>Why the last Confirm was refused ("Profile name required.", "A profile named … already exists. …"), or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>The trimmed name, once Confirm has passed.</summary>
    public string Result => _name.Trim();

    /// <summary>Confirm: true when the name passes; otherwise the refusal shows and the dialog stays open.</summary>
    public bool TryConfirm()
    {
        var check = _check(_name);
        Error = check switch
        {
            NameCheck.Missing => UiStrings.Builder_NameRequired,
            NameCheck.Taken => UiStrings.Builder_NameCollision(_name.Trim()),
            _ => null,
        };
        return check == NameCheck.Ok;
    }
}

/// <summary>One "Hide these tabs (RSTify)" row on the Loader defaults tab: a tab and its switch.</summary>
public sealed class PresetTabViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _hidden;

    internal PresetTabViewModel(string tab, bool hidden, Action changed)
    {
        Tab = tab;
        _hidden = hidden;
        _changed = changed;
    }

    public string Tab { get; }

    public bool IsHidden
    {
        get => _hidden;
        set
        {
            if (SetField(ref _hidden, value)) _changed();
        }
    }
}

/// <summary>One Junk cleanup row: the on/off switch, the editable name (the path in its tooltip), ×.</summary>
public sealed class CleanupRowViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;

    internal CleanupRowViewModel(CleanupTarget target, ProfileBuilderViewModel owner)
    {
        Target = target;
        _owner = owner;
        RemoveCommand = new RelayCommand(() => _owner.RemoveCleanupTarget(this), () => _owner.IsIdle);
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
            _owner.ProfileEdited();
        }
    }

    public string Name
    {
        get => Target.Name ?? "";
        set
        {
            if ((Target.Name ?? "") == (value ?? "")) return;
            Target.Name = value ?? "";
            OnPropertyChanged();
            _owner.ProfileEdited();
        }
    }

    /// <summary>The name box's tooltip: the preset path, which is fixed.</summary>
    public string PathTip => UiStrings.Builder_CleanupPathTip(Target.Path ?? "");

    public System.Windows.Input.ICommand RemoveCommand { get; }
}
