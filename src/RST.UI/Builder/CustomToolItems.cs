// rst-c's own (not vendored): the Commands pane's Custom Tools (feature #18, spec #19, WU4;
// decision #20, the approved layout of decision #25). The list is RST.Core's CustomTools.Derive over
// the saved profiles and the open working copy: one row per normalised link, with its icon, label,
// kind tag (URL or EMAIL), ✎ (edit everywhere) and × (remove everywhere) on hover, and the link and
// "Used in: …" as its tooltip. Rows are dragged onto panels like catalogue commands and double-
// clicked to add to the selected panel; each placement is a fresh slot. The group sits first in the
// catalogue tree (one scroll region with it, decision #25) and the Filter… box filters it too.
// The Custom Tool dialog (+ Create new, and ✎ on a row or a placed tool) is one model: the URL / Email
// switch, the label, the address with its live "Opens: …" line, the icon (the icon picker), and for
// an edit the warning that lists every profile that will change ("Update in N profiles", where N is
// what will actually be written: RST.Core's CustomToolEditor.Plan) and the ones that will not.

using System.Collections.ObjectModel;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using RST.Core.Builder;
using RST.Core.Ribbon;
using RST.UI.Controls;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Builder;

/// <summary>
/// The Custom Tools group at the top of the Commands pane: its rows (filtered by the Filter… box),
/// its count, and whether it is expanded (it opens expanded; a filter shows its matches).
/// </summary>
public sealed class CustomToolsGroupViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;
    private List<CustomToolItemViewModel> _all = [];
    private string _signature = "";
    private string _query = "";
    private bool _isExpanded = true;

    internal CustomToolsGroupViewModel(ProfileBuilderViewModel owner)
    {
        _owner = owner;
        ToggleCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    /// <summary>Every tool, by label (the list's order).</summary>
    public IReadOnlyList<CustomToolItemViewModel> All => _all;

    /// <summary>The rows the filter shows.</summary>
    public FilteredCollection<CustomToolItemViewModel> Items { get; } = [];

    /// <summary>How many tools the list holds (the group's pill; added to the Commands count).</summary>
    public int Count => _all.Count;

    public string Note => UiStrings.Builder_CustomToolsNote;

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value)) return;
            OnPropertyChanged(nameof(ShowsRows));
        }
    }

    public ICommand ToggleCommand { get; }

    public bool IsFiltering => _query.Length > 0;

    /// <summary>The rows show while the group is expanded, and always while filtering (as the tree's groups open).</summary>
    public bool ShowsRows => (_isExpanded || IsFiltering) && Items.Count > 0;

    /// <summary>The group shows unless a filter matches none of its tools.</summary>
    public bool IsVisible => !IsFiltering || Items.Count > 0;

    /// <summary>A filter is active and matches at least one tool.</summary>
    public bool HasMatches => IsFiltering && Items.Count > 0;

    /// <summary>The tool with that link, or null.</summary>
    public CustomTool? Find(string link) => _all.FirstOrDefault(i => i.Tool.Link == link)?.Tool;

    /// <summary>A new list: rows are rebuilt only when something they show changed (a name typed on a tile rarely does).</summary>
    internal bool Update(IReadOnlyList<CustomTool> tools)
    {
        var signature = string.Join("\n", tools.Select(t => string.Join("\t", t.Link, t.Label, t.IconFile, t.Kind, string.Join("\u001f", t.UsedIn))));
        if (signature == _signature) return false;
        _signature = signature;
        _all = tools.Select(t => new CustomToolItemViewModel(t, _owner)).ToList();
        Items.Clear();
        Sync();
        OnPropertyChanged(nameof(All));
        OnPropertyChanged(nameof(Count));
        return true;
    }

    /// <summary>The Filter… box: label or link, case-insensitive.</summary>
    internal void ApplyFilter(string? query)
    {
        _query = (query ?? "").Trim();
        Sync();
    }

    private void Sync()
    {
        Items.Sync(_all, i => i.Matches(_query));
        OnPropertyChanged(nameof(IsFiltering));
        OnPropertyChanged(nameof(ShowsRows));
        OnPropertyChanged(nameof(IsVisible));
        OnPropertyChanged(nameof(HasMatches));
    }

    public override string ToString() => UiStrings.Builder_CustomTools;
}

/// <summary>One Custom Tools row: icon, label, kind tag; ✎ and × on hover; the link and "Used in" as its tooltip.</summary>
public sealed class CustomToolItemViewModel : ObservableObject
{
    private readonly ProfileBuilderViewModel _owner;

    internal CustomToolItemViewModel(CustomTool tool, ProfileBuilderViewModel owner)
    {
        Tool = tool;
        _owner = owner;
        EditCommand = new RelayCommand(() => _owner.EditCustomTool(Tool), () => _owner.IsIdle);
        RemoveCommand = new RelayCommand(() => _owner.RemoveCustomTool(Tool), () => _owner.IsIdle);
        AddCommand = new RelayCommand(() => _owner.AddCustomToolToSelectedPanel(Tool), () => _owner.IsIdle);
    }

    public CustomTool Tool { get; }

    public string Label => Tool.Label;

    public string Link => Tool.Link;

    /// <summary>"URL" or "EMAIL".</summary>
    public string KindText => Tool.Kind == CustomToolKind.Email ? UiStrings.Builder_KindEmail : UiStrings.Builder_KindUrl;

    /// <summary>The link, then "Used in: Architecture, Structure".</summary>
    public string ToolTip => Tool.Link + "\n" + UiStrings.Builder_CustomToolUsedIn(string.Join(", ", Tool.UsedIn));

    /// <summary>The 16 px pack icon (the ribbon's default_16.png when it has none, as a stack row).</summary>
    public BitmapSource? Icon => IconPack.TryParseValue(Tool.IconFile, out var value)
        ? PackIcons.Load(value, _owner.PackDirectory, 16) ?? PackIcons.Default16
        : PackIcons.Default16;

    /// <summary>✎: the Custom Tool dialog, filled in; Update applies everywhere.</summary>
    public ICommand EditCommand { get; }

    /// <summary>×: asks first, listing the profiles; then removes it from every one.</summary>
    public ICommand RemoveCommand { get; }

    /// <summary>A double-click: a new placement on the selected panel.</summary>
    public ICommand AddCommand { get; }

    internal bool Matches(string query) => query.Length == 0
        || Tool.Label.Contains(query, StringComparison.OrdinalIgnoreCase)
        || Tool.Link.Contains(query, StringComparison.OrdinalIgnoreCase);

    public override string ToString() => Label;
}

/// <summary>
/// The Custom Tool dialog (+ Create new, and ✎ for an edit everywhere): URL / Email, the label, the
/// address and the link it opens, the icon; Create's note names the panel, Edit's warning names the
/// profiles. Confirm passes only with a label and an address.
/// </summary>
public sealed class CustomToolDialogModel : ObservableObject
{
    private readonly IReadOnlyList<IconPackEntry> _pack;
    private bool _isEmail;
    private string _label;
    private string _address;
    private string? _iconFile;
    private string? _error;

    private CustomToolDialogModel(bool isEdit, bool isEmail, string label, string address, string? iconFile,
                                  IReadOnlyList<IconPackEntry> pack, string? packDirectory)
    {
        IsEdit = isEdit;
        _isEmail = isEmail;
        _label = label;
        _address = address;
        _iconFile = iconFile;
        _pack = pack ?? [];
        PackDirectory = packDirectory;
    }

    /// <summary>+ Create new: a URL tool with the link icon, added to <paramref name="panelName"/>.</summary>
    public static CustomToolDialogModel ForCreate(string panelName, IReadOnlyList<IconPackEntry> pack, string? packDirectory) =>
        new(false, false, "", "", DefaultIcon, pack, packDirectory) { Note = UiStrings.Builder_ToolCreateNote(panelName) };

    /// <summary>✎: the tool filled in, and what the update will do (<paramref name="plan"/>).</summary>
    public static CustomToolDialogModel ForEdit(CustomTool tool, CustomToolPlan plan, IReadOnlyList<IconPackEntry> pack, string? packDirectory)
    {
        ArgumentNullException.ThrowIfNull(tool);
        ArgumentNullException.ThrowIfNull(plan);
        var email = tool.Kind == CustomToolKind.Email;
        return new CustomToolDialogModel(true, email, tool.Label, email ? CustomTools.ShortLink(tool.Link) : tool.Link, tool.IconFile, pack, packDirectory)
        {
            Plan = plan,
            Tool = tool,
            // With nothing to change, the warning says so instead of naming no profile (review F5).
            Warning = plan.Count > 0 ? UiStrings.Builder_ToolEditWarning(string.Join(", ", TargetNames(plan.Changing))) : UiStrings.Builder_ToolNothingToChange,
            Refusals = plan.Refused.Select(RefusalLine).ToList(),
        };
    }

    /// <summary>A new tool's icon: the link icon (as the approved spike).</summary>
    public const string DefaultIcon = "pack:link_external_blue";

    public bool IsEdit { get; }

    /// <summary>The tool being edited (null for Create).</summary>
    public CustomTool? Tool { get; private init; }

    /// <summary>What an edit will do (null for Create).</summary>
    public CustomToolPlan? Plan { get; private init; }

    public string? PackDirectory { get; }

    public string Title => IsEdit ? UiStrings.Builder_EditToolTitle : UiStrings.Builder_CreateToolTitle;

    /// <summary>"Create", or "Update in N profiles" (N: the profiles that will actually change).</summary>
    public string ConfirmText => IsEdit ? UiStrings.Builder_UpdateIn(ProfileCount(Plan!.Count)) : UiStrings.Builder_Create;

    /// <summary>An edit that would change no profile (every holder is refused) cannot be confirmed.</summary>
    public bool CanConfirm => !IsEdit || Plan!.Count > 0;

    /// <summary>The Email side of the URL / Email switch.</summary>
    public bool IsEmail
    {
        get => _isEmail;
        set
        {
            if (!SetField(ref _isEmail, value)) return;
            OnPropertyChanged(nameof(IsUrl));
            OnPropertyChanged(nameof(AddressLabel));
            OnPropertyChanged(nameof(AddressPlaceholder));
            RaiseOpens();
        }
    }

    public bool IsUrl
    {
        get => !_isEmail;
        set => IsEmail = !value;
    }

    public string Label
    {
        get => _label;
        set
        {
            if (SetField(ref _label, value ?? "")) Error = null;
        }
    }

    /// <summary>The address as typed (an email address without its "mailto:").</summary>
    public string Address
    {
        get => _address;
        set
        {
            if (!SetField(ref _address, value ?? "")) return;
            Error = null;
            RaiseOpens();
        }
    }

    public string AddressLabel => _isEmail ? UiStrings.Builder_ToolEmailAddress : UiStrings.Builder_ToolAddress;

    public string AddressPlaceholder => _isEmail ? UiStrings.Builder_ToolEmailPlaceholder : UiStrings.Builder_ToolAddressPlaceholder;

    /// <summary>
    /// The address the tool gets: in Email mode a "mailto:" is added unless typed; in URL mode the
    /// address as typed (RST.Core's rule adds https://, or mailto: for a bare email address).
    /// </summary>
    public string EffectiveAddress
    {
        get
        {
            var a = _address.Trim();
            return _isEmail && a.Length > 0 && !a.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ? "mailto:" + a : a;
        }
    }

    /// <summary>The live "Opens: https://…" line ("" while the address is blank).</summary>
    public string Opens
    {
        get
        {
            var link = CustomTools.Opens(EffectiveAddress);
            return link.Length == 0 ? "" : UiStrings.Builder_ToolOpens(link);
        }
    }

    public bool HasOpens => Opens.Length > 0;

    /// <summary>The icon's profile value ("pack:link_external_blue"), or null for none.</summary>
    public string? IconFile
    {
        get => _iconFile;
        set
        {
            if (!SetField(ref _iconFile, string.IsNullOrEmpty(value) ? null : value)) return;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(IconName));
        }
    }

    public BitmapSource? Icon => IconPack.TryParseValue(_iconFile, out var value) ? PackIcons.Load(value, PackDirectory, 16) : null;

    /// <summary>The icon button's text: "link external blue", or "No icon".</summary>
    public string IconName => IconPack.TryParseValue(_iconFile, out var value)
        ? (value.Color is null ? value.Name : value.Name + "_" + value.Color).Replace('_', ' ')
        : UiStrings.Builder_ToolNoIcon;

    /// <summary>Create's note: the panel it goes to, and that tools live in profiles.</summary>
    public string? Note { get; private init; }

    public bool HasNote => Note is not null;

    /// <summary>Edit's warning: every profile that will change.</summary>
    public string? Warning { get; private init; }

    public bool HasWarning => Warning is not null;

    /// <summary>Edit: the profiles that will not change, and why.</summary>
    public IReadOnlyList<string> Refusals { get; private init; } = [];

    public bool HasRefusals => Refusals.Count > 0;

    /// <summary>Why the last Confirm was refused, or null.</summary>
    public string? Error
    {
        get => _error;
        private set
        {
            if (SetField(ref _error, value)) OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => _error is not null;

    /// <summary>What Confirm passes on: the trimmed label, the effective address, the icon.</summary>
    public CustomToolEdit Result => new(_label.Trim(), EffectiveAddress, _iconFile);

    /// <summary>A picker over the icon pack, starting on the current icon; <paramref name="closed"/> gets its outcome.</summary>
    public IconPickerViewModel CreatePicker(Action<IconPickerOutcome> closed) => new(_pack, _iconFile, PackDirectory, closed);

    /// <summary>Confirm: true with a label and an address; otherwise the refusal shows and the dialog stays open.</summary>
    public bool TryConfirm()
    {
        if (!CanConfirm) return false;
        Error = CustomTools.Check(Result) switch
        {
            null => null,
            "Label required" => UiStrings.Builder_ToolLabelRequired,
            CustomTools.NotAnEmail => UiStrings.Builder_ToolNotAnEmail,
            _ => UiStrings.Builder_ToolAddressRequired,
        };
        return Error is null;
    }

    /// <summary>"1 profile", "3 profiles" (the Custom Tool confirmations and toasts).</summary>
    internal static string ProfileCount(int count) => count == 1 ? UiStrings.Builder_ProfileCountOne : UiStrings.Builder_ProfileCountMany(count);

    /// <summary>
    /// The profiles in a confirmation, in order: each by name (a blank name is "New profile"); a name
    /// two of them share gets its file, so they can be told apart (review F5); the open profile's
    /// working copy, when only it changes, is marked: "not saved yet" with no file, else "only its
    /// unsaved copy changes" (its file shares its id with another and is refused).
    /// </summary>
    internal static IReadOnlyList<string> TargetNames(IEnumerable<CustomToolTarget> targets)
    {
        var list = targets.ToList();
        static string NameOf(CustomToolTarget t) => string.IsNullOrWhiteSpace(t.ProfileName) ? UiStrings.Builder_UnnamedProfile : t.ProfileName;
        var shared = list.GroupBy(NameOf, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return list.Select(t =>
        {
            var name = NameOf(t);
            if (t.State == CustomToolTargetState.WorkingCopy)
                return string.IsNullOrEmpty(t.ProfileId) ? UiStrings.Builder_ToolTargetUnsaved(name) : UiStrings.Builder_ToolTargetOpenOnly(name);
            return shared.Contains(name) && t.FileName.Length > 0 ? UiStrings.Builder_ToolTargetFile(name, t.FileName) : name;
        }).ToList();
    }

    /// <summary>A profile that will not change, and why.</summary>
    internal static string RefusalLine(CustomToolTarget target) => target.State switch
    {
        CustomToolTargetState.NoId => UiStrings.Builder_ToolRefusedNoId(target.ProfileName),
        CustomToolTargetState.SharedId => UiStrings.Builder_ToolRefusedSharedId(target.ProfileName, target.FileName),
        _ => UiStrings.Builder_ToolRefusedUnreadable(target.FileName),
    };

    private void RaiseOpens()
    {
        OnPropertyChanged(nameof(EffectiveAddress));
        OnPropertyChanged(nameof(Opens));
        OnPropertyChanged(nameof(HasOpens));
    }
}
