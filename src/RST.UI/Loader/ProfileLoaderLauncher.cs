// rst-c's own (not vendored); follows splash-it src/SplashIt.UI/Settings/SettingsLauncher.cs
// (splash-it commit ba86a71, MIT, same owner). The Profile Loader's entry point for RST.Engine's
// LoaderCommand (spec #16, WU3), replacing LoaderHost.ShowModal with the same captured data.

using RST.Core.Configuration;
using RST.Core.Profiles;
using RST.Core.Scanning;
using RST.UI.Dialogs;
using Serilog;

namespace RST.UI.Loader;

public static class ProfileLoaderLauncher
{
    /// <summary>
    /// Shows the Profile Loader modally, owned by <paramref name="owner"/> and centred on it, and
    /// returns when it closes. Call on Revit's UI thread, after RevitThemeDetector.ApplyToWindows().
    /// </summary>
    /// <param name="owner">Revit's main window (<c>UIApplication.MainWindowHandle</c>); zero opens it unowned, centred on the screen.</param>
    /// <param name="revitVersion">Revit's version number (e.g. "2026").</param>
    /// <param name="catalog">The command catalogue LoaderHost.ShowModal took. The Loader does not show it; it is kept so
    /// the command still builds (and caches) the catalogue as before.</param>
    /// <param name="switchScheduler">The live profile switch; null falls back to "restart Revit".</param>
    /// <param name="allTabs">The ribbon's tab titles (RibbonTabEnumerator), for RSTify and the add-in matcher.</param>
    public static void Show(IntPtr owner,
                            string revitVersion,
                            IReadOnlyList<ScannedCommand>? catalog = null,
                            IProfileSwitchScheduler? switchScheduler = null,
                            IReadOnlyList<string>? allTabs = null)
    {
        // As the bridge did when the Loader opened: seed the machine-wide branding logo.
        try { BrandingDefaults.EnsureSeeded(); }
        catch (Exception ex) { Log.Warning(ex, "BrandingDefaults.EnsureSeeded failed (non-fatal)"); }
        Log.Information("ProfileLoader ready: revit={RevitVersion}, catalog={CatalogCount} commands, tabs={TabCount}, liveSwitch={LiveSwitch}",
            revitVersion, catalog?.Count ?? 0, allTabs?.Count ?? 0, switchScheduler is not null);

        var backend = new ProfileLoaderBackend(revitVersion, switchScheduler, allTabs);
        var dialogs = new DialogService();
        var loaderDialogs = new LoaderDialogs();
        var model = new ProfileLoaderViewModel(backend, dialogs, loaderDialogs);
        model.LoadData();
        var window = new ProfileLoaderWindow(model);
        dialogs.Owner = window;
        loaderDialogs.Owner = window;
        WindowPlacement.Own(window, owner);
        window.ShowDialog();
    }
}
