// LoaderHost.cs — entry point used by RST.Engine's BuilderCommand to open
// the WebView2 Profile Builder without the Engine taking a direct dependency
// on WPF classes at the call site. The Profile Loader opens through
// ProfileLoaderLauncher (native WPF, spec #16 WU3).

using System.Collections.Generic;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.UI.Loader;

public static class LoaderHost
{
    /// <summary>
    /// Open the WebView2 host modally on the profile builder page. Wired
    /// to the dedicated Builder ribbon button so the user can edit/create
    /// profiles. Blocks until the user closes it; call on the Revit UI
    /// thread.
    /// </summary>
    /// <param name="revitVersion">Revit major version (e.g. "2026").</param>
    /// <param name="catalog">Pre-built command catalog for the builder's
    /// tool picker. Pass an empty list when running outside a Revit
    /// session — the builder degrades to URL-only / hand-typed slots.</param>
    /// <param name="switchScheduler">Live profile-switch scheduler, used
    /// by the Builder's auto-reload of the active profile after a save.
    /// Pass null to fall back to restart-required behavior.</param>
    /// <param name="allTabs">Snapshot of distinct non-contextual ribbon
    /// tab titles (RST.Engine.Scanning.RibbonTabEnumerator), so this
    /// project stays independent of AdWindows.</param>
    public static void ShowModalToBuilder(string revitVersion,
                                          IReadOnlyList<ScannedCommand> catalog,
                                          IProfileSwitchScheduler? switchScheduler = null,
                                          IReadOnlyList<string>? allTabs = null)
    {
        var window = new LoaderWindow(revitVersion, catalog, switchScheduler, allTabs);
        window.ShowDialog();
    }
}
