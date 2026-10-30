// rst-c's own (not vendored): the RST tray window's view-model (spec #20 §1; F17 WU1). Four pages: Updates, Apps
// and Add-ins are placeholders in WU1 (WU3 and WU2 fill them); Settings / About is real: the theme, "Check for
// updates automatically" (locked off by the UpdateChecks preset), the version and the log folder. Persistence
// and re-theming go through ITraySettingsHost (TrayHost in the app, a fake in RuntimeTests).

using System.Windows.Input;
using RST.Core.Tray;
using RST.Tray.Resources;
using RST.UI.Mvvm;

namespace RST.Tray.Windows;

/// <summary>The window's pages, in tab order.</summary>
public enum TrayPage
{
    Updates,
    Apps,
    AddIns,
    Settings,
}

/// <summary>What the window needs from the tray.</summary>
internal interface ITraySettingsHost
{
    TrayThemeChoice ThemeChoice { get; }

    UpdateChecksState UpdateChecks { get; }

    /// <summary>The installed RST version shown under About (e.g. 3.1.0).</summary>
    string Version { get; }

    /// <summary>%AppData%\RST\logs.</summary>
    string LogFolder { get; }

    /// <summary>Saves the theme and re-themes the window (it is rebuilt). False when the save failed.</summary>
    bool SetTheme(TrayThemeChoice choice);

    /// <summary>Saves "Check for updates automatically". False when the save failed.</summary>
    bool SetCheckForUpdates(bool on);

    void OpenLogFolder();
}

internal sealed class TrayViewModel : ObservableObject
{
    private readonly ITraySettingsHost _host;
    private TrayPage _page;
    private bool _checkForUpdates;
    private string? _saveError;

    public TrayViewModel(ITraySettingsHost host, TrayPage page)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _page = page;
        _checkForUpdates = host.UpdateChecks.Enabled;
        OpenLogFolderCommand = new RelayCommand(host.OpenLogFolder);
        CloseCommand = new RelayCommand(() => RequestClose?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>The window asks to close (the Close button).</summary>
    public event EventHandler? RequestClose;

    public TrayPage Page
    {
        get => _page;
        set
        {
            if (!SetField(ref _page, value)) return;
            OnPropertyChanged(nameof(PageIndex));
            OnPropertyChanged(nameof(IsUpdatesPage));
            OnPropertyChanged(nameof(IsAppsPage));
            OnPropertyChanged(nameof(IsAddInsPage));
            OnPropertyChanged(nameof(IsSettingsPage));
        }
    }

    /// <summary>The TabControl's selected index.</summary>
    public int PageIndex
    {
        get => (int)_page;
        set
        {
            if (Enum.IsDefined(typeof(TrayPage), value)) Page = (TrayPage)value;
        }
    }

    public bool IsUpdatesPage => _page == TrayPage.Updates;

    public bool IsAppsPage => _page == TrayPage.Apps;

    public bool IsAddInsPage => _page == TrayPage.AddIns;

    public bool IsSettingsPage => _page == TrayPage.Settings;

    public bool ThemeSystem
    {
        get => _host.ThemeChoice == TrayThemeChoice.System;
        set { if (value) SetTheme(TrayThemeChoice.System); }
    }

    public bool ThemeLight
    {
        get => _host.ThemeChoice == TrayThemeChoice.Light;
        set { if (value) SetTheme(TrayThemeChoice.Light); }
    }

    public bool ThemeDark
    {
        get => _host.ThemeChoice == TrayThemeChoice.Dark;
        set { if (value) SetTheme(TrayThemeChoice.Dark); }
    }

    /// <summary>"Check for updates automatically". Always off while the preset locks it.</summary>
    public bool CheckForUpdates
    {
        get => _checkForUpdates;
        set
        {
            if (IsCheckForUpdatesLocked || value == _checkForUpdates) return;
            if (_host.SetCheckForUpdates(value))
            {
                _checkForUpdates = value;
                SaveError = null;
            }
            else
            {
                SaveError = TrayStrings.Settings_SaveFailed;
            }
            OnPropertyChanged();
        }
    }

    public bool IsCheckForUpdatesLocked => _host.UpdateChecks.LockedOff;

    public bool CanChangeCheckForUpdates => !IsCheckForUpdatesLocked;

    /// <summary>A failed save's message under the setting (null: none).</summary>
    public string? SaveError
    {
        get => _saveError;
        private set
        {
            if (SetField(ref _saveError, value)) OnPropertyChanged(nameof(HasSaveError));
        }
    }

    public bool HasSaveError => !string.IsNullOrEmpty(_saveError);

    public string VersionText => TrayStrings.Settings_VersionFormat(_host.Version);

    public string LogFolder => _host.LogFolder;

    public ICommand OpenLogFolderCommand { get; }

    public ICommand CloseCommand { get; }

    private void SetTheme(TrayThemeChoice choice)
    {
        if (choice == _host.ThemeChoice) return;
        SaveError = _host.SetTheme(choice) ? null : TrayStrings.Settings_SaveFailed;
        OnPropertyChanged(nameof(ThemeSystem));
        OnPropertyChanged(nameof(ThemeLight));
        OnPropertyChanged(nameof(ThemeDark));
    }
}
