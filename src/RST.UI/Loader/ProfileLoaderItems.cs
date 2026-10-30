// rst-c's own (not vendored): the Profile Loader's row models (spec #16, WU3): a profile card,
// an RSTify tab toggle and a required add-in row.

using System.Windows.Input;
using RST.Core.Loader;
using RST.Core.Profiles;
using RST.UI.Mvvm;
using RST.UI.Resources;

namespace RST.UI.Loader;

/// <summary>A profile card: name, "● Loaded" when active, and Tab / Min Revit / Panels.</summary>
public sealed class ProfileCardViewModel : ObservableObject
{
    private Profile _profile;
    private string _fileName;
    private bool _isLoaded;

    public ProfileCardViewModel(string fileName, Profile profile)
    {
        _fileName = fileName;
        _profile = profile;
    }

    public Profile Profile => _profile;

    /// <summary>The file under the profiles folder (<c>_filename</c>), which an import matches on.</summary>
    public string FileName => _fileName;

    public string? Id => _profile.Id;

    public string Name => _profile.ProfileName;

    public string Tab => _profile.Tab;

    public string MinRevit => UiStrings.Loader_MinRevitValue(_profile.MinVersion);

    public string PanelCount => _profile.Panels.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The active profile's card: the success border and the "● Loaded" badge.</summary>
    public bool IsLoaded
    {
        get => _isLoaded;
        set => SetField(ref _isLoaded, value);
    }

    /// <summary>An import that re-installs this profile replaces the card's data in place.</summary>
    public void Replace(string fileName, Profile profile)
    {
        _fileName = fileName;
        _profile = profile;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One "RSTify: Hide These Tabs" row: the tab and whether it is hidden (the switch is on).</summary>
public sealed class TabToggleViewModel : ObservableObject
{
    private bool _isHidden;

    public TabToggleViewModel(string tab, bool isHidden)
    {
        Tab = tab;
        _isHidden = isHidden;
    }

    public string Tab { get; }

    public bool IsHidden
    {
        get => _isHidden;
        set => SetField(ref _isHidden, value);
    }
}

/// <summary>One "Required Add-ins for this profile" row (the Core rules' four-state status).</summary>
public sealed class RequiredAddinRowViewModel
{
    public RequiredAddinRowViewModel(RequiredAddinRow row, ICommand openUrl)
    {
        Row = row;
        OpenUrlCommand = openUrl;
    }

    public RequiredAddinRow Row { get; }

    public string TabName => Row.TabName;

    public string FileColumn => Row.FileColumn;

    public string? InfoUrl => Row.InfoUrl;

    public bool HasInfoUrl => !string.IsNullOrEmpty(Row.InfoUrl);

    public string? Origin => Row.Origin;

    public bool HasOrigin => !string.IsNullOrEmpty(Row.Origin);

    public AddinRowStatus Status => Row.Status;

    public string StatusLabel => Row.StatusLabel;

    public string StatusTooltip => Row.StatusTooltip;

    public ICommand OpenUrlCommand { get; }
}
