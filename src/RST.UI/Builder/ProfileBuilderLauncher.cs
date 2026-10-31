// rst-c's own (not vendored); follows splash-it src/SplashIt.UI/Settings/SettingsLauncher.cs
// (splash-it commit ba86a71, MIT, same owner). The WPF Profile Builder's entry point (spec #16),
// taking the same captured data LoaderHost.ShowModalToBuilder took. RST.Engine's BuilderCommand
// calls it (WU5).

using RST.Core.Configuration;
using RST.Core.Profiles;
using RST.Core.Scanning;
using RST.UI.Dialogs;
using RST.Core.Logging;

namespace RST.UI.Builder;

public static class ProfileBuilderLauncher
{
    /// <summary>
    /// Shows the Profile Builder modally, owned by <paramref name="owner"/> and centred on it, and
    /// returns when it closes. Call on Revit's UI thread, after RevitThemeDetector.ApplyToWindows().
    /// Returns false when <see cref="WindowGuard"/> closed it after an unhandled UI exception (logged).
    /// </summary>
    /// <param name="owner">Revit's main window (<c>UIApplication.MainWindowHandle</c>); zero opens it unowned, centred on the screen.</param>
    /// <param name="revitVersion">Revit's version number (e.g. "2026").</param>
    /// <param name="catalog">The command catalogue scanned from Revit.</param>
    /// <param name="switchScheduler">The live profile switch (the auto-reload after saving the active profile).</param>
    /// <param name="allTabs">The ribbon's tab titles.</param>
    public static bool Show(IntPtr owner,
                            string revitVersion,
                            IReadOnlyList<ScannedCommand>? catalog = null,
                            IProfileSwitchScheduler? switchScheduler = null,
                            IReadOnlyList<string>? allTabs = null)
    {
        // As the bridge did when the Builder opened: seed the machine-wide branding logo.
        try { BrandingDefaults.EnsureSeeded(); }
        catch (Exception ex) { Log.Warning(ex, "BrandingDefaults.EnsureSeeded failed (non-fatal)"); }
        Log.Information("ProfileBuilder ready: revit={RevitVersion}, catalog={CatalogCount} commands, tabs={TabCount}, liveSwitch={LiveSwitch}",
            revitVersion, catalog?.Count ?? 0, allTabs?.Count ?? 0, switchScheduler is not null);

        var backend = new ProfileBuilderBackend(revitVersion, catalog, switchScheduler, allTabs);
        var dialogs = new DialogService { EscapeCancels = true, ReturnsFocus = true }; // decision #62; rc.3 VM finding F2
        var builderDialogs = new BuilderDialogs(dialogs);
        var model = new ProfileBuilderViewModel(backend, dialogs, builderDialogs: builderDialogs);
        model.Initialize();
        var window = new ProfileBuilderWindow(model);
        dialogs.Owner = window;
        builderDialogs.Owner = window;
        WindowPlacement.Own(window, owner);
        return WindowGuard.ShowDialog(window, "Builder");
    }
}
