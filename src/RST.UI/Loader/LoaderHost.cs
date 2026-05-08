// LoaderHost.cs — entry point used by RST.Engine's LoaderCommand to open
// the loader UI without the Engine taking a direct dependency on WPF
// classes at the call site.

using System.Collections.Generic;
using RST.Core.Profiles;
using RST.Core.Scanning;

namespace RST.UI.Loader;

public static class LoaderHost
{
    /// <summary>
    /// Open the loader window modally. Blocks until the user closes it.
    /// Must be called on the Revit UI thread.
    /// </summary>
    /// <param name="revitVersion">Revit major version (e.g. "2026").</param>
    /// <param name="catalog">Pre-built command catalog for the builder's
    /// tool picker. Pass an empty list when running outside a Revit
    /// session — the builder degrades to URL-only / hand-typed slots.</param>
    /// <param name="switchScheduler">Live profile-switch scheduler. The
    /// bridge calls Schedule() after writing active_profile.json so the
    /// ribbon rebuilds in place once the modal closes — no Revit
    /// restart. Pass null to fall back to legacy restart-required
    /// behavior (e.g. when running outside Revit).</param>
    public static void ShowModal(string revitVersion,
                                 IReadOnlyList<ScannedCommand> catalog,
                                 IProfileSwitchScheduler? switchScheduler = null)
    {
        var window = new LoaderWindow(revitVersion, catalog, switchScheduler);
        window.ShowDialog();
    }

    /// <summary>
    /// Open the WebView2 host modally and land directly on the profile
    /// builder page. Same window class as <see cref="ShowModal"/> —
    /// only the initial URL differs. Wired to the dedicated Builder
    /// ribbon button so the user can edit/create profiles without going
    /// through the loader picker first.
    /// </summary>
    public static void ShowModalToBuilder(string revitVersion,
                                          IReadOnlyList<ScannedCommand> catalog,
                                          IProfileSwitchScheduler? switchScheduler = null)
    {
        var window = new LoaderWindow(revitVersion, catalog, switchScheduler, LoaderInitialPage.Builder);
        window.ShowDialog();
    }
}
